namespace sGBA;

public partial class HleBios
{
	public GbaSystem Gba { get; }

	public bool HleActive;

	public int BiosStall;

	public HleBios( GbaSystem gba )
	{
		Gba = gba;
	}

	public bool HandleSwi( uint comment )
	{
		HleActive = true;
		BiosStall = 0;
		switch ( comment )
		{
			case 0x00: SoftReset(); break;
			case 0x01: RegisterRamReset(); break;
			case 0x02: Halt(); break;
			case 0x03: Stop(); break;
			case 0x04: IntrWait(); break;
			case 0x05: VBlankIntrWait(); break;
			case 0x06: Div(); break;
			case 0x07: DivArm(); break;
			case 0x08: Sqrt(); break;
			case 0x09: ArcTan(); break;
			case 0x0A: ArcTan2(); break;
			case 0x0B: CpuSet(); break;
			case 0x0C: CpuFastSet(); break;
			case 0x0D: GetBiosChecksum(); break;
			case 0x0E: BgAffineSet(); break;
			case 0x0F: ObjAffineSet(); break;
			case 0x10: BitUnPack(); break;
			case 0x11: LZ77UnCompWram(); break;
			case 0x12: LZ77UnCompVram(); break;
			case 0x13: HuffUnComp(); break;
			case 0x14: RLUnCompWram(); break;
			case 0x15: RLUnCompVram(); break;
			case 0x16: DiffUnFilterWram(); break;
			case 0x17: DiffUnFilterVram(); break;
			case 0x18: DiffUnFilter16(); break;
			case 0x19: SoundDriverMain(); break;
			case 0x1A: SoundDriverVSync(); break;
			case 0x1B: SoundChannelClear(); break;
			case 0x1F: MidiKey2Freq(); break;
			default:
				break;
		}
		HleActive = false;

		Gba.Bus.BiosPrefetch = 0xE3A02004;

		return true;
	}

	private void SoftReset()
	{
		byte flag = Gba.Bus.Read8( 0x03007FFA );
		for ( int i = 0; i < 0x200; i++ )
			Gba.Bus.Write8( 0x03007E00u + (uint)i, 0 );

		for ( int i = 0; i < 13; i++ )
			Gba.Cpu.Registers[i] = 0;

		Gba.Cpu.Registers[13] = GbaConstants.SpSys;
		Gba.Cpu.SpsrBank[1] = 0;
		Gba.Cpu.SpsrBank[2] = 0;

		Gba.Cpu.SwitchMode( CpuMode.IRQ );
		Gba.Cpu.Registers[13] = GbaConstants.SpIrq;
		Gba.Cpu.SwitchMode( CpuMode.Supervisor );
		Gba.Cpu.Registers[13] = GbaConstants.SpSvc;
		Gba.Cpu.SwitchMode( CpuMode.System );
		Gba.Cpu.Registers[13] = GbaConstants.SpSys;

		uint entry = flag != 0 ? 0x02000000u : 0x08000000u;
		Gba.Cpu.Registers[15] = entry;
		Gba.Cpu.FlushPipeline();
	}

	private void GetBiosChecksum()
	{
		Gba.Cpu.Registers[0] = 0xBAAE187F;
		Gba.Cpu.Registers[1] = 1;
		Gba.Cpu.Registers[3] = 0x4000;
	}

	private void RegisterRamReset()
	{
		uint flags = Gba.Cpu.Registers[0];
		var io = Gba.Io;

		io.Write16( 0x000, 0x0080 );

		if ( (flags & 0x01) != 0 )
			Gba.Bus.Ewram.AsSpan( 0, 0x40000 ).Clear();
		if ( (flags & 0x02) != 0 )
			Gba.Bus.Iwram.AsSpan( 0, 0x7E00 ).Clear();
		if ( (flags & 0x04) != 0 )
			Array.Clear( Gba.Bus.PaletteRam );
		if ( (flags & 0x08) != 0 )
			Gba.Bus.Vram.AsSpan( 0, 0x18000 ).Clear();
		if ( (flags & 0x10) != 0 )
			Array.Clear( Gba.Bus.Oam );
		if ( (flags & 0x20) != 0 )
		{
			io.Write16( 0x128, 0 );
			io.Write16( 0x134, 0x8000 );
			io.Write16( 0x120, 0 );
			io.Write16( 0x140, 0 );
			io.Write16( 0x150, 0 );
			io.Write16( 0x152, 0 );
			io.Write16( 0x154, 0 );
			io.Write16( 0x156, 0 );
		}
		if ( (flags & 0x40) != 0 )
		{
			var apu = Gba.Apu;
			apu.WriteRegister( 0x60, 0 );
			apu.WriteRegister( 0x62, 0 );
			apu.WriteRegister( 0x64, 0 );
			apu.WriteRegister( 0x68, 0 );
			apu.WriteRegister( 0x6C, 0 );
			apu.WriteRegister( 0x70, 0 );
			apu.WriteRegister( 0x72, 0 );
			apu.WriteRegister( 0x74, 0 );
			apu.WriteRegister( 0x78, 0 );
			apu.WriteRegister( 0x7C, 0 );
			apu.WriteRegister( 0x80, 0 );
			apu.WriteRegister( 0x82, 0 );
			apu.WriteRegister( 0x84, 0 );
			apu.SoundBias = 0x0200;
			Array.Clear( apu.WaveRam );
		}
		if ( (flags & 0x80) != 0 )
		{
			io.Write16( 0x004, 0 );
			io.Write16( 0x008, 0 );
			io.Write16( 0x00A, 0 );
			io.Write16( 0x00C, 0 );
			io.Write16( 0x00E, 0 );
			io.Write16( 0x010, 0 );
			io.Write16( 0x012, 0 );
			io.Write16( 0x014, 0 );
			io.Write16( 0x016, 0 );
			io.Write16( 0x018, 0 );
			io.Write16( 0x01A, 0 );
			io.Write16( 0x01C, 0 );
			io.Write16( 0x01E, 0 );
			io.Write16( 0x020, 0x0100 );
			io.Write16( 0x022, 0 );
			io.Write16( 0x024, 0 );
			io.Write16( 0x026, 0x0100 );
			io.Write16( 0x028, 0 );
			io.Write16( 0x02A, 0 );
			io.Write16( 0x02C, 0 );
			io.Write16( 0x02E, 0 );
			io.Write16( 0x030, 0x0100 );
			io.Write16( 0x032, 0 );
			io.Write16( 0x034, 0 );
			io.Write16( 0x036, 0x0100 );
			io.Write16( 0x038, 0 );
			io.Write16( 0x03A, 0 );
			io.Write16( 0x03C, 0 );
			io.Write16( 0x03E, 0 );
			io.Write16( 0x040, 0 );
			io.Write16( 0x042, 0 );
			io.Write16( 0x044, 0 );
			io.Write16( 0x046, 0 );
			io.Write16( 0x048, 0 );
			io.Write16( 0x04A, 0 );
			io.Write16( 0x04C, 0 );
			io.Write16( 0x050, 0 );
			io.Write16( 0x052, 0 );
			io.Write16( 0x054, 0 );
			for ( uint ch = 0; ch < 4; ch++ )
			{
				uint b = 0x0B0 + ch * 12;
				io.Write16( b, 0 );
				io.Write16( b + 2, 0 );
				io.Write16( b + 4, 0 );
				io.Write16( b + 6, 0 );
				io.Write16( b + 8, 0 );
				io.Write16( b + 10, 0 );
			}
			for ( uint t = 0; t < 4; t++ )
			{
				io.Write16( 0x100 + t * 4, 0 );
				io.Write16( 0x102 + t * 4, 0 );
			}
			io.Write16( 0x200, 0 );
			io.Write16( 0x202, 0xFFFF );
			io.Write16( 0x204, 0 );
			io.Write16( 0x208, 0 );
		}
	}

	private void Halt()
	{
		Gba.Cpu.Halted = true;
	}

	private void Stop()
	{
		Gba.Cpu.Halted = true;
	}

	private void IntrWait()
	{
		bool discardOld = Gba.Cpu.Registers[0] != 0;
		uint flags = Gba.Cpu.Registers[1];

		if ( discardOld )
		{
			uint biosIF = Gba.Bus.Read16( 0x03007FF8 );
			biosIF &= ~flags;
			Gba.Bus.Write16( 0x03007FF8, (ushort)biosIF );
		}

		Gba.Cpu.Halted = true;
		Gba.Cpu.IntrWaitFlags = (ushort)flags;
		Gba.Cpu.InIntrWait = true;
		Gba.Io.IME = 1;
	}

	private void VBlankIntrWait()
	{
		Gba.Cpu.Registers[0] = 1;
		Gba.Cpu.Registers[1] = 1;
		IntrWait();
	}
}
