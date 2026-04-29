using System.Collections.Concurrent;
using Sandbox.Network;

namespace sGBA;

public sealed class NetworkManager : Component, IWirelessNetwork, Component.INetworkListener
{
	public enum SessionState
	{
		Solo,
		Hosting,
		Joined,
		InGame
	}

	public const int MaxPlayers = 5;
	private const int BroadcastClientId = 0xFFFF;

	public static NetworkManager Current { get; private set; }

	public SessionState State { get; private set; } = SessionState.Solo;
	public SessionVisibility Visibility { get; private set; } = SessionVisibility.Public;
	public string RomTitle { get; private set; }
	public string RomSha1 { get; private set; }
	public string RomGameCode { get; private set; }

	public event Action<string> JoinFailed;

	public IReadOnlyList<SessionPlayer> Players => GetLivePlayers();
	public SessionPlayer LocalPlayer => GetLivePlayers().FirstOrDefault( p => p.IsLocal );

	public bool IsActive => State != SessionState.Solo;
	public bool IsHost => Networking.IsHost && IsActive;
	public bool IsClient => IsActive && !Networking.IsHost;
	public bool AllReady
	{
		get
		{
			var ps = GetLivePlayers();
			return ps.Count > 1 && ps.All( p => p.Ready || p.IsHost );
		}
	}
	public bool CanStart => IsHost && AllReady && State == SessionState.Hosting;

	private readonly Dictionary<Guid, bool> _ready = [];
	private readonly List<SessionPlayer> _scratch = new( MaxPlayers );

	private EmulatorComponent _emulator;
	private GbaWirelessAdapter Adapter => _emulator?.Core?.Io?.WirelessAdapter;
	private GbaWirelessAdapter _wiredAdapter;
	private readonly ConcurrentQueue<(int ClientId, byte[] Payload)> _outbox = new();

	protected override void OnAwake()
	{
		Current = this;
	}

	protected override void OnStart()
	{
		_emulator = Scene.GetAllComponents<EmulatorComponent>().FirstOrDefault();
		TryWireAdapter();
	}

	protected override void OnUpdate()
	{
		var adapter = Adapter;
		if ( adapter is not null && !ReferenceEquals( adapter, _wiredAdapter ) )
		{
			adapter.Network = this;
			_wiredAdapter = adapter;
		}

		while ( _outbox.TryDequeue( out var pkt ) )
			DispatchSend( pkt.ClientId, pkt.Payload );
	}

	protected override void OnDestroy()
	{
		if ( Current == this )
			Current = null;
	}

	public bool Host( RomEntry rom, SessionVisibility visibility )
	{
		if ( IsActive )
			return false;
		if ( rom is null )
			return false;

		Visibility = visibility;
		RomTitle = rom.DisplayTitle;
		RomGameCode = rom.GameCode;
		RomSha1 = ComputeRomSha1( rom );

		var config = new LobbyConfig
		{
			MaxPlayers = MaxPlayers,
			Name = $"{rom.DisplayTitle}",
			Privacy = ToLobbyPrivacy( visibility ),
			Hidden = visibility == SessionVisibility.InviteOnly,
			DestroyWhenHostLeaves = true,
			AutoSwitchToBestHost = false
		};

		Networking.CreateLobby( config );
		State = SessionState.Hosting;

		Networking.SetData( LobbyDataKeys.RomTitle, RomTitle ?? string.Empty );
		Networking.SetData( LobbyDataKeys.GameCode, RomGameCode ?? string.Empty );
		Networking.SetData( LobbyDataKeys.RomSha1, RomSha1 ?? string.Empty );
		Networking.SetData( LobbyDataKeys.Visibility, ((int)Visibility).ToString() );
		Networking.SetData( LobbyDataKeys.HostName, Connection.Local?.DisplayName ?? string.Empty );

		return true;
	}

	public bool Join( ulong lobbyId )
	{
		if ( IsActive )
			return false;

		Networking.Connect( lobbyId );
		State = SessionState.Joined;
		return true;
	}

	public void Leave()
	{
		if ( !IsActive )
			return;

		Networking.Disconnect();
		_ready.Clear();
		State = SessionState.Solo;
		RomTitle = null;
		RomSha1 = null;
		RomGameCode = null;
	}

	public void SetReady( bool ready )
	{
		if ( !IsActive )
			return;
		if ( IsHost )
			return;

		RpcSetReady( ready );
	}

	public void StartGame()
	{
		if ( !CanStart )
			return;
		RpcBeginGame();
	}

	public void OnActive( Connection conn )
	{
		if ( !Networking.IsHost && State == SessionState.Solo )
			State = SessionState.Joined;

		if ( IsHost && conn is not null && conn != Connection.Local )
		{
			using ( Rpc.FilterInclude( conn ) )
				RpcSyncSession( RomTitle ?? string.Empty, RomSha1 ?? string.Empty, RomGameCode ?? string.Empty, (int)Visibility );
		}
	}

	public void OnDisconnected( Connection conn )
	{
		if ( conn is not null )
			_ready.Remove( conn.Id );
	}

	private List<SessionPlayer> GetLivePlayers()
	{
		_scratch.Clear();
		var all = Connection.All;
		if ( all is null )
			return _scratch;

		var ordered = all
			.Where( c => c is not null && c.IsActive && !string.IsNullOrEmpty( c.DisplayName ) )
			.OrderByDescending( c => c.IsHost )
			.ThenBy( c => c.Id )
			.Take( MaxPlayers );

		int slot = 0;
		foreach ( var c in ordered )
		{
			var ready = c.IsHost || _ready.GetValueOrDefault( c.Id );
			_scratch.Add( new SessionPlayer( c, slot++ ) { Ready = ready } );
		}
		return _scratch;
	}

	[Rpc.Host( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RpcSetReady( bool ready )
	{
		var caller = Rpc.Caller;
		if ( caller is null )
			return;

		_ready[caller.Id] = ready;
		RpcBroadcastReady( caller.Id, ready );
	}

	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RpcBroadcastReady( Guid playerId, bool ready )
	{
		_ready[playerId] = ready;
	}

	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RpcSyncSession( string romTitle, string romSha1, string gameCode, int visibility )
	{
		if ( Networking.IsHost )
			return;

		RomTitle = romTitle;
		RomSha1 = romSha1;
		RomGameCode = gameCode;
		Visibility = (SessionVisibility)visibility;
		State = SessionState.Joined;

		TryLoadJoinedRom();
	}

	private void TryLoadJoinedRom()
	{
		var match = FindLocalRomMatch( RomSha1, RomGameCode );
		if ( match is null )
		{
			Log.Warning( $"[sGBA] Join: no matching ROM for '{RomTitle}' (code {RomGameCode}); requesting kick." );
			RpcRequestMissingRomKick( RomTitle ?? string.Empty );
			return;
		}

		EmulatorComponent.Current?.Restart( match.Path );
		LibraryPanel.Current?.Hide();
	}

	[Rpc.Host( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RpcRequestMissingRomKick( string romTitle )
	{
		var caller = Rpc.Caller;
		if ( caller is null || caller == Connection.Local )
			return;

		caller.Kick( $"Missing ROM for '{romTitle}'." );
	}

	private static RomEntry FindLocalRomMatch( string sha1, string gameCode )
	{
		List<RomEntry> roms;
		try { roms = RomLibrary.Discover(); }
		catch ( Exception e ) { Log.Warning( $"[sGBA] RomLibrary.Discover failed: {e.Message}" ); return null; }

		if ( !string.IsNullOrEmpty( sha1 ) )
		{
			foreach ( var r in roms )
			{
				if ( string.Equals( ComputeRomSha1( r ), sha1, StringComparison.OrdinalIgnoreCase ) )
					return r;
			}
		}

		if ( !string.IsNullOrEmpty( gameCode ) )
		{
			return roms.FirstOrDefault( r => string.Equals( r.GameCode, gameCode, StringComparison.OrdinalIgnoreCase ) );
		}

		return null;
	}

	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RpcBeginGame()
	{
		State = SessionState.InGame;
	}

	private static LobbyPrivacy ToLobbyPrivacy( SessionVisibility v ) => v switch
	{
		SessionVisibility.Public => LobbyPrivacy.Public,
		SessionVisibility.FriendsOnly => LobbyPrivacy.FriendsOnly,
		SessionVisibility.InviteOnly => LobbyPrivacy.Private,
		_ => LobbyPrivacy.Public
	};

	private static string ComputeRomSha1( RomEntry rom )
	{
		try
		{
			var bytes = rom.FileSystem.ReadAllBytes( rom.Path ).ToArray();
			return Convert.ToHexString( System.Security.Cryptography.SHA1.HashData( bytes ) );
		}
		catch
		{
			return string.Empty;
		}
	}

	private void TryWireAdapter()
	{
		var adapter = Adapter;
		if ( adapter is null )
			return;
		adapter.Network = this;
		_wiredAdapter = adapter;
	}

	void IWirelessNetwork.Send( int clientId, byte[] data, int length )
	{
		if ( data is null || length <= 0 )
			return;

		var payload = new byte[length];
		Buffer.BlockCopy( data, 0, payload, 0, length );
		_outbox.Enqueue( (clientId, payload) );
	}

	private void DispatchSend( int clientId, byte[] payload )
	{
		if ( clientId == BroadcastClientId )
		{
			RpcReceive( payload );
			return;
		}

		var players = GetLivePlayers();
		if ( clientId < 0 || clientId >= players.Count )
			return;

		var target = players[clientId].Connection;
		if ( target is null || target == Connection.Local )
			return;

		using ( Rpc.FilterInclude( target ) )
			RpcReceive( payload );
	}

	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RpcReceive( byte[] payload )
	{
		if ( Rpc.Caller == Connection.Local )
			return;

		if ( payload is null || payload.Length < 12 )
			return;

		var players = GetLivePlayers();
		var senderIndex = -1;
		for ( int i = 0; i < players.Count; i++ )
		{
			if ( players[i].Id == Rpc.Caller.Id ) { senderIndex = i; break; }
		}
		if ( senderIndex < 0 )
			return;

		Adapter?.EnqueueReceive( senderIndex, payload, payload.Length );
	}
}
