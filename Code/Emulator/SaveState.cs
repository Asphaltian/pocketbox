using System.IO;

namespace sGBA;

public static class SaveState
{
	private const uint Magic = 0x53474241;
	public const int SlotCount = 4;
	public const int ScreenshotSize = GbaConstants.ScreenWidth * GbaConstants.ScreenHeight * 4;
	private const int HeaderSize = sizeof( uint ) + sizeof( long );

	public static byte[] Save( GbaSystem gba, byte[] screenshot )
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter( ms );

		w.Write( Magic );
		w.Write( DateTime.UtcNow.Ticks );

		if ( screenshot != null && screenshot.Length == ScreenshotSize )
			w.Write( screenshot );
		else
			w.Write( new byte[ScreenshotSize] );

		WriteCpu( w, gba.Cpu );
		WriteMemory( w, gba.Bus );
		WritePpu( w, gba.Ppu );
		WriteApu( w, gba.Apu );
		WriteIo( w, gba.Io );
		WriteDma( w, gba.Dma );
		WriteTimers( w, gba.Timers );
		WriteSave( w, gba.Save );
		WriteGpio( w, gba.Gpio );
		WriteHleBios( w, gba.HleBios );
		WriteSystem( w, gba );

		gba.Cpu.SerializePipeline( w );

		return ms.ToArray();
	}

	public static void Load( GbaSystem gba, byte[] data )
	{
		using var ms = new MemoryStream( data );
		using var r = new BinaryReader( ms );

		ValidateHeader( r );
		r.BaseStream.Position = HeaderSize + ScreenshotSize;

		ReadCpu( r, gba.Cpu );
		ReadMemory( r, gba.Bus );
		ReadPpu( r, gba.Ppu );
		ReadApu( r, gba.Apu );
		ReadIo( r, gba.Io );
		ReadDma( r, gba.Dma );
		ReadTimers( r, gba.Timers );
		ReadSave( r, gba.Save );
		ReadGpio( r, gba.Gpio );
		ReadHleBios( r, gba.HleBios );
		ReadSystem( r, gba );

		gba.Cpu.DeserializePipeline( r );
		gba.Bus.InstallHleBios();
	}

	public static byte[] ReadScreenshot( byte[] data )
	{
		if ( data == null || data.Length < HeaderSize + ScreenshotSize )
			return null;

		using var ms = new MemoryStream( data );
		using var r = new BinaryReader( ms );

		if ( !TryValidateHeader( r ) )
			return null;

		r.BaseStream.Position = HeaderSize;
		return r.ReadBytes( ScreenshotSize );
	}

	public static DateTime? ReadTimestamp( byte[] data )
	{
		if ( data == null || data.Length < HeaderSize )
			return null;

		using var ms = new MemoryStream( data );
		using var r = new BinaryReader( ms );

		if ( !TryValidateHeader( r ) )
			return null;

		return new DateTime( r.ReadInt64(), DateTimeKind.Utc ).ToLocalTime();
	}

	private static void ValidateHeader( BinaryReader r )
	{
		if ( !TryValidateHeader( r ) )
			throw new Exception( "Invalid suspend point." );
	}

	private static bool TryValidateHeader( BinaryReader r )
	{
		return r.ReadUInt32() == Magic;
	}

	private static void WriteCpu( BinaryWriter w, Arm7Cpu cpu )
	{
		for ( int i = 0; i < 16; i++ ) w.Write( cpu.R[i] );
		w.Write( cpu.FlagN ); w.Write( cpu.FlagZ ); w.Write( cpu.FlagC ); w.Write( cpu.FlagV );
		w.Write( cpu.IrqDisable ); w.Write( cpu.FiqDisable );
		w.Write( cpu.ThumbMode );
		w.Write( (int)cpu.Mode );

		for ( int i = 0; i < 6; i++ )
		{
			w.Write( cpu.SpsrBank[i] );
		}

		WriteBankedRegs( w, cpu );

		w.Write( cpu.Cycles );
		w.Write( cpu.Halted ); w.Write( cpu.IrqPending );
		w.Write( cpu.InIntrWait ); w.Write( cpu.InIrqContext );
		w.Write( cpu.IntrWaitFlags );
		w.Write( cpu.OpenBusPrefetch );
	}

	private static void WriteBankedRegs( BinaryWriter w, Arm7Cpu cpu )
	{
		var origMode = cpu.Mode;
		var origR13 = cpu.R[13];
		var origR14 = cpu.R[14];

		CpuMode[] modes = [CpuMode.User, CpuMode.FIQ, CpuMode.IRQ, CpuMode.Supervisor, CpuMode.Abort, CpuMode.Undefined];

		foreach ( var mode in modes )
		{
			cpu.SwitchMode( mode );
			w.Write( cpu.R[13] );
			w.Write( cpu.R[14] );
		}

		cpu.SwitchMode( CpuMode.FIQ );
		for ( int i = 8; i <= 12; i++ ) w.Write( cpu.R[i] );

		cpu.SwitchMode( CpuMode.System );
		for ( int i = 8; i <= 12; i++ ) w.Write( cpu.R[i] );

		cpu.SwitchMode( origMode );
		cpu.R[13] = origR13;
		cpu.R[14] = origR14;
	}

	private static void ReadCpu( BinaryReader r, Arm7Cpu cpu )
	{
		for ( int i = 0; i < 16; i++ ) cpu.R[i] = r.ReadUInt32();
		cpu.FlagN = r.ReadBoolean(); cpu.FlagZ = r.ReadBoolean();
		cpu.FlagC = r.ReadBoolean(); cpu.FlagV = r.ReadBoolean();
		cpu.IrqDisable = r.ReadBoolean(); cpu.FiqDisable = r.ReadBoolean();
		cpu.ThumbMode = r.ReadBoolean();
		cpu.Mode = (CpuMode)r.ReadInt32();

		for ( int i = 0; i < 6; i++ )
			cpu.SpsrBank[i] = r.ReadUInt32();

		ReadBankedRegs( r, cpu );

		cpu.Cycles = r.ReadInt64();
		cpu.Halted = r.ReadBoolean(); cpu.IrqPending = r.ReadBoolean();
		cpu.InIntrWait = r.ReadBoolean(); cpu.InIrqContext = r.ReadBoolean();
		cpu.IntrWaitFlags = r.ReadUInt16();
		cpu.OpenBusPrefetch = r.ReadUInt32();
	}

	private static void ReadBankedRegs( BinaryReader r, Arm7Cpu cpu )
	{
		CpuMode[] modes = [CpuMode.User, CpuMode.FIQ, CpuMode.IRQ, CpuMode.Supervisor, CpuMode.Abort, CpuMode.Undefined];
		var targetMode = cpu.Mode;
		var savedR13 = cpu.R[13];
		var savedR14 = cpu.R[14];

		foreach ( var mode in modes )
		{
			cpu.SwitchMode( mode );
			cpu.R[13] = r.ReadUInt32();
			cpu.R[14] = r.ReadUInt32();
		}

		cpu.SwitchMode( CpuMode.FIQ );
		for ( int i = 8; i <= 12; i++ ) cpu.R[i] = r.ReadUInt32();

		cpu.SwitchMode( CpuMode.System );
		for ( int i = 8; i <= 12; i++ ) cpu.R[i] = r.ReadUInt32();

		cpu.SwitchMode( targetMode );
		cpu.R[13] = savedR13;
		cpu.R[14] = savedR14;
	}

	private static void WriteMemory( BinaryWriter w, MemoryBus bus )
	{
		w.Write( bus.Ewram );
		w.Write( bus.Iwram );
		w.Write( bus.PaletteRam );
		w.Write( bus.Vram );
		w.Write( bus.Oam );

		for ( int i = 0; i < bus.IoRegisters.Length; i++ )
			w.Write( bus.IoRegisters[i] );

		w.Write( bus.BiosPrefetch );
		w.Write( bus.PrefetchEnabled );
		w.Write( bus.LastPrefetchedPc );

		for ( int i = 0; i < 16; i++ ) w.Write( bus.WaitstatesNonseq16[i] );
		for ( int i = 0; i < 16; i++ ) w.Write( bus.WaitstatesNonseq32[i] );
		for ( int i = 0; i < 16; i++ ) w.Write( bus.WaitstatesSeq16[i] );
		for ( int i = 0; i < 16; i++ ) w.Write( bus.WaitstatesSeq32[i] );

		w.Write( bus.DebugEnabled );
		w.Write( bus.DebugString );
		w.Write( bus.DebugFlags );
	}

	private static void ReadMemory( BinaryReader r, MemoryBus bus )
	{
		r.Read( bus.Ewram );
		r.Read( bus.Iwram );
		r.Read( bus.PaletteRam );
		r.Read( bus.Vram );
		r.Read( bus.Oam );

		for ( int i = 0; i < bus.IoRegisters.Length; i++ )
			bus.IoRegisters[i] = r.ReadUInt16();

		bus.BiosPrefetch = r.ReadUInt32();
		bus.PrefetchEnabled = r.ReadBoolean();
		bus.LastPrefetchedPc = r.ReadUInt32();

		for ( int i = 0; i < 16; i++ ) bus.WaitstatesNonseq16[i] = r.ReadInt32();
		for ( int i = 0; i < 16; i++ ) bus.WaitstatesNonseq32[i] = r.ReadInt32();
		for ( int i = 0; i < 16; i++ ) bus.WaitstatesSeq16[i] = r.ReadInt32();
		for ( int i = 0; i < 16; i++ ) bus.WaitstatesSeq32[i] = r.ReadInt32();

		bus.DebugEnabled = r.ReadBoolean();
		r.Read( bus.DebugString );
		bus.DebugFlags = r.ReadUInt16();
	}

	private static void WritePpu( BinaryWriter w, Ppu ppu )
	{
		w.Write( ppu.VCount ); w.Write( ppu.Dot );
		w.Write( ppu.DispCnt ); w.Write( ppu.DispStat );

		for ( int i = 0; i < 4; i++ ) w.Write( ppu.BgCnt[i] );
		for ( int i = 0; i < 4; i++ ) w.Write( ppu.BgHOfs[i] );
		for ( int i = 0; i < 4; i++ ) w.Write( ppu.BgVOfs[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgPA[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgPB[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgPC[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgPD[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgX[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgY[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgRefX[i] );
		for ( int i = 0; i < 2; i++ ) w.Write( ppu.BgRefY[i] );

		w.Write( ppu.BldCnt ); w.Write( ppu.BldAlpha ); w.Write( ppu.BldY );
		w.Write( ppu.Win0H ); w.Write( ppu.Win0V );
		w.Write( ppu.Win1H ); w.Write( ppu.Win1V );
		w.Write( ppu.WinIn ); w.Write( ppu.WinOut );
		w.Write( ppu.Mosaic );
	}

	private static void ReadPpu( BinaryReader r, Ppu ppu )
	{
		ppu.VCount = r.ReadInt32(); ppu.Dot = r.ReadInt32();
		ppu.DispCnt = r.ReadUInt16(); ppu.DispStat = r.ReadUInt16();

		for ( int i = 0; i < 4; i++ ) ppu.BgCnt[i] = r.ReadUInt16();
		for ( int i = 0; i < 4; i++ ) ppu.BgHOfs[i] = r.ReadInt16();
		for ( int i = 0; i < 4; i++ ) ppu.BgVOfs[i] = r.ReadInt16();
		for ( int i = 0; i < 2; i++ ) ppu.BgPA[i] = r.ReadInt16();
		for ( int i = 0; i < 2; i++ ) ppu.BgPB[i] = r.ReadInt16();
		for ( int i = 0; i < 2; i++ ) ppu.BgPC[i] = r.ReadInt16();
		for ( int i = 0; i < 2; i++ ) ppu.BgPD[i] = r.ReadInt16();
		for ( int i = 0; i < 2; i++ ) ppu.BgX[i] = r.ReadInt32();
		for ( int i = 0; i < 2; i++ ) ppu.BgY[i] = r.ReadInt32();
		for ( int i = 0; i < 2; i++ ) ppu.BgRefX[i] = r.ReadInt32();
		for ( int i = 0; i < 2; i++ ) ppu.BgRefY[i] = r.ReadInt32();

		ppu.BldCnt = r.ReadUInt16(); ppu.BldAlpha = r.ReadUInt16(); ppu.BldY = r.ReadUInt16();
		ppu.Win0H = r.ReadUInt16(); ppu.Win0V = r.ReadUInt16();
		ppu.Win1H = r.ReadUInt16(); ppu.Win1V = r.ReadUInt16();
		ppu.WinIn = r.ReadUInt16(); ppu.WinOut = r.ReadUInt16();
		ppu.Mosaic = r.ReadUInt16();
	}

	private static void WriteApu( BinaryWriter w, Apu apu )
	{
		w.Write( apu.Enable );
		w.Write( apu.SoundBias );

		w.Write( apu.Sound1CntL ); w.Write( apu.Sound1CntH ); w.Write( apu.Sound1CntX );
		w.Write( apu.Sound2CntL ); w.Write( apu.Sound2CntH );
		w.Write( apu.Sound3CntL ); w.Write( apu.Sound3CntH ); w.Write( apu.Sound3CntX );
		w.Write( apu.Sound4CntL ); w.Write( apu.Sound4CntH );
		w.Write( apu.SoundCntL ); w.Write( apu.SoundCntH ); w.Write( apu.SoundCntX );

		w.Write( apu.WaveRam );

		apu.SerializeState( w );
	}

	private static void ReadApu( BinaryReader r, Apu apu )
	{
		apu.Enable = r.ReadBoolean();
		apu.SoundBias = r.ReadUInt16();

		apu.Sound1CntL = r.ReadUInt16(); apu.Sound1CntH = r.ReadUInt16(); apu.Sound1CntX = r.ReadUInt16();
		apu.Sound2CntL = r.ReadUInt16(); apu.Sound2CntH = r.ReadUInt16();
		apu.Sound3CntL = r.ReadUInt16(); apu.Sound3CntH = r.ReadUInt16(); apu.Sound3CntX = r.ReadUInt16();
		apu.Sound4CntL = r.ReadUInt16(); apu.Sound4CntH = r.ReadUInt16();
		apu.SoundCntL = r.ReadUInt16(); apu.SoundCntH = r.ReadUInt16(); apu.SoundCntX = r.ReadUInt16();

		r.Read( apu.WaveRam );

		apu.DeserializeState( r );
	}

	private static void WriteIo( BinaryWriter w, IoRegisters io )
	{
		w.Write( io.IE ); w.Write( io.IF ); w.Write( io.IME );
		w.Write( io.WaitCnt );
		w.Write( io.KeyInput ); w.Write( io.KeyCnt );
		w.Write( io.Rcnt );
		w.Write( io.PostBoot );
		w.Write( io.HaltRequested );
		io.SerializeState( w );
	}

	private static void ReadIo( BinaryReader r, IoRegisters io )
	{
		io.IE = r.ReadUInt16(); io.IF = r.ReadUInt16(); io.IME = r.ReadUInt16();
		io.WaitCnt = r.ReadUInt16();
		io.KeyInput = r.ReadUInt16(); io.KeyCnt = r.ReadUInt16();
		io.Rcnt = r.ReadUInt16();
		io.PostBoot = r.ReadByte();
		io.HaltRequested = r.ReadBoolean();
		io.DeserializeState( r );
	}

	private static void WriteDma( BinaryWriter w, DmaController dma )
	{
		w.Write( dma.ActiveDma );
		w.Write( dma.CpuBlocked );
		w.Write( dma.PerformingDma );

		for ( int i = 0; i < 4; i++ )
		{
			var c = dma.Channels[i];
			w.Write( c.SrcLow ); w.Write( c.SrcHigh );
			w.Write( c.DstLow ); w.Write( c.DstHigh );
			w.Write( c.WordCount ); w.Write( c.Control );
			w.Write( c.NextSource ); w.Write( c.NextDest );
			w.Write( c.NextCount ); w.Write( c.Latch );
			w.Write( c.When ); w.Write( c.SeqCycles );
			w.Write( c.IsFirstUnit );
			w.Write( c.SourceOffset ); w.Write( c.DestOffset );
			w.Write( c.DestInvalid );
		}
	}

	private static void ReadDma( BinaryReader r, DmaController dma )
	{
		dma.ActiveDma = r.ReadInt32();
		dma.CpuBlocked = r.ReadBoolean();
		dma.PerformingDma = r.ReadInt32();

		for ( int i = 0; i < 4; i++ )
		{
			var c = dma.Channels[i];
			c.SrcLow = r.ReadUInt16(); c.SrcHigh = r.ReadUInt16();
			c.DstLow = r.ReadUInt16(); c.DstHigh = r.ReadUInt16();
			c.WordCount = r.ReadUInt16(); c.Control = r.ReadUInt16();
			c.NextSource = r.ReadUInt32(); c.NextDest = r.ReadUInt32();
			c.NextCount = r.ReadInt32(); c.Latch = r.ReadUInt32();
			c.When = r.ReadInt64(); c.SeqCycles = r.ReadInt32();
			c.IsFirstUnit = r.ReadBoolean();
			c.SourceOffset = r.ReadInt32(); c.DestOffset = r.ReadInt32();
			c.DestInvalid = r.ReadBoolean();
		}
	}

	private static void WriteTimers( BinaryWriter w, TimerController timers )
	{
		w.Write( timers.NextGlobalEvent );

		for ( int i = 0; i < 4; i++ )
		{
			var c = timers.Channels[i];
			w.Write( c.Reload ); w.Write( c.Counter ); w.Write( c.Control );
			w.Write( c.Enabled ); w.Write( c.CountUp ); w.Write( c.IrqEnable );
			w.Write( c.PrescalerIndex );
			w.Write( c.LastEventCycle ); w.Write( c.NextOverflowCycle );
		}
	}

	private static void ReadTimers( BinaryReader r, TimerController timers )
	{
		timers.NextGlobalEvent = r.ReadInt64();

		for ( int i = 0; i < 4; i++ )
		{
			var c = timers.Channels[i];
			c.Reload = r.ReadUInt16(); c.Counter = r.ReadUInt16(); c.Control = r.ReadUInt16();
			c.Enabled = r.ReadBoolean(); c.CountUp = r.ReadBoolean(); c.IrqEnable = r.ReadBoolean();
			c.PrescalerIndex = r.ReadInt32();
			c.LastEventCycle = r.ReadInt64(); c.NextOverflowCycle = r.ReadInt64();
		}
	}

	private static void WriteSave( BinaryWriter w, SaveManager save )
	{
		w.Write( (int)save.Type );
		w.Write( save.Data.Length );
		w.Write( save.Data );
		save.SerializeState( w );
	}

	private static void ReadSave( BinaryReader r, SaveManager save )
	{
		var type = (SaveType)r.ReadInt32();
		int len = r.ReadInt32();
		var data = r.ReadBytes( len );

		if ( type == save.Type && data.Length == save.Data.Length )
			Array.Copy( data, save.Data, data.Length );
		else if ( type == save.Type )
			r.BaseStream.Position -= len;

		save.DeserializeState( r );
	}

	private static void WriteGpio( BinaryWriter w, GpioController gpio )
	{
		w.Write( gpio.HasRtc );
		gpio.SerializeState( w );
	}

	private static void ReadGpio( BinaryReader r, GpioController gpio )
	{
		gpio.HasRtc = r.ReadBoolean();
		gpio.DeserializeState( r );
	}

	private static void WriteHleBios( BinaryWriter w, HleBios bios )
	{
		w.Write( bios.HleActive );
		w.Write( bios.BiosStall );
	}

	private static void ReadHleBios( BinaryReader r, HleBios bios )
	{
		bios.HleActive = r.ReadBoolean();
		bios.BiosStall = r.ReadInt32();
	}

	private static void WriteSystem( BinaryWriter w, GbaSystem gba )
	{
		w.Write( gba.CyclesThisFrame );
		w.Write( gba.TotalFrames );
		w.Write( gba.TotalCycles );
	}

	private static void ReadSystem( BinaryReader r, GbaSystem gba )
	{
		gba.CyclesThisFrame = r.ReadInt32();
		gba.TotalFrames = r.ReadInt64();
		gba.TotalCycles = r.ReadInt64();
		gba.IsRunning = true;
	}
}
