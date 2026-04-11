namespace sGBA;

public static class RomLibrary
{
	private const string LibretroThumbnailBase = "https://thumbnails.libretro.com/Nintendo%20-%20Game%20Boy%20Advance/Named_Boxarts/";

	private static readonly Dictionary<string, string> MakerCodes = new( StringComparer.OrdinalIgnoreCase )
	{
		["01"] = "Nintendo",       ["02"] = "Rocket Games",    ["08"] = "Capcom",
		["09"] = "Hot B",          ["0A"] = "Jaleco",          ["0B"] = "Coconuts Japan",
		["13"] = "EA",             ["18"] = "Hudson",          ["1A"] = "Yanoman",
		["1F"] = "Virgin",         ["20"] = "KSS",             ["22"] = "POW",
		["28"] = "Kemco",          ["29"] = "SETA",            ["2H"] = "Ubisoft",
		["30"] = "Viacom",         ["31"] = "Nintendo",        ["32"] = "Bandai",
		["33"] = "Ocean/Acclaim",  ["34"] = "Konami",          ["35"] = "Hector",
		["37"] = "Taito",          ["38"] = "Hudson",          ["39"] = "Banpresto",
		["3C"] = "Enterbrain",     ["3E"] = "Gremlin",         ["41"] = "Ubisoft",
		["42"] = "Atlus",          ["44"] = "Malibu",          ["46"] = "Angel",
		["47"] = "Spectrum Holobyte", ["49"] = "Irem",         ["4A"] = "Virgin",
		["4D"] = "Malibu",         ["4F"] = "Eidos",           ["50"] = "Absolute",
		["51"] = "Acclaim",        ["52"] = "Activision",      ["53"] = "American Sammy",
		["54"] = "Konami",         ["55"] = "Hi Tech Ent.",    ["56"] = "LJN",
		["57"] = "Matchbox",       ["58"] = "Mattel",          ["59"] = "Milton Bradley",
		["5A"] = "Mindscape",      ["5D"] = "Tradewest",       ["60"] = "Titus",
		["61"] = "Virgin",         ["67"] = "Ocean",           ["69"] = "EA",
		["6E"] = "Elite Systems",  ["6F"] = "Electro Brain",   ["70"] = "Infogrames",
		["71"] = "Interplay",      ["72"] = "Broderbund",      ["73"] = "Sculptured Soft",
		["75"] = "SCi",            ["78"] = "THQ",             ["79"] = "Accolade",
		["7A"] = "Triffix Ent.",   ["7C"] = "Microprose",      ["7F"] = "Kemco",
		["80"] = "Misawa",         ["83"] = "LOZC",            ["86"] = "Tokuma Shoten",
		["8B"] = "Bullet-Proof",   ["8C"] = "Vic Tokai",       ["8E"] = "Ape",
		["8F"] = "I'Max",          ["91"] = "Chunsoft",        ["92"] = "Video System",
		["93"] = "Tsubaraya Prod", ["95"] = "Varie",           ["96"] = "Yonezawa/S'pal",
		["97"] = "Kaneko",         ["99"] = "Arc",             ["9A"] = "Nihon Bussan",
		["9B"] = "Tecmo",          ["9C"] = "Imagineer",       ["9D"] = "Banpresto",
		["9F"] = "Nova",           ["A1"] = "Hori Electric",   ["A2"] = "Bandai",
		["A3"] = "Konami",         ["A4"] = "Konami",          ["A6"] = "Kawada",
		["A7"] = "Takara",         ["A9"] = "Technos Japan",   ["AA"] = "Broderbund",
		["AC"] = "Toei Animation", ["AD"] = "Toho",            ["AF"] = "Namco",
		["B0"] = "Acclaim",        ["B1"] = "ASCII/Nexoft",    ["B2"] = "Bandai",
		["B4"] = "Enix",           ["B6"] = "HAL",             ["B7"] = "SNK",
		["B9"] = "Pony Canyon",    ["BA"] = "Culture Brain",   ["BB"] = "Sunsoft",
		["BF"] = "Sammy",          ["C0"] = "Taito",           ["C2"] = "Kemco",
		["C3"] = "Square",         ["C4"] = "Tokuma Shoten",   ["C5"] = "Data East",
		["C6"] = "Tonkin House",   ["C8"] = "Koei",            ["CA"] = "Konami",
		["CB"] = "Vapinc/Palsoft", ["CC"] = "Use Gold",        ["CE"] = "Pony Canyon",
		["CF"] = "Angel",          ["D1"] = "Sofel",           ["D2"] = "Quest",
		["D3"] = "Sigma Ent.",     ["D4"] = "Ask Kodansha",    ["D6"] = "Naxat Soft",
		["D7"] = "Copya System",   ["D9"] = "Banpresto",       ["DA"] = "Tomy",
		["DB"] = "LJN",            ["DD"] = "NCS",             ["DE"] = "Human",
		["DF"] = "Altron",         ["E0"] = "Jaleco",          ["E1"] = "Towachiki",
		["E2"] = "Upal",           ["E3"] = "Vap",             ["E5"] = "Epoch",
		["E7"] = "Athena",         ["E8"] = "Asmik",           ["E9"] = "Natsume",
		["EA"] = "King Records",   ["EB"] = "Atlus",           ["EC"] = "Epic/Sony",
		["EF"] = "Inform",         ["F0"] = "Kemco",           ["F3"] = "Extreme Ent.",
		["F4"] = "Tecmo",          ["F5"] = "Virgin",          ["F6"] = "Nicotaka",
		["F7"] = "Meldac",         ["F8"] = "Pony Canyon",     ["F9"] = "Societa Daikanyama",
		["FA"] = "Konami",         ["FB"] = "Hector",          ["FF"] = "LJN",
		["G5"] = "Majesco",        ["G9"] = "Natsume",         ["GD"] = "Square Enix",
		["GN"] = "Oxygen Games",   ["8P"] = "Sega",
	};

	public static List<RomEntry> Discover()
	{
		List<RomEntry> entries = [];
		CollectFrom( FileSystem.Mounted, entries );
		CollectFrom( FileSystem.Data, entries );
		entries.Sort( ( a, b ) => string.Compare( a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase ) );
		return entries;
	}

	public static HashSet<string> GetRomPaths()
	{
		var paths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		try { foreach ( var f in FileSystem.Mounted.FindFile( "roms", "*.gba" ) ?? [] ) paths.Add( $"roms/{f}" ); } catch { }
		try { foreach ( var f in FileSystem.Data.FindFile( "roms", "*.gba" ) ?? [] ) paths.Add( $"roms/{f}" ); } catch { }
		return paths;
	}

	private static void CollectFrom( BaseFileSystem fs, List<RomEntry> entries )
	{
		IEnumerable<string> found;
		try { found = fs.FindFile( "roms", "*.gba" ) ?? []; }
		catch { return; }

		foreach ( string fileName in found )
		{
			RomEntry entry = BuildEntry( fs, $"roms/{fileName}", fileName );
			if ( entry is not null )
				entries.Add( entry );
		}
	}

	private static RomEntry BuildEntry( BaseFileSystem fs, string fullPath, string fileName )
	{
		string baseName = System.IO.Path.GetFileNameWithoutExtension( fileName );
		(string displayTitle, string region) = ParseNoIntroName( baseName );
		(string internalTitle, string gameCode, string makerCode) = ReadRomHeader( fs, fullPath );

		MakerCodes.TryGetValue( makerCode, out string publisher );

		string thumbUrl = string.IsNullOrEmpty( region )
			? null
			: $"{LibretroThumbnailBase}{baseName.Replace( "&", "_" ).Replace( " ", "%20" )}.png";

		return new RomEntry( fullPath, displayTitle, region, internalTitle, gameCode, publisher ?? string.Empty, thumbUrl, fs );
	}

	private static (string DisplayTitle, string Region) ParseNoIntroName( string baseName )
	{
		int parenOpen = baseName.IndexOf( '(' );
		if ( parenOpen <= 0 )
			return (baseName, string.Empty);

		string displayTitle = baseName[..parenOpen].TrimEnd();
		int parenClose = baseName.IndexOf( ')', parenOpen );
		string region = parenClose > parenOpen ? baseName[(parenOpen + 1)..parenClose] : string.Empty;
		return (displayTitle, region);
	}

	private static (string InternalTitle, string GameCode, string MakerCode) ReadRomHeader( BaseFileSystem fs, string path )
	{
		try
		{
			using System.IO.Stream stream = fs.OpenRead( path );
			if ( stream.Length < 0xB2 )
				return (string.Empty, string.Empty, string.Empty);

			byte[] header = new byte[0xB2];
			stream.ReadExactly( header, 0, 0xB2 );

			string internalTitle = System.Text.Encoding.ASCII.GetString( header, 0xA0, 12 ).TrimEnd( '\0', ' ' );
			string gameCode = System.Text.Encoding.ASCII.GetString( header, 0xAC, 4 ).TrimEnd( '\0' );
			string makerCode = System.Text.Encoding.ASCII.GetString( header, 0xB0, 2 ).TrimEnd( '\0', ' ' );
			return (internalTitle, gameCode, makerCode);
		}
		catch
		{
			return (string.Empty, string.Empty, string.Empty);
		}
	}
}
