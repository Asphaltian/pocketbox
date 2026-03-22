namespace sGBA;

[Flags]
public enum LogLevel
{
	Fatal = 0x01,
	Error = 0x02,
	Warn = 0x04,
	Info = 0x08,
	Debug = 0x10,
	Stub = 0x20,
	GameError = 0x40,

	All = 0x7F
}

public enum LogCategory
{
	GBA,
	GBADebug,
	GBADMA,
	GBAMem,
	GBAIO,
	GBAVideo,
	GBAAudio,
	GBABIOS,
	GBASave,
	GBASIO,
	GBAState,
	GBAHardware,

	Status,
	Max
}

public static class GbaLog
{
	private const int MaxLogBuf = 1024;

	private static readonly string[] CategoryNames = new string[(int)LogCategory.Max]
	{
		"GBA",
		"GBA Debug",
		"GBA DMA",
		"GBA Memory",
		"GBA I/O",
		"GBA Video",
		"GBA Audio",
		"GBA BIOS",
		"GBA Savedata",
		"GBA SIO",
		"GBA State",
		"GBA Hardware",
		"Status"
	};

	private static readonly string[] CategoryIds = new string[(int)LogCategory.Max]
	{
		"gba",
		"gba.debug",
		"gba.dma",
		"gba.memory",
		"gba.io",
		"gba.video",
		"gba.audio",
		"gba.bios",
		"gba.savedata",
		"gba.sio",
		"gba.serialize",
		"gba.hardware",
		"status"
	};

	private static LogLevel _defaultLevels = LogLevel.All;
	private static readonly Dictionary<LogCategory, LogLevel> _categoryLevels = new();
	private static Action<LogCategory, LogLevel, string> _backend;

	public static LogLevel DefaultLevels
	{
		get => _defaultLevels;
		set => _defaultLevels = value;
	}

	public static void SetBackend( Action<LogCategory, LogLevel, string> backend )
	{
		_backend = backend;
	}

	public static void SetCategoryLevels( LogCategory category, LogLevel levels )
	{
		_categoryLevels[category] = levels;
	}

	public static void ResetCategoryLevels( LogCategory category )
	{
		_categoryLevels.Remove( category );
	}

	public static LogLevel GetCategoryLevels( LogCategory category )
	{
		if ( _categoryLevels.TryGetValue( category, out var levels ) )
			return levels;
		return _defaultLevels;
	}

	public static bool FilterTest( LogCategory category, LogLevel level )
	{
		if ( _categoryLevels.TryGetValue( category, out var catLevels ) )
			return (catLevels & level) != 0;
		return (_defaultLevels & level) != 0;
	}

	public static string GetCategoryName( LogCategory category )
	{
		int idx = (int)category;
		if ( idx >= 0 && idx < CategoryNames.Length )
			return CategoryNames[idx];
		return "Unknown";
	}

	public static string GetCategoryId( LogCategory category )
	{
		int idx = (int)category;
		if ( idx >= 0 && idx < CategoryIds.Length )
			return CategoryIds[idx];
		return "unknown";
	}

	public static LogCategory? CategoryById( string id )
	{
		for ( int i = 0; i < CategoryIds.Length; i++ )
		{
			if ( string.Equals( CategoryIds[i], id, StringComparison.Ordinal ) )
				return (LogCategory)i;
		}
		return null;
	}

	public static void Write( LogCategory category, LogLevel level, string message )
	{
		if ( !FilterTest( category, level ) )
			return;

		if ( _backend != null )
		{
			_backend( category, level, message );
		}
		else
		{
			DefaultLog( category, level, message );
		}
	}

	public static void Write( LogCategory category, LogLevel level, string format, object arg0 )
	{
		if ( !FilterTest( category, level ) )
			return;

		Write( category, level, string.Format( format, arg0 ) );
	}

	public static void Write( LogCategory category, LogLevel level, string format, object arg0, object arg1 )
	{
		if ( !FilterTest( category, level ) )
			return;

		Write( category, level, string.Format( format, arg0, arg1 ) );
	}

	public static void Write( LogCategory category, LogLevel level, string format, object arg0, object arg1, object arg2 )
	{
		if ( !FilterTest( category, level ) )
			return;

		Write( category, level, string.Format( format, arg0, arg1, arg2 ) );
	}

	public static void Write( LogCategory category, LogLevel level, string format, params object[] args )
	{
		if ( !FilterTest( category, level ) )
			return;

		Write( category, level, string.Format( format, args ) );
	}

	private static void DefaultLog( LogCategory category, LogLevel level, string message )
	{
		string prefix = GetCategoryName( category );
		string levelTag = level switch
		{
			LogLevel.Fatal => "FATAL",
			LogLevel.Error => "ERROR",
			LogLevel.Warn => "WARN",
			LogLevel.Info => "INFO",
			LogLevel.Debug => "DEBUG",
			LogLevel.Stub => "STUB",
			LogLevel.GameError => "GAME ERROR",
			_ => level.ToString()
		};

		Log.Info( $"[{levelTag}] {prefix}: {message}" );
	}
}
