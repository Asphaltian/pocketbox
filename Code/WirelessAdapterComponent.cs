using System.Collections.Concurrent;

namespace sGBA;

public sealed class WirelessAdapterComponent : Component, IWirelessNetwork, Component.INetworkListener
{
	private const int MaxPeers = 32;

	private EmulatorComponent _emulator;
	private GbaWirelessAdapter Adapter => _emulator?.Core?.Io?.WirelessAdapter;

	private readonly List<Connection> _peers = [];
	private readonly Dictionary<Guid, int> _peerIndex = [];
	private bool _wired;

	private readonly ConcurrentQueue<(int ClientId, byte[] Payload)> _outbox = new();

	protected override void OnStart()
	{
		_emulator = Scene.GetAllComponents<EmulatorComponent>().FirstOrDefault();
		TryWire();
		RebuildPeerTable();
	}

	protected override void OnUpdate()
	{
		if ( !_wired )
			TryWire();

		while ( _outbox.TryDequeue( out var pkt ) )
			DispatchSend( pkt.ClientId, pkt.Payload );
	}

	public void OnActive( Connection conn ) => RebuildPeerTable();
	public void OnDisconnected( Connection conn ) => RebuildPeerTable();

	void IWirelessNetwork.Send( int clientId, byte[] data, int length )
	{
		if ( data is null || length <= 0 )
			return;

		var payload = new byte[length];
		Buffer.BlockCopy( data, 0, payload, 0, length );
		_outbox.Enqueue( (clientId, payload) );
	}

	private void TryWire()
	{
		var adapter = Adapter;
		if ( adapter is null )
			return;
		adapter.Network = this;
		_wired = true;
	}

	private void RebuildPeerTable()
	{
		_peers.Clear();
		_peerIndex.Clear();

		var ordered = Connection.All?
			.Where( c => c is not null )
			.OrderBy( c => c.Id )
			.Take( MaxPeers )
			.ToList() ?? [];

		for ( int i = 0; i < ordered.Count; i++ )
		{
			_peers.Add( ordered[i] );
			_peerIndex[ordered[i].Id] = i;
		}
	}

	private void DispatchSend( int clientId, byte[] payload )
	{
		if ( clientId == 0xFFFF )
		{
			RpcReceive( payload );
			return;
		}

		if ( clientId < 0 || clientId >= _peers.Count )
			return;

		var target = _peers[clientId];
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

		if ( !_peerIndex.TryGetValue( Rpc.Caller.Id, out int senderIndex ) )
			return;

		Adapter?.EnqueueReceive( senderIndex, payload, payload.Length );
	}
}
