namespace sGBA;

public class GbaMemory
{
	public byte[] Bios { get; set; }
	public byte[] Wram { get; set; }
	public byte[] Iwram { get; set; }
	public byte[] PaletteRam { get; set; }
	public byte[] Vram { get; set; }
	public byte[] Oam { get; set; }
	public byte[] Rom { get; set; }
	public byte[] Sram { get; set; }
	public ushort[] Io { get; set; }

	public Gba Gba { get; }

	public uint BiosPrefetch;

	public int[] WaitstatesNonseq16 = new int[16];
	public int[] WaitstatesNonseq32 = new int[16];
	public int[] WaitstatesSeq16 = new int[16];
	public int[] WaitstatesSeq32 = new int[16];

	private static readonly int[] RomWaitN = [4, 3, 2, 8];
	private static readonly int[] RomWaitS = [2, 1, 4, 1, 8, 1];

	public bool Prefetch;
	public uint LastPrefetchedPc;

	public bool Debug;
	public byte[] DebugString = new byte[0x100];
	public ushort DebugFlags;

	public GbaMemory( Gba gba )
	{
		Gba = gba;
		Bios = new byte[GbaConstants.BiosSize];
		Wram = new byte[GbaConstants.EwramSize];
		Iwram = new byte[GbaConstants.IwramSize];
		PaletteRam = new byte[GbaConstants.PaletteSize];
		Vram = new byte[GbaConstants.VramSize];
		Oam = new byte[GbaConstants.OamSize];
		Sram = new byte[GbaConstants.SramSize];
		Io = new ushort[GbaConstants.IoSize / 2];
		Rom = [];
		InitDefaultWaitstates();
	}

	public void Reset()
	{
		Array.Clear( Bios );
		Array.Clear( Wram );
		Array.Clear( Iwram );
		Array.Clear( PaletteRam );
		Array.Clear( Vram );
		Array.Clear( Oam );
		Array.Clear( Sram );
		Array.Clear( Io );
		BiosPrefetch = 0;
		Debug = false;
		Array.Clear( DebugString );
		DebugFlags = 0;
		InitDefaultWaitstates();
	}

	private void InitDefaultWaitstates()
	{
		int[] n16 = [0, 0, 2, 0, 0, 0, 0, 0, 4, 4, 4, 4, 4, 4, 4, 0];
		int[] n32 = [0, 0, 5, 0, 0, 1, 1, 0, 7, 7, 9, 9, 13, 13, 9, 0];
		int[] s16 = [0, 0, 2, 0, 0, 0, 0, 0, 2, 2, 4, 4, 8, 8, 4, 0];
		int[] s32 = [0, 0, 5, 0, 0, 1, 1, 0, 5, 5, 9, 9, 17, 17, 9, 0];
		Array.Copy( n16, WaitstatesNonseq16, 16 );
		Array.Copy( n32, WaitstatesNonseq32, 16 );
		Array.Copy( s16, WaitstatesSeq16, 16 );
		Array.Copy( s32, WaitstatesSeq32, 16 );
		Prefetch = false;
		LastPrefetchedPc = 0;
	}

	public int MemoryStall( uint pc, int wait )
	{
		int activeRegion = (int)((pc >> 24) & 0xF);
		if ( activeRegion < 8 || !Prefetch )
			return wait;

		int previousLoads = 0;
		uint dist = LastPrefetchedPc - pc;
		int maxLoads = 8;
		if ( dist < 16 )
		{
			previousLoads = (int)(dist >> 1);
			maxLoads -= previousLoads;
		}

		int s = WaitstatesSeq16[activeRegion];
		int stall = s + 1;
		int loads = 1;

		while ( stall < wait && loads < maxLoads )
		{
			stall += s;
			loads++;
		}

		LastPrefetchedPc = pc + (uint)(2 * (loads + previousLoads - 1));

		if ( stall > wait )
			wait = stall;

		wait -= WaitstatesNonseq16[activeRegion] - s;
		wait -= stall;

		return wait;
	}

	public void AdjustWaitstates( ushort waitcnt )
	{
		Prefetch = (waitcnt & 0x4000) != 0;

		int sramWait = RomWaitN[waitcnt & 3];
		int ws0N = RomWaitN[(waitcnt >> 2) & 3];
		int ws0S = RomWaitS[(waitcnt >> 4) & 1];
		int ws1N = RomWaitN[(waitcnt >> 5) & 3];
		int ws1S = RomWaitS[((waitcnt >> 7) & 1) + 2];
		int ws2N = RomWaitN[(waitcnt >> 8) & 3];
		int ws2S = RomWaitS[((waitcnt >> 10) & 1) + 4];

		WaitstatesNonseq16[8] = WaitstatesNonseq16[9] = ws0N;
		WaitstatesSeq16[8] = WaitstatesSeq16[9] = ws0S;
		WaitstatesNonseq32[8] = WaitstatesNonseq32[9] = ws0N + 1 + ws0S;
		WaitstatesSeq32[8] = WaitstatesSeq32[9] = ws0S + 1 + ws0S;

		WaitstatesNonseq16[10] = WaitstatesNonseq16[11] = ws1N;
		WaitstatesSeq16[10] = WaitstatesSeq16[11] = ws1S;
		WaitstatesNonseq32[10] = WaitstatesNonseq32[11] = ws1N + 1 + ws1S;
		WaitstatesSeq32[10] = WaitstatesSeq32[11] = ws1S + 1 + ws1S;

		WaitstatesNonseq16[12] = WaitstatesNonseq16[13] = ws2N;
		WaitstatesSeq16[12] = WaitstatesSeq16[13] = ws2S;
		WaitstatesNonseq32[12] = WaitstatesNonseq32[13] = ws2N + 1 + ws2S;
		WaitstatesSeq32[12] = WaitstatesSeq32[13] = ws2S + 1 + ws2S;

		WaitstatesNonseq16[14] = WaitstatesNonseq16[15] = sramWait;
		WaitstatesSeq16[14] = WaitstatesSeq16[15] = sramWait;
		WaitstatesNonseq32[14] = WaitstatesNonseq32[15] = sramWait + 1 + sramWait;
		WaitstatesSeq32[14] = WaitstatesSeq32[15] = sramWait + 1 + sramWait;
	}

	public void LoadRom( byte[] romData )
	{
		Rom = romData;
	}

	public void LoadBios( byte[] biosData )
	{
		Array.Copy( biosData, Bios, Math.Min( biosData.Length, GbaConstants.BiosSize ) );
	}

	public void InstallHleBios()
	{
		Array.Clear( Bios, 0, GbaConstants.BiosSize );

		WriteBiosWord( 0x00, 0xEAFFFFFE );
		WriteBiosWord( 0x04, 0xEAFFFFFE );
		WriteBiosWord( 0x08, 0xE1B0F00E );
		WriteBiosWord( 0x0C, 0xEAFFFFFE );
		WriteBiosWord( 0x10, 0xEAFFFFFE );
		WriteBiosWord( 0x14, 0xEAFFFFFE );
		WriteBiosWord( 0x18, 0xEA000042 );

		int off = 0x128;

		WriteBiosWord( off, 0xE92D500F ); off += 4;
		WriteBiosWord( off, 0xE3A00301 ); off += 4;
		WriteBiosWord( off, 0xE28FE000 ); off += 4;
		WriteBiosWord( off, 0xE510F004 ); off += 4;
		WriteBiosWord( off, 0xE8BD500F ); off += 4;
		WriteBiosWord( off, 0xE25EF004 ); off += 4;

		WriteBiosWord( off, 0xE3A02004 ); off += 4;
		WriteBiosWord( off, 0xE3A02004 );
	}

	private void WriteBiosWord( int offset, uint value )
	{
		Bios[offset + 0] = (byte)value;
		Bios[offset + 1] = (byte)(value >> 8);
		Bios[offset + 2] = (byte)(value >> 16);
		Bios[offset + 3] = (byte)(value >> 24);
	}

	public byte Load8( uint address )
	{
		int region = (int)(address >> 24);
		switch ( region )
		{
			case 0x0:
				if ( address < GbaConstants.BiosSize )
				{
					if ( Gba.Cpu.Gprs[15] < GbaConstants.BiosSize )
					{
						uint addr = address & 0x3FFF;
						BiosPrefetch = ReadWordFromArray( Bios, addr & ~3u );
						return Bios[addr];
					}
					if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
						GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Bad BIOS Load8: 0x{address:X8}" );
					return (byte)(BiosPrefetch >> (int)((address & 3) * 8));
				}
				return Gba.Bios.HleActive
					? (byte)0
					: (byte)(Gba.Cpu.OpenBusPrefetch >> (int)((address & 3) * 8));

			case 0x2: return Wram[address & 0x3FFFF];
			case 0x3: return Iwram[address & 0x7FFF];
			case 0x4: return ReadIO8( address );
			case 0x5: return PaletteRam[address & 0x3FF];
			case 0x6:
				{
					uint rawAddr = address & 0x1FFFF;
					if ( (rawAddr & 0x1C000) == 0x18000 && (Gba.Video.DispCnt & 7) >= 3 )
						return 0;
					return Vram[MapVramAddress( address )];
				}
			case 0x7: return Oam[address & 0x3FF];

			case 0x8:
			case 0x9:
			case 0xA:
			case 0xB:
			case 0xC:
			case 0xD:
				uint romAddr = address & 0x1FFFFFF;
				if ( romAddr < (uint)Rom.Length )
					return Rom[romAddr];
				return (byte)(romAddr >> 1 >> ((int)(address & 1) * 8));

			case 0xE:
			case 0xF:
				return Gba.Savedata.Read8( address );

			default:
				if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
					GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Bad memory Load8: 0x{address:X8}" );
				return (byte)(Gba.Cpu.OpenBusPrefetch >> (int)((address & 3) * 8));
		}
	}

	public ushort Load16( uint address )
	{
		int region = (int)(address >> 24);

		if ( region >= 0xE )
		{
			byte b = Gba.Savedata.Read8( address );
			return (ushort)(b * 0x0101);
		}

		address &= ~1u;
		switch ( region )
		{
			case 0x0:
				if ( address < GbaConstants.BiosSize )
				{
					if ( Gba.Cpu.Gprs[15] < GbaConstants.BiosSize )
					{
						uint addr = address & 0x3FFF;
						BiosPrefetch = ReadWordFromArray( Bios, addr & ~3u );
						return ReadHalfFromArray( Bios, addr );
					}
					if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
						GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Bad BIOS Load16: 0x{address:X8}" );
					return (ushort)(BiosPrefetch >> (int)((address & 2) * 8));
				}
				return Gba.Bios.HleActive
					? (ushort)0
					: (ushort)(Gba.Cpu.OpenBusPrefetch >> (int)((address & 2) * 8));

			case 0x2: return ReadHalfFromArray( Wram, address & 0x3FFFF );
			case 0x3: return ReadHalfFromArray( Iwram, address & 0x7FFF );
			case 0x4: return ReadIO16( address );
			case 0x5: return ReadHalfFromArray( PaletteRam, address & 0x3FF );
			case 0x6:
				{
					uint rawAddr = address & 0x1FFFF;
					if ( (rawAddr & 0x1C000) == 0x18000 && (Gba.Video.DispCnt & 7) >= 3 )
						return 0;
					return ReadHalfFromArray( Vram, MapVramAddress( address ) );
				}
			case 0x7: return ReadHalfFromArray( Oam, address & 0x3FF );

			case 0x8:
			case 0x9:
			case 0xA:
			case 0xB:
			case 0xC:
				{
					uint romAddr = address & 0x1FFFFFF;
					if ( Gba.Hardware.HasRtc && romAddr >= 0xC4 && romAddr <= 0xC8 && (romAddr & 1) == 0 )
						return Gba.Hardware.GpioRead( romAddr );
					if ( romAddr < (uint)Rom.Length - 1 )
						return ReadHalfFromArray( Rom, romAddr );
					return (ushort)(romAddr >> 1);
				}
			case 0xD:
				{
					if ( Gba.Savedata.Type == SavedataType.Eeprom )
						return Gba.Savedata.ReadEEPROM();
					uint romAddr = address & 0x1FFFFFF;
					if ( romAddr < (uint)Rom.Length - 1 )
						return ReadHalfFromArray( Rom, romAddr );
					if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
						GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Out of bounds ROM Load16: 0x{address:X8}" );
					return (ushort)(romAddr >> 1);
				}

			default:
				if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
					GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Bad memory Load16: 0x{address:X8}" );
				return (ushort)(Gba.Cpu.OpenBusPrefetch >> (int)((address & 2) * 8));
		}
	}

	public uint Load32( uint address )
	{
		int region = (int)(address >> 24);

		if ( region >= 0xE )
		{
			byte b = Gba.Savedata.Read8( address );
			return (uint)(b | (b << 8) | (b << 16) | (b << 24));
		}

		address &= ~3u;
		switch ( region )
		{
			case 0x0:
				if ( address < GbaConstants.BiosSize )
				{
					if ( Gba.Cpu.Gprs[15] < GbaConstants.BiosSize )
					{
						uint addr = address & 0x3FFF;
						BiosPrefetch = ReadWordFromArray( Bios, addr );
						return BiosPrefetch;
					}
					if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
						GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Bad BIOS Load32: 0x{address:X8}" );
					return BiosPrefetch;
				}
				return Gba.Bios.HleActive ? 0u : Gba.Cpu.OpenBusPrefetch;

			case 0x2: return ReadWordFromArray( Wram, address & 0x3FFFF );
			case 0x3: return ReadWordFromArray( Iwram, address & 0x7FFF );
			case 0x4: return ReadIO32( address );
			case 0x5: return ReadWordFromArray( PaletteRam, address & 0x3FF );
			case 0x6:
				{
					uint rawAddr = address & 0x1FFFF;
					if ( (rawAddr & 0x1C000) == 0x18000 && (Gba.Video.DispCnt & 7) >= 3 )
						return 0;
					return ReadWordFromArray( Vram, MapVramAddress( address ) );
				}
			case 0x7: return ReadWordFromArray( Oam, address & 0x3FF );

			case 0x8:
			case 0x9:
			case 0xA:
			case 0xB:
			case 0xC:
			case 0xD:
				uint romAddr = address & 0x1FFFFFF;
				if ( romAddr < (uint)Rom.Length - 3 )
					return ReadWordFromArray( Rom, romAddr );
				if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
					GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Out of bounds ROM Load32: 0x{address:X8}" );
				return (romAddr >> 1) & 0xFFFF | ((romAddr >> 1) + 1) << 16;

			default:
				if ( GbaLog.FilterTest( LogCategory.GBAMem, LogLevel.GameError ) )
					GbaLog.Write( LogCategory.GBAMem, LogLevel.GameError, $"Bad memory Load32: 0x{address:X8}" );
				return Gba.Cpu.OpenBusPrefetch;
		}
	}

	public void Store8( uint address, byte value )
	{
		int region = (int)(address >> 24);
		switch ( region )
		{
			case 0x2: Wram[address & 0x3FFFF] = value; break;
			case 0x3: Iwram[address & 0x7FFF] = value; break;
			case 0x4: WriteIO8( address, value ); break;
			case 0x5:
				{
					uint addr = address & 0x3FE;
					PaletteRam[addr] = value;
					PaletteRam[addr + 1] = value;
				}
				break;
			case 0x6:
				{
					uint objThreshold = (uint)((Gba.Video.DispCnt & 7) >= 3 ? 0x14000 : 0x10000);
					if ( (address & 0x1FFFF) >= objThreshold )
						break;
					uint addr = address & 0x1FFFE;
					Vram[addr] = value;
					Vram[addr + 1] = value;
				}
				break;
			case 0x7: break;
			case 0xE:
			case 0xF:
				Gba.Savedata.Write8( address, value );
				break;
		}
	}

	public void Store16( uint address, ushort value )
	{
		int region = (int)(address >> 24);
		if ( region >= 0xE )
		{
			byte b = (address & 1) != 0 ? (byte)(value >> 8) : (byte)value;
			Gba.Savedata.Write8( address, b );
			return;
		}

		address &= ~1u;
		switch ( region )
		{
			case 0x2: WriteHalfToArray( Wram, address & 0x3FFFF, value ); break;
			case 0x3: WriteHalfToArray( Iwram, address & 0x7FFF, value ); break;
			case 0x4: WriteIO16( address, value ); break;
			case 0x5: WriteHalfToArray( PaletteRam, address & 0x3FF, value ); break;
			case 0x6:
				{
					uint rawAddr = address & 0x1FFFF;
					if ( (rawAddr & 0x1C000) == 0x18000 && (Gba.Video.DispCnt & 7) >= 3 )
						break;
					WriteHalfToArray( Vram, MapVramAddress( address ), value );
				}
				break;
			case 0x7: WriteHalfToArray( Oam, address & 0x3FF, value ); break;
			case 0x8:
			case 0x9:
				{
					uint romAddr = address & 0x1FFFFFF;
					if ( Gba.Hardware.HasRtc && romAddr >= 0xC4 && romAddr <= 0xC8 && (romAddr & 1) == 0 )
						Gba.Hardware.GpioWrite( romAddr, value );
					break;
				}
			case 0xD:
				if ( Gba.Savedata.Type == SavedataType.Eeprom )
					Gba.Savedata.WriteEEPROM( value, 1 );
				break;
		}
	}

	public void Store32( uint address, uint value )
	{
		int region = (int)(address >> 24);
		if ( region >= 0xE )
		{
			Gba.Savedata.Write8( address, (byte)(value >> (int)(8 * (address & 3))) );
			return;
		}

		address &= ~3u;
		switch ( region )
		{
			case 0x2: WriteWordToArray( Wram, address & 0x3FFFF, value ); break;
			case 0x3: WriteWordToArray( Iwram, address & 0x7FFF, value ); break;
			case 0x4: WriteIO32( address, value ); break;
			case 0x5: WriteWordToArray( PaletteRam, address & 0x3FF, value ); break;
			case 0x6:
				{
					uint rawAddr = address & 0x1FFFF;
					if ( (rawAddr & 0x1C000) == 0x18000 && (Gba.Video.DispCnt & 7) >= 3 )
						break;
					WriteWordToArray( Vram, MapVramAddress( address ), value );
				}
				break;
			case 0x7: WriteWordToArray( Oam, address & 0x3FF, value ); break;
		}
	}

	private byte ReadIO8( uint address )
	{
		uint offset = address & 0x00FFFFFF;
		if ( offset >= 0xFFF600 && offset < 0xFFF700 )
		{
			if ( Debug )
				return DebugString[offset - 0xFFF600];
			return 0;
		}
		if ( offset >= 0x400 ) return (byte)(Gba.Cpu.OpenBusPrefetch >> ((int)(address & 3) * 8));
		ushort val = Gba.Io.Read16( offset & ~1u );
		return (address & 1) == 0 ? (byte)val : (byte)(val >> 8);
	}

	private ushort ReadIO16( uint address )
	{
		uint offset = address & 0x00FFFFFF;
		if ( offset >= 0xFFF600 )
		{
			if ( offset >= 0xFFF600 && offset < 0xFFF700 && Debug )
			{
				uint idx = offset - 0xFFF600;
				if ( idx + 1 < 0x100 )
					return (ushort)(DebugString[idx] | (DebugString[idx + 1] << 8));
				return 0;
			}
			if ( offset == 0xFFF700 ) return DebugFlags;
			if ( offset == 0xFFF780 ) return Debug ? (ushort)0x1DEA : (ushort)0;
			return 0;
		}
		if ( offset >= 0x400 ) return (ushort)Gba.Cpu.OpenBusPrefetch;
		return Gba.Io.Read16( offset );
	}

	private uint ReadIO32( uint address )
	{
		uint offset = address & 0x00FFFFFF;
		if ( offset >= 0xFFF600 )
		{
			ushort lo = ReadIO16( address );
			ushort hi = ReadIO16( address + 2 );
			return (uint)(lo | (hi << 16));
		}
		if ( offset >= 0x400 ) return Gba.Cpu.OpenBusPrefetch;
		ushort lo2 = Gba.Io.Read16( offset );
		ushort hi2 = Gba.Io.Read16( offset + 2 );
		return (uint)(lo2 | (hi2 << 16));
	}

	private void WriteIO8( uint address, byte value )
	{
		uint offset = address & 0x00FFFFFF;
		if ( offset >= 0xFFF600 && offset < 0xFFF700 )
		{
			if ( Debug )
				DebugString[offset - 0xFFF600] = value;
			return;
		}
		if ( offset >= 0x400 ) return;
		Gba.Io.Write8( offset, value );
	}

	private void WriteIO16( uint address, ushort value )
	{
		uint offset = address & 0x00FFFFFF;
		if ( offset >= 0xFFF600 )
		{
			if ( offset >= 0xFFF600 && offset < 0xFFF700 && Debug )
			{
				uint idx = offset - 0xFFF600;
				if ( idx + 1 < 0x100 )
				{
					DebugString[idx] = (byte)value;
					DebugString[idx + 1] = (byte)(value >> 8);
				}
				return;
			}
			if ( offset == 0xFFF700 )
			{
				if ( Debug )
					HandleDebugFlags( value );
				return;
			}
			if ( offset == 0xFFF780 )
			{
				Debug = value == 0xC0DE;
				return;
			}
			return;
		}
		if ( offset >= 0x400 ) return;
		Gba.Io.Write16( offset, value );
	}

	private void WriteIO32( uint address, uint value )
	{
		uint offset = address & 0x00FFFFFF;
		if ( offset >= 0xFFF600 )
		{
			if ( offset >= 0xFFF600 && offset < 0xFFF700 && Debug )
			{
				uint idx = offset - 0xFFF600;
				if ( idx + 3 < 0x100 )
				{
					DebugString[idx] = (byte)value;
					DebugString[idx + 1] = (byte)(value >> 8);
					DebugString[idx + 2] = (byte)(value >> 16);
					DebugString[idx + 3] = (byte)(value >> 24);
				}
				return;
			}
			WriteIO16( address, (ushort)value );
			WriteIO16( address + 2, (ushort)(value >> 16) );
			return;
		}
		if ( offset >= 0x400 ) return;
		Gba.Io.Write16( offset, (ushort)value );
		Gba.Io.Write16( offset + 2, (ushort)(value >> 16) );
	}

	private void HandleDebugFlags( ushort flags )
	{
		DebugFlags = flags;
		bool send = (flags & 0x100) != 0;
		if ( !send ) return;

		int level = (1 << (flags & 0x7)) & 0x1F;

		int len = 0;
		while ( len < 0x100 && DebugString[len] != 0 ) len++;
		string msg = System.Text.Encoding.ASCII.GetString( DebugString, 0, len );
		Array.Clear( DebugString );
		DebugFlags = (ushort)(flags & ~0x100);

		GbaLog.Write( LogCategory.GBADebug, (LogLevel)level, msg );
	}

	public static uint MapVramAddress( uint address )
	{
		uint addr = address & 0x1FFFF;
		if ( addr >= 0x18000 ) addr -= 0x8000;
		return addr;
	}

	private static ushort ReadHalfFromArray( byte[] arr, uint offset )
	{
		return (ushort)(arr[offset] | (arr[offset + 1] << 8));
	}

	private static uint ReadWordFromArray( byte[] arr, uint offset )
	{
		return (uint)(arr[offset] | (arr[offset + 1] << 8) | (arr[offset + 2] << 16) | (arr[offset + 3] << 24));
	}

	private static void WriteHalfToArray( byte[] arr, uint offset, ushort value )
	{
		arr[offset] = (byte)value;
		arr[offset + 1] = (byte)(value >> 8);
	}

	private static void WriteWordToArray( byte[] arr, uint offset, uint value )
	{
		arr[offset] = (byte)value;
		arr[offset + 1] = (byte)(value >> 8);
		arr[offset + 2] = (byte)(value >> 16);
		arr[offset + 3] = (byte)(value >> 24);
	}
}
