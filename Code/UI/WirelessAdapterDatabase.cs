namespace sGBA;

public static class WirelessAdapterDatabase
{
	private static readonly HashSet<string> SupportedPrefixes = new( StringComparer.OrdinalIgnoreCase )
	{
		"BPE", // Pokémon Emerald
		"BPR", // Pokémon FireRed
		"BPG", // Pokémon LeafGreen

        // I'm too lazy to find more, but feel free to PR with additional games
	};

	public static bool Supports( string gameCode )
	{
		if ( string.IsNullOrEmpty( gameCode ) || gameCode.Length < 3 )
			return false;

		return SupportedPrefixes.Contains( gameCode[..3] );
	}
}
