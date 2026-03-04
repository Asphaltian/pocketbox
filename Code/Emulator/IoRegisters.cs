namespace sGBA;

public class IoRegisters
{
	public GbaSystem Gba { get; }

	public ushort IE;
	public ushort IF;
	public ushort IME;
	public ushort WaitCnt;
	public ushort KeyInput = 0x3FF;
	public ushort KeyCnt;
	public ushort Rcnt = 0x8000;
	public byte PostBoot;
	public bool HaltRequested;

	private const int IrqDelayBase = 7;
	private long _irqFireCycle = long.MaxValue;

	private ushort _fifoALatch;
	private ushort _fifoBLatch;
	private ushort _sioCnt;
	private ushort _keysLast = 0x400;

	public IoRegisters( GbaSystem gba )
	{
		Gba = gba;
	}

	public void Reset()
	{
		IE = 0;
		IF = 0;
		IME = 0;
		WaitCnt = 0;
		KeyInput = 0x3FF;
		KeyCnt = 0;
		Rcnt = 0x8000;
		_sioCnt = 0;
		_keysLast = 0x400;
		PostBoot = 0;
		HaltRequested = false;
		_irqFireCycle = long.MaxValue;
	}

	public void RaiseIrq( IrqFlag irq, int cyclesLate = 0 )
	{
		IF |= (ushort)irq;

		ushort matched = (ushort)(IE & (ushort)irq);
		if ( matched != 0 )
		{
			ushort biosIF = Gba.Bus.Read16( 0x03007FF8 );
			biosIF |= matched;
			Gba.Bus.Write16( 0x03007FF8, biosIF );
		}

		bool wasInIntrWait = Gba.Cpu.InIntrWait;
		Gba.CheckIntrWait( irq );

		if ( (IE & IF) != 0 )
		{
			Gba.Cpu.Halted = false;

			if ( wasInIntrWait )
			{
				if ( IME != 0 )
				{
					Gba.Cpu.IrqPending = true;
					_irqFireCycle = long.MaxValue;
				}
			}
			else if ( _irqFireCycle == long.MaxValue )
			{
				_irqFireCycle = Gba.Cpu.Cycles - cyclesLate + IrqDelayBase;
			}
		}
	}

	public void TickIrqDelay( int cycles )
	{
		if ( _irqFireCycle == long.MaxValue ) return;

		if ( Gba.Cpu.Cycles >= _irqFireCycle )
		{
			_irqFireCycle = long.MaxValue;
			if ( IME != 0 && (IE & IF) != 0 )
			{
				Gba.Cpu.IrqPending = true;
			}
		}
	}

	public void CheckIrq()
	{
		if ( (IE & IF) != 0 )
		{
			Gba.Cpu.Halted = false;

			if ( _irqFireCycle == long.MaxValue )
			{
				_irqFireCycle = Gba.Cpu.Cycles + IrqDelayBase;
			}
		}
	}

	public void CheckKeypadIrq()
	{
		if ( (KeyCnt & 0x4000) == 0 ) return;

		ushort pressed = (ushort)(~KeyInput & 0x3FF);
		ushort mask = (ushort)(KeyCnt & 0x3FF);
		bool isAnd = (KeyCnt & 0x8000) != 0;

		if ( isAnd )
		{
			if ( (pressed & mask) == mask )
			{
				if ( _keysLast == pressed ) return;
				_keysLast = pressed;
				RaiseIrq( IrqFlag.Keypad );
			}
			else
			{
				_keysLast = 0x400;
			}
		}
		else
		{
			if ( (pressed & mask) != 0 )
			{
				RaiseIrq( IrqFlag.Keypad );
			}
			else
			{
				_keysLast = 0x400;
			}
		}
	}

	public ushort Read16( uint offset )
	{
		switch ( offset )
		{
			case 0x000: return Gba.Ppu.DispCnt;
			case 0x002: return 0;
			case 0x004: return Gba.Ppu.DispStat;
			case 0x006: return (ushort)Gba.Ppu.VCount;
			case 0x008: return (ushort)(Gba.Ppu.BgCnt[0] & 0xDFFF);
			case 0x00A: return (ushort)(Gba.Ppu.BgCnt[1] & 0xDFFF);
			case 0x00C: return Gba.Ppu.BgCnt[2];
			case 0x00E: return Gba.Ppu.BgCnt[3];

			case 0x048: return (ushort)(Gba.Ppu.WinIn & 0x3F3F);
			case 0x04A: return (ushort)(Gba.Ppu.WinOut & 0x3F3F);

			case 0x050: return (ushort)(Gba.Ppu.BldCnt & 0x3FFF);
			case 0x052: return (ushort)(Gba.Ppu.BldAlpha & 0x1F1F);

			case 0x060:
			case 0x062:
			case 0x064:
			case 0x068:
			case 0x06C:
			case 0x070:
			case 0x072:
			case 0x074:
			case 0x078:
			case 0x07C:
			case 0x080:
			case 0x082:
			case 0x084:
			case 0x088:
				return Gba.Apu.ReadRegister( offset );
			case 0x066:
			case 0x06A:
			case 0x06E:
			case 0x076:
			case 0x07A:
			case 0x07E:
			case 0x086:
			case 0x08A:
				return 0;
			case 0x090:
			case 0x092:
			case 0x094:
			case 0x096:
			case 0x098:
			case 0x09A:
			case 0x09C:
			case 0x09E:
				{
					int readBank;
					if ( (Gba.Apu.SoundCntX & 0x80) == 0 )
						readBank = 1;
					else
						readBank = (Gba.Apu.Sound3CntL & 0x40) != 0 ? 0 : 1;
					int waveOff = readBank * 16 + (int)(offset - 0x090);
					return (ushort)(Gba.Apu.WaveRam[waveOff] | (Gba.Apu.WaveRam[waveOff + 1] << 8));
				}

			case 0x0B8: return 0;
			case 0x0BA: return (ushort)(Gba.Dma.Channels[0].Control & 0xF7E0);
			case 0x0C4: return 0;
			case 0x0C6: return (ushort)(Gba.Dma.Channels[1].Control & 0xF7E0);
			case 0x0D0: return 0;
			case 0x0D2: return (ushort)(Gba.Dma.Channels[2].Control & 0xF7E0);
			case 0x0DC: return 0;
			case 0x0DE: return (ushort)(Gba.Dma.Channels[3].Control & 0xFFE0);

			case 0x100: return Gba.Timers.GetCounter( 0 );
			case 0x102: return Gba.Timers.Channels[0].Control;
			case 0x104: return Gba.Timers.GetCounter( 1 );
			case 0x106: return Gba.Timers.Channels[1].Control;
			case 0x108: return Gba.Timers.GetCounter( 2 );
			case 0x10A: return Gba.Timers.Channels[2].Control;
			case 0x10C: return Gba.Timers.GetCounter( 3 );
			case 0x10E: return Gba.Timers.Channels[3].Control;

			case 0x120:
			case 0x122:
			case 0x124:
			case 0x126:
			case 0x128: return _sioCnt;
			case 0x12A:
			case 0x12C:
			case 0x12E:
				return 0;
			case 0x130:
				return KeyInput;
			case 0x132: return KeyCnt;
			case 0x134:
				return Rcnt;
			case 0x136:
			case 0x138:
			case 0x13A:
			case 0x13C:
			case 0x13E:
			case 0x140:
			case 0x142:
			case 0x150:
			case 0x152:
			case 0x154:
			case 0x156:
			case 0x158:
			case 0x15A:
				return 0;

			case 0x200: return IE;
			case 0x202: return IF;
			case 0x204: return WaitCnt;
			case 0x206: return 0;
			case 0x208: return IME;
			case 0x20A: return 0;
			case 0x300: return PostBoot;
			case 0x302: return 0;

			default:
				return (ushort)Gba.Cpu.OpenBusPrefetch;
		}
	}

	public void Write8( uint offset, byte value )
	{
		if ( offset == 0x301 )
		{
			Gba.Cpu.Halted = true;
			return;
		}
		if ( offset == 0x300 )
		{
			PostBoot = value;
			return;
		}

		if ( offset >= 0x060 && offset <= 0x089 )
		{
			uint halfOffset = offset & ~1u;
			bool highByte = (offset & 1) != 0;
			Gba.Apu.WriteRegisterByte( halfOffset, highByte, value );
			return;
		}

		uint halfOffset2 = offset & ~1u;
		ushort current = Read16( halfOffset2 );
		if ( (offset & 1) == 0 )
			current = (ushort)((current & 0xFF00) | value);
		else
			current = (ushort)((current & 0x00FF) | (value << 8));
		Write16( halfOffset2, current );
	}

	public void Write16( uint offset, ushort value )
	{
		switch ( offset )
		{
			case 0x000: Gba.Ppu.DispCnt = value; break;
			case 0x004:
			{
				ushort dispstat = (ushort)((Gba.Ppu.DispStat & 0x7) | (value & 0xFFF8));
				int lyc = (dispstat >> 8) & 0xFF;
				if ( Gba.Ppu.VCount == lyc )
				{
					if ( (dispstat & 0x0020) != 0 && (dispstat & 0x0004) == 0 )
						RaiseIrq( IrqFlag.VCountMatch );
					dispstat |= 0x0004;
				}
				else
				{
					dispstat &= unchecked((ushort)~0x0004);
				}
				Gba.Ppu.DispStat = dispstat;
				break;
			}
			case 0x006: break;
			case 0x008: Gba.Ppu.BgCnt[0] = value; break;
			case 0x00A: Gba.Ppu.BgCnt[1] = value; break;
			case 0x00C: Gba.Ppu.BgCnt[2] = value; break;
			case 0x00E: Gba.Ppu.BgCnt[3] = value; break;

			case 0x010: Gba.Ppu.BgHOfs[0] = (short)(value & 0x1FF); break;
			case 0x012: Gba.Ppu.BgVOfs[0] = (short)(value & 0x1FF); break;
			case 0x014: Gba.Ppu.BgHOfs[1] = (short)(value & 0x1FF); break;
			case 0x016: Gba.Ppu.BgVOfs[1] = (short)(value & 0x1FF); break;
			case 0x018: Gba.Ppu.BgHOfs[2] = (short)(value & 0x1FF); break;
			case 0x01A: Gba.Ppu.BgVOfs[2] = (short)(value & 0x1FF); break;
			case 0x01C: Gba.Ppu.BgHOfs[3] = (short)(value & 0x1FF); break;
			case 0x01E: Gba.Ppu.BgVOfs[3] = (short)(value & 0x1FF); break;

			case 0x020: Gba.Ppu.BgPA[0] = (short)value; break;
			case 0x022: Gba.Ppu.BgPB[0] = (short)value; break;
			case 0x024: Gba.Ppu.BgPC[0] = (short)value; break;
			case 0x026: Gba.Ppu.BgPD[0] = (short)value; break;
			case 0x028:
				Gba.Ppu.BgRefX[0] = (Gba.Ppu.BgRefX[0] & unchecked((int)0xFFFF0000)) | value;
				Gba.Ppu.BgX[0] = Gba.Ppu.BgRefX[0];
				break;
			case 0x02A:
				Gba.Ppu.BgRefX[0] = (int)((uint)(Gba.Ppu.BgRefX[0] & 0x0000FFFF) | ((uint)value << 16));
				Gba.Ppu.BgRefX[0] <<= 4; Gba.Ppu.BgRefX[0] >>= 4;
				Gba.Ppu.BgX[0] = Gba.Ppu.BgRefX[0];
				break;
			case 0x02C:
				Gba.Ppu.BgRefY[0] = (Gba.Ppu.BgRefY[0] & unchecked((int)0xFFFF0000)) | value;
				Gba.Ppu.BgY[0] = Gba.Ppu.BgRefY[0];
				break;
			case 0x02E:
				Gba.Ppu.BgRefY[0] = (int)((uint)(Gba.Ppu.BgRefY[0] & 0x0000FFFF) | ((uint)value << 16));
				Gba.Ppu.BgRefY[0] <<= 4; Gba.Ppu.BgRefY[0] >>= 4;
				Gba.Ppu.BgY[0] = Gba.Ppu.BgRefY[0];
				break;

			case 0x030: Gba.Ppu.BgPA[1] = (short)value; break;
			case 0x032: Gba.Ppu.BgPB[1] = (short)value; break;
			case 0x034: Gba.Ppu.BgPC[1] = (short)value; break;
			case 0x036: Gba.Ppu.BgPD[1] = (short)value; break;
			case 0x038:
				Gba.Ppu.BgRefX[1] = (Gba.Ppu.BgRefX[1] & unchecked((int)0xFFFF0000)) | value;
				Gba.Ppu.BgX[1] = Gba.Ppu.BgRefX[1];
				break;
			case 0x03A:
				Gba.Ppu.BgRefX[1] = (int)((uint)(Gba.Ppu.BgRefX[1] & 0x0000FFFF) | ((uint)value << 16));
				Gba.Ppu.BgRefX[1] <<= 4; Gba.Ppu.BgRefX[1] >>= 4;
				Gba.Ppu.BgX[1] = Gba.Ppu.BgRefX[1];
				break;
			case 0x03C:
				Gba.Ppu.BgRefY[1] = (Gba.Ppu.BgRefY[1] & unchecked((int)0xFFFF0000)) | value;
				Gba.Ppu.BgY[1] = Gba.Ppu.BgRefY[1];
				break;
			case 0x03E:
				Gba.Ppu.BgRefY[1] = (int)((uint)(Gba.Ppu.BgRefY[1] & 0x0000FFFF) | ((uint)value << 16));
				Gba.Ppu.BgRefY[1] <<= 4; Gba.Ppu.BgRefY[1] >>= 4;
				Gba.Ppu.BgY[1] = Gba.Ppu.BgRefY[1];
				break;

			case 0x040: Gba.Ppu.Win0H = value; break;
			case 0x042: Gba.Ppu.Win1H = value; break;
			case 0x044: Gba.Ppu.Win0V = value; break;
			case 0x046: Gba.Ppu.Win1V = value; break;
			case 0x048: Gba.Ppu.WinIn = value; break;
			case 0x04A: Gba.Ppu.WinOut = value; break;

			case 0x04C: Gba.Ppu.Mosaic = value; break;

			case 0x050: Gba.Ppu.BldCnt = value; break;
			case 0x052: Gba.Ppu.BldAlpha = value; break;
			case 0x054: Gba.Ppu.BldY = value; break;

			case 0x060:
			case 0x062:
			case 0x064:
			case 0x066:
			case 0x068:
			case 0x06A:
			case 0x06C:
			case 0x06E:
			case 0x070:
			case 0x072:
			case 0x074:
			case 0x076:
			case 0x078:
			case 0x07A:
			case 0x07C:
			case 0x07E:
			case 0x080:
			case 0x082:
			case 0x084:
			case 0x088:
			case 0x08A:
				Gba.Apu.WriteRegister( offset, value );
				break;
			case 0x090:
			case 0x092:
			case 0x094:
			case 0x096:
			case 0x098:
			case 0x09A:
			case 0x09C:
			case 0x09E:
				{
					int writeBank;
					if ( (Gba.Apu.SoundCntX & 0x80) == 0 )
						writeBank = 1;
					else
						writeBank = (Gba.Apu.Sound3CntL & 0x40) != 0 ? 0 : 1;
					int waveOff = writeBank * 16 + (int)(offset - 0x090);
					Gba.Apu.WaveRam[waveOff] = (byte)value;
					Gba.Apu.WaveRam[waveOff + 1] = (byte)(value >> 8);
					break;
				}
			case 0x0A0:
				_fifoALatch = value;
				break;
			case 0x0A2:
				Gba.Apu.WriteFifo( true, (uint)(_fifoALatch | (value << 16)) );
				break;
			case 0x0A4:
				_fifoBLatch = value;
				break;
			case 0x0A6:
				Gba.Apu.WriteFifo( false, (uint)(_fifoBLatch | (value << 16)) );
				break;

			case >= 0x0B0 and <= 0x0DE:
			{
				int ch = (int)(offset - 0x0B0) / 12;
				switch ( (offset - 0x0B0) % 12 )
				{
					case 0: Gba.Dma.Channels[ch].SrcLow = value; break;
					case 2: Gba.Dma.Channels[ch].SrcHigh = value; break;
					case 4: Gba.Dma.Channels[ch].DstLow = value; break;
					case 6: Gba.Dma.Channels[ch].DstHigh = value; break;
					case 8: Gba.Dma.Channels[ch].WordCount = value; break;
					case 10: Gba.Dma.WriteControl( ch, value ); break;
				}
				break;
			}

			case 0x100: Gba.Timers.Channels[0].Reload = value; break;
			case 0x102: Gba.Timers.WriteControl( 0, value ); break;
			case 0x104: Gba.Timers.Channels[1].Reload = value; break;
			case 0x106: Gba.Timers.WriteControl( 1, value ); break;
			case 0x108: Gba.Timers.Channels[2].Reload = value; break;
			case 0x10A: Gba.Timers.WriteControl( 2, value ); break;
			case 0x10C: Gba.Timers.Channels[3].Reload = value; break;
			case 0x10E: Gba.Timers.WriteControl( 3, value ); break;

			case 0x120:
			case 0x122:
			case 0x124:
			case 0x126:
			case 0x12A:
			case 0x12C:
			case 0x12E:
				break;
			case 0x128:
				_sioCnt = value;
				if ( (value & 0x0080) != 0 )
				{
					_sioCnt &= unchecked((ushort)~0x0080);
					if ( (value & 0x4000) != 0 )
						RaiseIrq( IrqFlag.Serial );
				}
				break;

			case 0x130: break;
			case 0x132:
				KeyCnt = value;
				CheckKeypadIrq();
				break;

			case 0x134:
				Rcnt = (ushort)((Rcnt & 0x1FF) | (value & 0xC000));
				break;
			case 0x136:
			case 0x138:
			case 0x13A:
			case 0x13C:
			case 0x13E:
			case 0x140:
			case 0x142:
			case 0x150:
			case 0x152:
			case 0x154:
			case 0x156:
			case 0x158:
				break;

			case 0x200: IE = value; CheckIrq(); break;
			case 0x202:
				IF &= (ushort)~value;
				break;
			case 0x204:
				WaitCnt = value;
				Gba.Bus.UpdateWaitstates( value );
				break;
			case 0x208: IME = (ushort)(value & 1); CheckIrq(); break;
			case 0x300: PostBoot = (byte)value; break;
		}
	}
}

[Flags]
public enum IrqFlag : ushort
{
	VBlank = 1 << 0,
	HBlank = 1 << 1,
	VCountMatch = 1 << 2,
	Timer0 = 1 << 3,
	Timer1 = 1 << 4,
	Timer2 = 1 << 5,
	Timer3 = 1 << 6,
	Serial = 1 << 7,
	Dma0 = 1 << 8,
	Dma1 = 1 << 9,
	Dma2 = 1 << 10,
	Dma3 = 1 << 11,
	Keypad = 1 << 12,
	GamePak = 1 << 13,
}
