namespace sGBA;

public partial class Apu
{
	public GbaSystem Gba { get; }

	public const int SampleRate = 32768;
	public const int CyclesPerSample = GbaConstants.Arm7Clock / SampleRate;
	public const int SamplesPerFrame = (GbaConstants.FrameCycles + CyclesPerSample - 1) / CyclesPerSample;
	public short[] OutputBuffer { get; set; } = new short[SamplesPerFrame * 2];
	public int SamplesWritten { get; set; }

	private long _nextSampleCycle;
	private long _nextFrameSeqCycle;

	private const int TimingFactor = 4;
	private const int FrameCycles = 0x2000;
	private const int CyclesPerFrameSeq = TimingFactor * FrameCycles;
	private int _frameSeqStep;

	private long _totalCycles;

	public bool Enable;

	public ushort Sound1CntL, Sound1CntH, Sound1CntX;
	public ushort Sound2CntL, Sound2CntH;
	public ushort Sound3CntL, Sound3CntH, Sound3CntX;
	public ushort Sound4CntL, Sound4CntH;
	public ushort SoundCntL, SoundCntH, SoundCntX;
	public ushort SoundBias;

	private int _volumeRight;
	private int _volumeLeft;
	private bool _psgCh1Right, _psgCh2Right, _psgCh3Right, _psgCh4Right;
	private bool _psgCh1Left, _psgCh2Left, _psgCh3Left, _psgCh4Left;

	private int _psgVolume;
	private bool _volumeChA;
	private bool _volumeChB;
	private bool _chARight;
	private bool _chALeft;
	private bool _chATimer;
	private bool _chBRight;
	private bool _chBLeft;
	private bool _chBTimer;

	public byte[] WaveRam = new byte[32];

	private struct FifoState
	{
		public uint[] Buffer;
		public int Write, Read;
		public uint Internal;
		public int Remaining;
		public sbyte Sample;
	}

	private FifoState _fifoA;
	private FifoState _fifoB;

	private bool _ch1Playing;
	private int _ch1Frequency;
	private int _ch1Length;
	private bool _ch1Stop;
	private int _ch1DutyIndex;
	private int _ch1Duty;
	private int _ch1Sample;
	private long _ch1LastUpdate;

	private int _ch1EnvVolume;
	private int _ch1EnvStepTime;
	private bool _ch1EnvDirection;
	private int _ch1EnvInitVolume;
	private int _ch1EnvDead;
	private int _ch1EnvNextStep;

	private int _ch1SweepShift;
	private bool _ch1SweepDirection;
	private int _ch1SweepTime;
	private int _ch1SweepStep;
	private bool _ch1SweepEnable;
	private bool _ch1SweepOccurred;
	private int _ch1SweepRealFreq;

	private bool _ch2Playing;
	private int _ch2Frequency;
	private int _ch2Length;
	private bool _ch2Stop;
	private int _ch2DutyIndex;
	private int _ch2Duty;
	private int _ch2Sample;
	private long _ch2LastUpdate;

	private int _ch2EnvVolume;
	private int _ch2EnvStepTime;
	private bool _ch2EnvDirection;
	private int _ch2EnvInitVolume;
	private int _ch2EnvDead;
	private int _ch2EnvNextStep;

	private bool _ch3Playing;
	private bool _ch3Enable;
	private bool _ch3Size;
	private bool _ch3Bank;
	private int _ch3Volume;
	private int _ch3Rate;
	private int _ch3Length;
	private bool _ch3Stop;
	private int _ch3Window;
	private int _ch3Sample;
	private long _ch3NextUpdate;

	private bool _ch4Playing;
	private int _ch4Ratio;
	private int _ch4Frequency;
	private bool _ch4Power;
	private int _ch4Length;
	private bool _ch4Stop;
	private uint _ch4Lfsr;
	private int _ch4Sample;
	private long _ch4LastEvent;

	private int _ch4EnvVolume;
	private int _ch4EnvStepTime;
	private bool _ch4EnvDirection;
	private int _ch4EnvInitVolume;
	private int _ch4EnvDead;
	private int _ch4EnvNextStep;

	private static readonly int[][] DutyTable =
	[
		[0, 0, 0, 0, 0, 0, 0, 1],
		[1, 0, 0, 0, 0, 0, 0, 1],
		[1, 0, 0, 0, 0, 1, 1, 1],
		[0, 1, 1, 1, 1, 1, 1, 0],
	];

	public Apu( GbaSystem gba )
	{
		Gba = gba;
		SoundBias = 0x0200;
		_fifoA.Buffer = new uint[8];
		_fifoB.Buffer = new uint[8];
	}

	public void Reset()
	{
		SamplesWritten = 0;
		_frameSeqStep = 0;
		_totalCycles = 0;
		_nextSampleCycle = CyclesPerSample;
		_nextFrameSeqCycle = CyclesPerFrameSeq;

		Sound1CntL = Sound1CntH = Sound1CntX = 0;
		Sound2CntL = Sound2CntH = 0;
		Sound3CntL = Sound3CntH = Sound3CntX = 0;
		Sound4CntL = Sound4CntH = 0;
		SoundCntL = SoundCntH = SoundCntX = 0;
		SoundBias = 0x0200;

		Enable = false;
		_psgVolume = 0;
		_volumeChA = false;
		_volumeChB = false;
		_chARight = _chALeft = false;
		_chATimer = false;
		_chBRight = _chBLeft = false;
		_chBTimer = false;
		_volumeRight = _volumeLeft = 0;
		_psgCh1Right = _psgCh2Right = _psgCh3Right = _psgCh4Right = false;
		_psgCh1Left = _psgCh2Left = _psgCh3Left = _psgCh4Left = false;

		Array.Clear( WaveRam );

		ResetFifo( true, true );
		ResetFifo( false, true );

		_ch1Playing = _ch2Playing = _ch3Playing = _ch4Playing = false;
		_ch1EnvDead = _ch2EnvDead = _ch4EnvDead = 2;
		_ch1SweepTime = 8;
		_ch1EnvVolume = _ch2EnvVolume = _ch4EnvVolume = 0;
		_ch1EnvInitVolume = _ch2EnvInitVolume = _ch4EnvInitVolume = 0;
		_ch1EnvStepTime = _ch2EnvStepTime = _ch4EnvStepTime = 0;
		_ch1EnvDirection = _ch2EnvDirection = _ch4EnvDirection = false;
		_ch1EnvNextStep = _ch2EnvNextStep = _ch4EnvNextStep = 0;
		_ch1Frequency = _ch2Frequency = 0;
		_ch1Length = _ch2Length = _ch3Length = _ch4Length = 0;
		_ch1Stop = _ch2Stop = _ch3Stop = _ch4Stop = false;
		_ch1DutyIndex = _ch2DutyIndex = 0;
		_ch1Duty = _ch2Duty = 0;
		_ch1Sample = _ch2Sample = _ch3Sample = _ch4Sample = 0;
		_ch1LastUpdate = _ch2LastUpdate = 0;
		_ch3NextUpdate = 0;
		_ch4LastEvent = 0;
		_ch1SweepShift = 0;
		_ch1SweepDirection = false;
		_ch1SweepStep = 0;
		_ch1SweepEnable = false;
		_ch1SweepOccurred = false;
		_ch1SweepRealFreq = 0;
		_ch3Enable = false;
		_ch3Size = false;
		_ch3Bank = false;
		_ch3Volume = 0;
		_ch3Rate = 0;
		_ch3Window = 0;
		_ch4Ratio = 0;
		_ch4Frequency = 0;
		_ch4Power = false;
		_ch4Lfsr = 0;
	}

	public void ResetFifo( bool isA, bool clearLatched )
	{
		ref var fifo = ref isA ? ref _fifoA : ref _fifoB;
		Array.Clear( fifo.Buffer );
		fifo.Write = fifo.Read = 0;
		fifo.Internal = 0;
		fifo.Remaining = 0;
		if ( clearLatched ) fifo.Sample = 0;
	}

	public void BeginFrame()
	{
		SamplesWritten = 0;
	}

	public void WriteFifo( bool isA, uint value )
	{
		ref var fifo = ref isA ? ref _fifoA : ref _fifoB;
		fifo.Buffer[fifo.Write] = value;
		fifo.Write = (fifo.Write + 1) & 7;
	}

	public void OnTimerOverflow( int timer )
	{
		if ( !Enable ) return;

		if ( (_chALeft || _chARight) && (_chATimer ? 1 : 0) == timer )
			SampleFifo( ref _fifoA, 1 );

		if ( (_chBLeft || _chBRight) && (_chBTimer ? 1 : 0) == timer )
			SampleFifo( ref _fifoB, 2 );
	}

	private void SampleFifo( ref FifoState fifo, int dmaChannel )
	{
		int size = FifoSize( ref fifo );

		if ( 8 - size > 4 )
		{
			Gba.Dma.OnFifo( dmaChannel );
			size = FifoSize( ref fifo );
		}

		if ( fifo.Remaining == 0 )
		{
			if ( size > 0 )
			{
				fifo.Internal = fifo.Buffer[fifo.Read];
				fifo.Remaining = 4;
				fifo.Read = (fifo.Read + 1) & 7;
			}
			else
			{
				return;
			}
		}

		fifo.Sample = (sbyte)(fifo.Internal & 0xFF);
		fifo.Internal >>= 8;
		fifo.Remaining--;
	}

	private static int FifoSize( ref FifoState fifo )
	{
		return fifo.Write >= fifo.Read
			? fifo.Write - fifo.Read
			: 8 - fifo.Read + fifo.Write;
	}

	public void Tick( int cycles )
	{
		_totalCycles += cycles;

		if ( _totalCycles < _nextFrameSeqCycle && _totalCycles < _nextSampleCycle )
			return;

		while ( _totalCycles >= _nextFrameSeqCycle )
		{
			_nextFrameSeqCycle += CyclesPerFrameSeq;
			ClockFrameSequencer();
		}

		while ( _totalCycles >= _nextSampleCycle )
		{
			_nextSampleCycle += CyclesPerSample;
			if ( SamplesWritten < SamplesPerFrame )
			{
				short l, r;
				if ( Enable )
					MixSample( out l, out r );
				else
				{
					l = 0;
					r = 0;
				}

				OutputBuffer[SamplesWritten * 2] = l;
				OutputBuffer[SamplesWritten * 2 + 1] = r;
				SamplesWritten++;
			}
		}
	}
}
