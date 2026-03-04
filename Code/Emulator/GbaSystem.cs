namespace sGBA;

public class GbaSystem
{
	public Arm7Cpu Cpu { get; private set; }
	public MemoryBus Bus { get; private set; }
	public Ppu Ppu { get; private set; }
	public IoRegisters Io { get; private set; }
	public DmaController Dma { get; private set; }
	public TimerController Timers { get; private set; }
	public HleBios HleBios { get; private set; }
	public Apu Apu { get; private set; }
	public SaveManager Save { get; private set; }
	public GpioController Gpio { get; private set; }

	public bool IsRunning { get; set; }
	public int CyclesThisFrame { get; set; }
	public long TotalFrames { get; set; }
	public long TotalCycles { get; set; }

	public GbaSystem()
	{
		Bus = new MemoryBus( this );
		Cpu = new Arm7Cpu( this );
		Ppu = new Ppu( this );
		Io = new IoRegisters( this );
		Dma = new DmaController( this );
		Timers = new TimerController( this );
		HleBios = new HleBios( this );
		Apu = new Apu( this );
		Save = new SaveManager( this );
		Gpio = new GpioController( this );
	}

	public void LoadRom( byte[] romData )
	{
		Bus.LoadRom( romData );
		Save.DetectSaveType( romData );
		Gpio.DetectRtc( romData );
	}

	public void Reset()
	{
		Bus.Reset();
		Cpu.Reset();
		Ppu.Reset();
		Io.Reset();
		Dma.Reset();
		Timers.Reset();
		Apu.Reset();
		Save.Reset();
		Gpio.Reset();
		Bus.InstallHleBios();
		Cpu.SkipBios();

		Ppu.DispCnt = 0x0080;
		Io.PostBoot = 1;

		CyclesThisFrame = 0;
		TotalFrames = 0;
		TotalCycles = 0;
		IsRunning = true;
	}

	public void RunFrame()
	{
		if ( !IsRunning ) return;

		Ppu.FrameReady = false;
		Apu.BeginFrame();
		Io.CheckKeypadIrq();

		long frameBase = Cpu.Cycles;

		for ( int line = 0; line < GbaConstants.TotalLines; line++ )
		{
			long lineBase = frameBase + (long)line * GbaConstants.ScanlineCycles;
			RunCpuTo( lineBase + GbaConstants.HDrawCycles );
			Ppu.StartHBlank();
			RunCpuTo( lineBase + GbaConstants.ScanlineCycles );
			Ppu.StartHDraw();
		}

		long frameEnd = frameBase + GbaConstants.FrameCycles;
		if ( Cpu.Cycles < frameEnd )
			Cpu.Cycles = frameEnd;

		TotalFrames++;
		TotalCycles += GbaConstants.FrameCycles;
	}

	private void RunCpuTo( long target )
	{
		while ( Cpu.Cycles < target )
		{
			if ( Dma.ActiveDma >= 0 )
			{
				ProcessDma( target );
				continue;
			}

			if ( Cpu.Halted )
			{
				ProcessHalt( target );
				continue;
			}

			Cpu.Run( target );
		}
	}

	private void ProcessDma( long target )
	{
		while ( Dma.ActiveDma >= 0 && Cpu.Cycles < target )
		{
			var ch = Dma.Channels[Dma.ActiveDma];

			if ( ch.When > Cpu.Cycles )
			{
				long runTo = Math.Min( ch.When, target );
				if ( Cpu.Halted )
					AdvanceClock( (int)(runTo - Cpu.Cycles) );
				else
					Cpu.Run( runTo );
				continue;
			}

			int unitCost = Dma.ServiceUnit();
			AdvanceClock( unitCost );
		}
	}

	private void ProcessHalt( long target )
	{
		while ( Cpu.Cycles < target && Cpu.Halted )
		{
			long nextEvent = Timers.NextGlobalEvent;
			if ( Dma.ActiveDma >= 0 )
				nextEvent = Math.Min( nextEvent, Dma.Channels[Dma.ActiveDma].When );
			nextEvent = Math.Min( nextEvent, target );

			int chunk = Math.Max( 1, (int)(nextEvent - Cpu.Cycles) );
			AdvanceClock( chunk );

			if ( Dma.ActiveDma >= 0 && Dma.Channels[Dma.ActiveDma].When <= Cpu.Cycles )
				ProcessDma( target );
		}
	}

	private void AdvanceClock( int cycles )
	{
		Cpu.Cycles += cycles;
		Timers.Tick( cycles );
		Apu.Tick( cycles );
		Io.TickIrqDelay( cycles );
		Save.TickEepromSettle( cycles );
	}

	public void CheckIntrWait( IrqFlag irq )
	{
		if ( !Cpu.InIntrWait ) return;

		if ( (Cpu.IntrWaitFlags & (ushort)irq) != 0 )
		{
			Cpu.InIntrWait = false;
			Cpu.Halted = false;
			Io.IME = 1;
			Io.CheckIrq();
		}
	}

	public void SetKeyState( GbaKey key, bool pressed )
	{
		if ( pressed )
			Io.KeyInput &= (ushort)~(int)key;
		else
			Io.KeyInput |= (ushort)key;
		Io.CheckKeypadIrq();
	}
}

[Flags]
public enum GbaKey : ushort
{
	A = 1 << 0,
	B = 1 << 1,
	Select = 1 << 2,
	Start = 1 << 3,
	Right = 1 << 4,
	Left = 1 << 5,
	Up = 1 << 6,
	Down = 1 << 7,
	R = 1 << 8,
	L = 1 << 9,
}
