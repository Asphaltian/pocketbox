namespace sGBA;

public sealed class SessionPlayer
{
	public Connection Connection { get; }
	public int Slot { get; internal set; }
	public bool Ready { get; internal set; }

	internal SessionPlayer( Connection connection, int slot )
	{
		Connection = connection;
		Slot = slot;
	}

	public Guid Id => Connection?.Id ?? Guid.Empty;
	public string DisplayName => Connection?.DisplayName ?? string.Empty;
	public ulong SteamId => Connection?.SteamId ?? 0UL;
	public bool IsHost => Connection?.IsHost ?? false;
	public bool IsLocal => Connection is not null && Connection == Connection.Local;
	public int Ping => (int)(Connection?.Ping ?? 0f);
}
