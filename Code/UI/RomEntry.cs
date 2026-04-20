namespace sGBA;

public sealed class RomEntry( string path, string displayTitle, string region,
	string internalTitle, string gameCode, string publisher, string thumbnailUrl,
	BaseFileSystem fileSystem )
{
	public string Path { get; } = path;
	public string DisplayTitle { get; } = displayTitle;
	public string Region { get; } = region;
	public string InternalTitle { get; } = internalTitle;
	public string GameCode { get; } = gameCode;
	public string Publisher { get; } = publisher;
	public string ThumbnailUrl { get; } = thumbnailUrl;
	public BaseFileSystem FileSystem { get; } = fileSystem;
	public bool SupportsWirelessAdapter => WirelessAdapterDatabase.Supports( GameCode );
}

