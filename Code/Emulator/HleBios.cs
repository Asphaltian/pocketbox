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
			case 0x0E: BgAffineSet(); break;
			case 0x0F: ObjAffineSet(); break;
			case 0x10: BitUnPack(); break;
			case 0x11: LZ77UnCompWram(); break;
			case 0x12: LZ77UnCompVram(); break;
			case 0x13: HuffUnComp(); break;
			case 0x14: RLUnCompWram(); break;
			case 0x15: RLUnCompVram(); break;
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
		uint clearAddr = flag != 0 ? 0x02000000u : 0x03007E00u;
		for ( int i = 0; i < 0x200; i++ )
			Gba.Bus.Write8( clearAddr + (uint)i, 0 );

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

	private void RegisterRamReset()
	{
		uint flags = Gba.Cpu.Registers[0];

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
		}
		if ( (flags & 0x40) != 0 )
		{
			var apu = Gba.Apu;
			apu.WriteRegister( 0x84, 0 );
			apu.WriteRegister( 0x84, 0x80 );
			apu.WriteRegister( 0x80, 0 );
			apu.WriteRegister( 0x82, 0 );
			apu.SoundBias = 0x0200;
			apu.ResetFifo( true, true );
			apu.ResetFifo( false, true );
		}
		if ( (flags & 0x80) != 0 )
		{
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
