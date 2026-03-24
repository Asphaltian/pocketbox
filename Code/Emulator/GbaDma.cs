namespace sGBA;

public class GbaDmaController
{
	private static readonly uint[] SrcMask = [0x07FFFFFEu, 0x0FFFFFFEu, 0x0FFFFFFEu, 0x0FFFFFFEu];
	private static readonly uint[] DstMask = [0x07FFFFFEu, 0x07FFFFFEu, 0x07FFFFFEu, 0x0FFFFFFEu];
	private static readonly int[] OffsetDir = [1, -1, 0, 1];

	public Gba Gba { get; }
	public GbaDma[] Channels = new GbaDma[4];

	public int ActiveDma = -1;
	public bool CpuBlocked;
	public int PerformingDma;

	public GbaDmaController( Gba gba )
	{
		Gba = gba;
		for ( int i = 0; i < 4; i++ )
			Channels[i] = new GbaDma( i );
	}

	public void Reset()
	{
		for ( int i = 0; i < 4; i++ )
			Channels[i].Reset();
		ActiveDma = -1;
		CpuBlocked = false;
		PerformingDma = 0;
	}

	public void WriteControl( int ch, ushort value )
	{
		var c = Channels[ch];
		bool wasEnabled = (c.Reg & 0x8000) != 0;

		value &= ch < 3 ? unchecked((ushort)0xF7E0) : unchecked((ushort)0xFFE0);
		c.Reg = value;

		uint width = (uint)(2 << ((value >> 10) & 1));
		RecalculateOffsets( c, ch, width, value );

		if ( (value & 0x0800) != 0 )
			GbaLog.Write( LogCategory.GBADMA, LogLevel.Stub, "DRQ not implemented" );

		if ( wasEnabled || (value & 0x8000) == 0 )
			return;

		c.NextSource = c.Source & SrcMask[ch] & ~(width - 1);
		c.NextDest = c.Dest & DstMask[ch] & ~(width - 1);
		c.DestInvalid = ch < 3 && c.Dest >= 0x08000000;

		if ( (c.NextSource & (width - 1)) != 0 && GbaLog.FilterTest( LogCategory.GBADMA, LogLevel.GameError ) )
			GbaLog.Write( LogCategory.GBADMA, LogLevel.GameError, $"Misaligned DMA source address: 0x{c.NextSource:X8}" );
		if ( (c.NextDest & (width - 1)) != 0 && GbaLog.FilterTest( LogCategory.GBADMA, LogLevel.GameError ) )
			GbaLog.Write( LogCategory.GBADMA, LogLevel.GameError, $"Misaligned DMA destination address: 0x{c.NextDest:X8}" );

		if ( GbaLog.FilterTest( LogCategory.GBADMA, LogLevel.Info ) )
			GbaLog.Write( LogCategory.GBADMA, LogLevel.Info,
				$"Starting DMA {ch} 0x{c.NextSource:X8} -> 0x{c.NextDest:X8} ({c.Reg:X4}:{c.Count:X4})" );

		int timing = (value >> 12) & 3;
		if ( timing == 0 )
		{
			ScheduleDma( c );
		}
		else if ( timing == 3 && ch == 0 )
		{
			GbaLog.Write( LogCategory.GBADMA, LogLevel.Warn, "Discarding invalid DMA0 scheduling" );
		}
		else if ( timing == 3 && (ch == 1 || ch == 2) )
		{
			c.Reg = (ushort)((c.Reg & ~0x0060) | 0x0040 | 0x0400);
			c.DestOffset = 0;
		}
	}

	private void RecalculateOffsets( GbaDma c, int ch, uint width, ushort control )
	{
		uint src = c.Source & SrcMask[ch];
		if ( src >= 0x08000000 && src < 0x0E000000 )
			c.SourceOffset = (int)width;
		else
			c.SourceOffset = OffsetDir[(control >> 7) & 3] * (int)width;

		c.DestOffset = OffsetDir[(control >> 5) & 3] * (int)width;
	}

	private void ScheduleDma( GbaDma c )
	{
		c.When = Gba.Cpu.InstructionStartCycles + 3;
		c.NextCount = c.EffectiveCount;
		c.IsFirstUnit = true;
		Update();
	}

	public void OnHBlank() => TriggerByTiming( 2 );
	public void OnVBlank() => TriggerByTiming( 1 );

	private void TriggerByTiming( int timing )
	{
		bool found = false;
		for ( int i = 0; i < 4; i++ )
		{
			var c = Channels[i];
			if ( (c.Reg & 0x8000) == 0 ) continue;
			if ( ((c.Reg >> 12) & 3) != timing ) continue;
			if ( c.NextCount != 0 ) continue;

			c.When = Gba.Cpu.Cycles + 3;
			c.NextCount = c.EffectiveCount;
			c.IsFirstUnit = true;
			found = true;
		}

		if ( found ) Update();
	}

	public void OnDisplayStart()
	{
		var c = Channels[3];
		if ( (c.Reg & 0x8000) == 0 ) return;
		if ( ((c.Reg >> 12) & 3) != 3 ) return;
		if ( c.NextCount != 0 ) return;

		ScheduleDma( c );
	}

	public void OnFifo( int channel )
	{
		if ( channel != 1 && channel != 2 ) return;
		var c = Channels[channel];
		if ( (c.Reg & 0x8000) == 0 ) return;
		if ( ((c.Reg >> 12) & 3) != 3 ) return;

		int srcRegion = (int)(c.NextSource >> 24) & 0xF;
		int dstRegion = (int)(c.NextDest >> 24) & 0xF;
		int nonseq = Gba.Memory.WaitstatesNonseq32[srcRegion] + Gba.Memory.WaitstatesNonseq32[dstRegion];
		int seq = Gba.Memory.WaitstatesSeq32[srcRegion] + Gba.Memory.WaitstatesSeq32[dstRegion];
		int totalCycles = (2 + nonseq) + 3 * (2 + seq);

		for ( int i = 0; i < 4; i++ )
		{
			if ( c.NextSource >= 0x02000000 )
				c.Latch = Gba.Memory.Load32( c.NextSource );
			Gba.Memory.Store32( c.NextDest, c.Latch );
			c.NextSource += (uint)c.SourceOffset;
		}

		Gba.Cpu.OpenBusPrefetch = c.Latch;
		ChargeCycles( totalCycles );

		if ( (c.Reg & 0x4000) != 0 )
			Gba.Io.RaiseIrq( (GbaIrq)(1 << (8 + channel)) );
	}

	public void Update()
	{
		int best = -1;
		long bestTime = long.MaxValue;

		for ( int i = 0; i < 4; i++ )
		{
			var c = Channels[i];
			if ( (c.Reg & 0x8000) != 0 && c.NextCount > 0 && c.When < bestTime )
			{
				bestTime = c.When;
				best = i;
			}
		}

		ActiveDma = best;
		if ( best < 0 )
			CpuBlocked = false;
	}

	public int ServiceUnit()
	{
		int number = ActiveDma;
		var ch = Channels[number];

		uint width = (uint)(2 << ((ch.Reg >> 10) & 1));
		uint source = ch.NextSource;
		uint dest = ch.NextDest;
		int srcRegion = (int)(source >> 24) & 0xF;
		int dstRegion = (int)(dest >> 24) & 0xF;

		CpuBlocked = true;
		PerformingDma = 1 | (number << 1);

		int cycles = 2 + CalculateAccessCycles( ch, width, srcRegion, dstRegion, source );
		ch.When += cycles;

		TransferUnit( ch, width, source, dest, srcRegion, dstRegion );
		AdvanceAddresses( ch, width, source, dest, srcRegion, dstRegion );

		ch.NextCount--;
		PerformingDma = 0;

		for ( int i = 0; i < 4; i++ )
		{
			if ( i == number ) continue;
			var other = Channels[i];
			if ( (other.Reg & 0x8000) != 0 && other.NextCount > 0 && other.When < ch.When )
				other.When = ch.When;
		}

		if ( ch.NextCount == 0 )
			cycles += CompleteTransfer( ch, number, width, srcRegion, dstRegion );

		Update();
		return cycles;
	}

	private int CalculateAccessCycles( GbaDma ch, uint width, int srcRegion, int dstRegion, uint source )
	{
		if ( ch.IsFirstUnit )
		{
			ch.When = Gba.Cpu.Cycles;
			ch.IsFirstUnit = false;

			if ( width == 4 )
			{
				ch.Cycles = Gba.Memory.WaitstatesSeq32[srcRegion] + Gba.Memory.WaitstatesSeq32[dstRegion];
				return Gba.Memory.WaitstatesNonseq32[srcRegion] + Gba.Memory.WaitstatesNonseq32[dstRegion];
			}

			if ( source >= 0x02000000 )
				ch.Latch = Gba.Memory.Load32( source );

			ch.Cycles = Gba.Memory.WaitstatesSeq16[srcRegion] + Gba.Memory.WaitstatesSeq16[dstRegion];
			return Gba.Memory.WaitstatesNonseq16[srcRegion] + Gba.Memory.WaitstatesNonseq16[dstRegion];
		}

		return ch.Cycles;
	}

	private void TransferUnit( GbaDma ch, uint width, uint source, uint dest, int srcRegion, int dstRegion )
	{
		if ( width == 4 )
		{
			if ( source >= 0x02000000 )
				ch.Latch = Gba.Memory.Load32( source );
			if ( !ch.DestInvalid )
				Gba.Memory.Store32( dest, ch.Latch );
			Gba.Cpu.OpenBusPrefetch = ch.Latch;
		}
		else
		{
			ReadHalfword( ch, source, srcRegion );

			if ( dstRegion == 0xD && Gba.Savedata.Type == SavedataType.Eeprom )
				Gba.Savedata.WriteEEPROM( (ushort)(ch.Latch >> (8 * (int)(dest & 2))), ch.NextCount );
			else if ( !ch.DestInvalid )
				Gba.Memory.Store16( dest, (ushort)(ch.Latch >> (8 * (int)(dest & 2))) );

			Gba.Cpu.OpenBusPrefetch = (ch.Latch & 0xFFFF) | (ch.Latch << 16);
		}
	}

	private void ReadHalfword( GbaDma ch, uint source, int srcRegion )
	{
		if ( srcRegion == 0xD && Gba.Savedata.Type == SavedataType.Eeprom )
		{
			uint hw = Gba.Savedata.ReadEEPROM();
			ch.Latch = hw | (hw << 16);
		}
		else if ( source >= 0x02000000 )
		{
			uint hw = Gba.Memory.Load16( source );
			ch.Latch = hw | (hw << 16);
		}
	}

	private void AdvanceAddresses( GbaDma ch, uint width, uint source, uint dest, int srcRegion, int dstRegion )
	{
		ch.NextSource += (uint)ch.SourceOffset;
		ch.NextDest += (uint)ch.DestOffset;

		int newSrcRegion = (int)(ch.NextSource >> 24) & 0xF;
		int newDstRegion = (int)(ch.NextDest >> 24) & 0xF;
		if ( newSrcRegion == srcRegion && newDstRegion == dstRegion )
			return;

		if ( ch.NextSource >= 0x08000000 && ch.NextSource < 0x0E000000 )
			ch.SourceOffset = (int)width;
		else
			ch.SourceOffset = OffsetDir[(ch.Reg >> 7) & 3] * (int)width;

		if ( width == 4 )
			ch.Cycles = Gba.Memory.WaitstatesSeq32[newSrcRegion] + Gba.Memory.WaitstatesSeq32[newDstRegion];
		else
			ch.Cycles = Gba.Memory.WaitstatesSeq16[newSrcRegion] + Gba.Memory.WaitstatesSeq16[newDstRegion];
	}

	private int CompleteTransfer( GbaDma ch, int number, uint width, int srcRegion, int dstRegion )
	{
		int extraCycles = 0;

		if ( srcRegion < 8 || dstRegion < 8 )
		{
			ch.When += 2;

			bool otherPending = false;
			for ( int i = 0; i < 4; i++ )
			{
				if ( i == number ) continue;
				if ( (Channels[i].Reg & 0x8000) != 0 && Channels[i].NextCount > 0 )
				{
					otherPending = true;
					break;
				}
			}

			if ( !otherPending )
				extraCycles = 2;
		}

		bool repeat = (ch.Reg & 0x0200) != 0;
		int timing = (ch.Reg >> 12) & 3;
		bool noRepeat = !repeat || timing == 0;

		if ( !noRepeat && number == 3 && timing == 3 &&
			 Gba.Video.VCount == GbaConstants.VisibleLines + 1 )
			noRepeat = true;

		if ( noRepeat )
		{
			ch.Reg &= unchecked((ushort)~0x8000);
		}
		else if ( ((ch.Reg >> 5) & 3) == 3 )
		{
			ch.NextDest = ch.Dest & DstMask[number];
		}

		if ( (ch.Reg & 0x4000) != 0 )
			Gba.Io.RaiseIrq( (GbaIrq)(1 << (8 + number)) );

		return extraCycles;
	}

	private void ChargeCycles( int cycles )
	{
		Gba.Cpu.Cycles += cycles;
		Gba.Timers.Tick( cycles );
		Gba.Audio.Tick( cycles );
		Gba.Io.TickIrqDelay( cycles );
	}
}

public class GbaDma
{
	public int Index;
	public ushort SrcLow, SrcHigh;
	public ushort DstLow, DstHigh;
	public ushort Count;
	public ushort Reg;

	public uint NextSource;
	public uint NextDest;
	public int NextCount;
	public uint Latch;

	public long When;
	public int Cycles;
	public bool IsFirstUnit;

	public int SourceOffset;
	public int DestOffset;
	public bool DestInvalid;

	public uint Source => (uint)(SrcLow | (SrcHigh << 16));
	public uint Dest => (uint)(DstLow | (DstHigh << 16));

	public int EffectiveCount
	{
		get
		{
			int count = Count;
			if ( Index < 3 ) count &= 0x3FFF;
			if ( count == 0 ) count = Index == 3 ? 0x10000 : 0x4000;
			return count;
		}
	}

	public GbaDma( int index )
	{
		Index = index;
	}

	public void Reset()
	{
		SrcLow = SrcHigh = DstLow = DstHigh = Count = Reg = 0;
		NextSource = NextDest = 0;
		NextCount = 0;
		Latch = 0;
		When = 0;
		Cycles = 0;
		IsFirstUnit = false;
		SourceOffset = DestOffset = 0;
		DestInvalid = false;
	}
}
