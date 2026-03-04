namespace sGBA;

public class DmaController
{
	private static readonly uint[] SrcMask = [0x07FFFFFEu, 0x0FFFFFFEu, 0x0FFFFFFEu, 0x0FFFFFFEu];
	private static readonly uint[] DstMask = [0x07FFFFFEu, 0x07FFFFFEu, 0x07FFFFFEu, 0x0FFFFFFEu];
	private static readonly int[] OffsetDir = [1, -1, 0, 1];

	public GbaSystem Gba { get; }
	public DmaChannel[] Channels = new DmaChannel[4];

	public int ActiveDma = -1;
	public bool CpuBlocked;
	public int PerformingDma;

	public DmaController( GbaSystem gba )
	{
		Gba = gba;
		for ( int i = 0; i < 4; i++ )
			Channels[i] = new DmaChannel( i );
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
		bool wasEnabled = (c.Control & 0x8000) != 0;

		value &= ch < 3 ? unchecked((ushort)0xF7E0) : unchecked((ushort)0xFFE0);
		c.Control = value;

		uint width = (uint)(2 << ((value >> 10) & 1));
		RecalculateOffsets( c, ch, width, value );

		if ( wasEnabled || (value & 0x8000) == 0 )
			return;

		c.NextSource = c.Source & SrcMask[ch] & ~(width - 1);
		c.NextDest = c.Destination & DstMask[ch] & ~(width - 1);
		c.DestInvalid = ch < 3 && c.Destination >= 0x08000000;

		int timing = (value >> 12) & 3;
		if ( timing == 0 )
		{
			ScheduleDma( c );
		}
		else if ( timing == 3 && (ch == 1 || ch == 2) )
		{
			c.Control = (ushort)((c.Control & ~0x0060) | 0x0040 | 0x0400);
			c.DestOffset = 0;
		}
	}

	private void RecalculateOffsets( DmaChannel c, int ch, uint width, ushort control )
	{
		uint src = c.Source & SrcMask[ch];
		if ( src >= 0x08000000 && src < 0x0E000000 )
			c.SourceOffset = (int)width;
		else
			c.SourceOffset = OffsetDir[(control >> 7) & 3] * (int)width;

		c.DestOffset = OffsetDir[(control >> 5) & 3] * (int)width;
	}

	private void ScheduleDma( DmaChannel c )
	{
		c.When = Gba.Cpu.Cycles + 3;
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
			if ( (c.Control & 0x8000) == 0 ) continue;
			if ( ((c.Control >> 12) & 3) != timing ) continue;
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
		if ( (c.Control & 0x8000) == 0 ) return;
		if ( ((c.Control >> 12) & 3) != 3 ) return;
		if ( c.NextCount != 0 ) return;

		ScheduleDma( c );
	}

	public void OnFifo( int channel )
	{
		if ( channel != 1 && channel != 2 ) return;
		var c = Channels[channel];
		if ( (c.Control & 0x8000) == 0 ) return;
		if ( ((c.Control >> 12) & 3) != 3 ) return;

		int srcRegion = (int)(c.NextSource >> 24) & 0xF;
		int dstRegion = (int)(c.NextDest >> 24) & 0xF;
		int nonseq = Gba.Bus.WaitstatesNonseq32[srcRegion] + Gba.Bus.WaitstatesNonseq32[dstRegion];
		int seq = Gba.Bus.WaitstatesSeq32[srcRegion] + Gba.Bus.WaitstatesSeq32[dstRegion];
		int totalCycles = (2 + nonseq) + 3 * (2 + seq);

		for ( int i = 0; i < 4; i++ )
		{
			if ( c.NextSource >= 0x02000000 )
				c.Latch = Gba.Bus.Read32( c.NextSource );
			Gba.Bus.Write32( c.NextDest, c.Latch );
			c.NextSource += (uint)c.SourceOffset;
		}

		Gba.Cpu.OpenBusPrefetch = c.Latch;
		ChargeCycles( totalCycles );

		if ( (c.Control & 0x4000) != 0 )
			Gba.Io.RaiseIrq( (IrqFlag)(1 << (8 + channel)) );
	}

	public void Update()
	{
		int best = -1;
		long bestTime = long.MaxValue;

		for ( int i = 0; i < 4; i++ )
		{
			var c = Channels[i];
			if ( (c.Control & 0x8000) != 0 && c.NextCount > 0 && c.When < bestTime )
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

		uint width = (uint)(2 << ((ch.Control >> 10) & 1));
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
			if ( (other.Control & 0x8000) != 0 && other.NextCount > 0 && other.When < ch.When )
				other.When = ch.When;
		}

		if ( ch.NextCount == 0 )
			cycles += CompleteTransfer( ch, number, width, srcRegion, dstRegion );

		Update();
		return cycles;
	}

	private int CalculateAccessCycles( DmaChannel ch, uint width, int srcRegion, int dstRegion, uint source )
	{
		if ( ch.IsFirstUnit )
		{
			ch.When = Gba.Cpu.Cycles;
			ch.IsFirstUnit = false;

			if ( width == 4 )
			{
				ch.SeqCycles = Gba.Bus.WaitstatesSeq32[srcRegion] + Gba.Bus.WaitstatesSeq32[dstRegion];
				return Gba.Bus.WaitstatesNonseq32[srcRegion] + Gba.Bus.WaitstatesNonseq32[dstRegion];
			}

			if ( source >= 0x02000000 )
				ch.Latch = Gba.Bus.Read32( source );

			ch.SeqCycles = Gba.Bus.WaitstatesSeq16[srcRegion] + Gba.Bus.WaitstatesSeq16[dstRegion];
			return Gba.Bus.WaitstatesNonseq16[srcRegion] + Gba.Bus.WaitstatesNonseq16[dstRegion];
		}

		return ch.SeqCycles;
	}

	private void TransferUnit( DmaChannel ch, uint width, uint source, uint dest, int srcRegion, int dstRegion )
	{
		if ( width == 4 )
		{
			if ( source >= 0x02000000 )
				ch.Latch = Gba.Bus.Read32( source );
			if ( !ch.DestInvalid )
				Gba.Bus.Write32( dest, ch.Latch );
			Gba.Cpu.OpenBusPrefetch = ch.Latch;
		}
		else
		{
			ReadHalfword( ch, source, srcRegion );

			if ( dstRegion == 0xD && Gba.Save.Type == SaveType.Eeprom )
				Gba.Save.WriteEeprom( (ushort)(ch.Latch >> (8 * (int)(dest & 2))), ch.NextCount );
			else if ( !ch.DestInvalid )
				Gba.Bus.Write16( dest, (ushort)(ch.Latch >> (8 * (int)(dest & 2))) );

			Gba.Cpu.OpenBusPrefetch = (ch.Latch & 0xFFFF) | (ch.Latch << 16);
		}
	}

	private void ReadHalfword( DmaChannel ch, uint source, int srcRegion )
	{
		if ( srcRegion == 0xD && Gba.Save.Type == SaveType.Eeprom )
		{
			uint hw = Gba.Save.ReadEeprom();
			ch.Latch = hw | (hw << 16);
		}
		else if ( source >= 0x02000000 )
		{
			uint hw = Gba.Bus.Read16( source );
			ch.Latch = hw | (hw << 16);
		}
	}

	private void AdvanceAddresses( DmaChannel ch, uint width, uint source, uint dest, int srcRegion, int dstRegion )
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
			ch.SourceOffset = OffsetDir[(ch.Control >> 7) & 3] * (int)width;

		if ( width == 4 )
			ch.SeqCycles = Gba.Bus.WaitstatesSeq32[newSrcRegion] + Gba.Bus.WaitstatesSeq32[newDstRegion];
		else
			ch.SeqCycles = Gba.Bus.WaitstatesSeq16[newSrcRegion] + Gba.Bus.WaitstatesSeq16[newDstRegion];
	}

	private int CompleteTransfer( DmaChannel ch, int number, uint width, int srcRegion, int dstRegion )
	{
		int extraCycles = 0;

		if ( srcRegion < 8 || dstRegion < 8 )
		{
			ch.When += 2;

			bool otherPending = false;
			for ( int i = 0; i < 4; i++ )
			{
				if ( i == number ) continue;
				if ( (Channels[i].Control & 0x8000) != 0 && Channels[i].NextCount > 0 )
				{
					otherPending = true;
					break;
				}
			}

			if ( !otherPending )
				extraCycles = 2;
		}

		bool repeat = (ch.Control & 0x0200) != 0;
		int timing = (ch.Control >> 12) & 3;
		bool noRepeat = !repeat || timing == 0;

		if ( !noRepeat && number == 3 && timing == 3 &&
			 Gba.Ppu.VCount == GbaConstants.VisibleLines + 1 )
			noRepeat = true;

		if ( noRepeat )
		{
			ch.Control &= unchecked((ushort)~0x8000);
		}
		else if ( ((ch.Control >> 5) & 3) == 3 )
		{
			ch.NextDest = ch.Destination & DstMask[number] & ~(width - 1);
		}

		if ( (ch.Control & 0x4000) != 0 )
			Gba.Io.RaiseIrq( (IrqFlag)(1 << (8 + number)) );

		return extraCycles;
	}

	private void ChargeCycles( int cycles )
	{
		Gba.Cpu.Cycles += cycles;
		Gba.Timers.Tick( cycles );
		Gba.Apu.Tick( cycles );
		Gba.Io.TickIrqDelay( cycles );
	}
}

public class DmaChannel
{
	public int Index;
	public ushort SrcLow, SrcHigh;
	public ushort DstLow, DstHigh;
	public ushort WordCount;
	public ushort Control;

	public uint NextSource;
	public uint NextDest;
	public int NextCount;
	public uint Latch;

	public long When;
	public int SeqCycles;
	public bool IsFirstUnit;

	public int SourceOffset;
	public int DestOffset;
	public bool DestInvalid;

	public uint Source => (uint)(SrcLow | (SrcHigh << 16));
	public uint Destination => (uint)(DstLow | (DstHigh << 16));

	public int EffectiveCount
	{
		get
		{
			int count = WordCount;
			if ( Index < 3 ) count &= 0x3FFF;
			if ( count == 0 ) count = Index == 3 ? 0x10000 : 0x4000;
			return count;
		}
	}

	public DmaChannel( int index )
	{
		Index = index;
	}

	public void Reset()
	{
		SrcLow = SrcHigh = DstLow = DstHigh = WordCount = Control = 0;
		NextSource = NextDest = 0;
		NextCount = 0;
		Latch = 0;
		When = 0;
		SeqCycles = 0;
		IsFirstUnit = false;
		SourceOffset = DestOffset = 0;
		DestInvalid = false;
	}
}
