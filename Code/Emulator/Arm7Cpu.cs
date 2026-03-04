namespace sGBA;

public partial class Arm7Cpu
{
	public uint[] R = new uint[16];
	public uint[] Registers => R;

	public bool FlagN, FlagZ, FlagC, FlagV;
	public bool IrqDisable = true;
	public bool FiqDisable = true;
	public bool ThumbMode;
	public CpuMode Mode = CpuMode.System;

	private readonly uint[][] _bankedRegs = new uint[6][];
	private readonly uint[] _bankedSpsr = new uint[6];
	private readonly uint[] _fiqRegsHi = new uint[5];
	private readonly uint[] _usrRegsHi = new uint[5];

	public long Cycles;
	public bool Halted;
	public bool IrqPending;
	public bool InIntrWait;
	public bool InIrqContext;
	public ushort IntrWaitFlags;
	public uint OpenBusPrefetch;

	public uint[] PcTrace = new uint[128];
	public bool[] PcTraceThumb = new bool[64];
	public int PcTraceIndex;

	public bool CrashDetected;
	public uint CrashPc;
	public uint CrashCpsr;
	public uint[] CrashRegs;
	public bool CrashThumb;

	public uint[] SpsrBank => _bankedSpsr;

	private uint _pipeline0;
	private uint _pipeline1;
	private bool _pipelineFlushed = true;

	public GbaSystem Gba { get; }
	private MemoryBus Bus => Gba.Bus;

	public Arm7Cpu( GbaSystem gba )
	{
		Gba = gba;
		for ( int i = 0; i < 6; i++ )
			_bankedRegs[i] = new uint[2];
	}

	public void Reset()
	{
		Array.Clear( R );
		FlagN = FlagZ = FlagC = FlagV = false;
		IrqDisable = true;
		FiqDisable = true;
		ThumbMode = false;
		Mode = CpuMode.System;
		Halted = false;
		IrqPending = false;
		InIntrWait = false;
		IntrWaitFlags = 0;
		_pipelineFlushed = true;
		Cycles = 0;

		for ( int i = 0; i < 6; i++ )
		{
			_bankedRegs[i][0] = 0;
			_bankedRegs[i][1] = 0;
			_bankedSpsr[i] = 0;
		}
	}

	public void SkipBios()
	{
		SwitchMode( CpuMode.IRQ );
		R[13] = GbaConstants.SpIrq;
		SwitchMode( CpuMode.Supervisor );
		R[13] = GbaConstants.SpSvc;
		SwitchMode( CpuMode.System );
		R[13] = GbaConstants.SpSys;

		IrqDisable = false;
		FiqDisable = false;
		R[15] = 0x08000000;
		_pipelineFlushed = true;
	}

	public void Run( long targetCycles )
	{
		while ( Cycles < targetCycles )
		{
			if ( CrashDetected )
			{
				Cycles = targetCycles;
				return;
			}

			if ( Halted )
				return;

			if ( Gba.Dma.ActiveDma >= 0 && Gba.Dma.Channels[Gba.Dma.ActiveDma].When <= Cycles )
				return;

			Step();
		}
	}

	private void Step()
	{
		long cyclesBefore = Cycles;

		if ( _pipelineFlushed )
		{
			FlushPipeline();
			_pipelineFlushed = false;
		}

		if ( IrqPending && !IrqDisable )
		{
			RaiseIrq();
			IrqPending = false;
			FlushPipeline();
			_pipelineFlushed = false;
		}

		if ( InIntrWait && !Halted && !IrqDisable && !InIrqContext )
		{
			Halted = true;
			return;
		}

		uint instrAddr = ThumbMode ? R[15] - 4 : R[15] - 8;

		if ( !IsExecutableAddress( instrAddr ) )
		{
			if ( !CrashDetected )
			{
				CrashDetected = true;
				CrashPc = instrAddr;
				CrashCpsr = GetCpsrRaw();
				CrashRegs = new uint[16];
				Array.Copy( R, CrashRegs, 16 );
				CrashThumb = ThumbMode;
			}
			return;
		}

		PcTrace[PcTraceIndex * 2] = instrAddr;
		PcTrace[PcTraceIndex * 2 + 1] = _pipeline0;
		PcTraceThumb[PcTraceIndex] = ThumbMode;
		PcTraceIndex = (PcTraceIndex + 1) & 63;

		if ( ThumbMode )
		{
			ExecuteThumb();
		}
		else
		{
			ExecuteArm();
		}

		int delta = (int)(Cycles - cyclesBefore);
		Gba.Timers.Tick( delta );
		Gba.Apu.Tick( delta );
		Gba.Io.TickIrqDelay( delta );
	}

	private static bool IsExecutableAddress( uint addr )
	{
		int region = (int)(addr >> 24);
		return region == 0x00 || region == 0x02 || region == 0x03 ||
			   (region >= 0x08 && region <= 0x0D);
	}

	public void FlushPipeline()
	{
		if ( ThumbMode )
		{
			R[15] &= ~1u;
			_pipeline0 = Bus.Read16( R[15] );
			R[15] += 2;
			_pipeline1 = Bus.Read16( R[15] );
			R[15] += 2;
		}
		else
		{
			R[15] &= ~3u;
			_pipeline0 = Bus.Read32( R[15] );
			R[15] += 4;
			_pipeline1 = Bus.Read32( R[15] );
			R[15] += 4;
		}

		int region = (int)((R[15] >> 24) & 0xF);
		if ( ThumbMode )
			Cycles += 2 + Bus.WaitstatesNonseq16[region] + Bus.WaitstatesSeq16[region];
		else
			Cycles += 2 + Bus.WaitstatesNonseq32[region] + Bus.WaitstatesSeq32[region];
	}

	public void RaiseIrq()
	{
		uint savedCpsr = GetCpsrRaw();
		SwitchMode( CpuMode.IRQ );
		SetSpsr( savedCpsr );
		R[14] = R[15] - (ThumbMode ? 0u : 4u);
		IrqDisable = true;
		ThumbMode = false;
		R[15] = GbaConstants.VectorIrq;
		_pipelineFlushed = true;
		Halted = false;
		if ( InIntrWait )
			InIrqContext = true;
	}

	public uint GetCpsrRaw()
	{
		uint cpsr = 0;
		if ( FlagN ) cpsr |= 0x80000000;
		if ( FlagZ ) cpsr |= 0x40000000;
		if ( FlagC ) cpsr |= 0x20000000;
		if ( FlagV ) cpsr |= 0x10000000;
		if ( IrqDisable ) cpsr |= 0x80;
		if ( FiqDisable ) cpsr |= 0x40;
		if ( ThumbMode ) cpsr |= 0x20;
		cpsr |= (uint)Mode;
		return cpsr;
	}

	public void SetCpsr( uint cpsr )
	{
		FlagN = (cpsr & 0x80000000) != 0;
		FlagZ = (cpsr & 0x40000000) != 0;
		FlagC = (cpsr & 0x20000000) != 0;
		FlagV = (cpsr & 0x10000000) != 0;
		IrqDisable = (cpsr & 0x80) != 0;
		FiqDisable = (cpsr & 0x40) != 0;
		ThumbMode = (cpsr & 0x20) != 0;

		CpuMode newMode = (CpuMode)(cpsr & 0x1F);
		if ( newMode != Mode && IsValidMode( newMode ) )
			SwitchMode( newMode );
	}

	private static bool IsValidMode( CpuMode mode )
	{
		return mode == CpuMode.User || mode == CpuMode.FIQ || mode == CpuMode.IRQ ||
			   mode == CpuMode.Supervisor || mode == CpuMode.Abort ||
			   mode == CpuMode.Undefined || mode == CpuMode.System;
	}

	public void SwitchMode( CpuMode newMode )
	{
		int oldBank = GetBankIndex( Mode );
		int newBank = GetBankIndex( newMode );

		if ( oldBank != newBank )
		{
			_bankedRegs[oldBank][0] = R[13];
			_bankedRegs[oldBank][1] = R[14];

			if ( Mode == CpuMode.FIQ )
			{
				for ( int i = 0; i < 5; i++ ) { _fiqRegsHi[i] = R[8 + i]; R[8 + i] = _usrRegsHi[i]; }
			}

			R[13] = _bankedRegs[newBank][0];
			R[14] = _bankedRegs[newBank][1];

			if ( newMode == CpuMode.FIQ )
			{
				for ( int i = 0; i < 5; i++ ) { _usrRegsHi[i] = R[8 + i]; R[8 + i] = _fiqRegsHi[i]; }
			}
		}

		Mode = newMode;
	}

	private int GetBankIndex( CpuMode mode )
	{
		switch ( mode )
		{
			case CpuMode.FIQ: return 0;
			case CpuMode.IRQ: return 1;
			case CpuMode.Supervisor: return 2;
			case CpuMode.Abort: return 3;
			case CpuMode.Undefined: return 4;
			default: return 5;
		}
	}

	public uint GetSpsr()
	{
		if ( Mode == CpuMode.User || Mode == CpuMode.System )
			return GetCpsrRaw();
		int bank = GetBankIndex( Mode );
		return _bankedSpsr[bank];
	}

	public void SetSpsr( uint value )
	{
		int bank = GetBankIndex( Mode );
		_bankedSpsr[bank] = value;
	}

	private uint GetUserReg( int reg )
	{
		if ( Mode == CpuMode.User || Mode == CpuMode.System )
			return R[reg];

		if ( reg >= 8 && reg <= 12 && Mode == CpuMode.FIQ )
			return _usrRegsHi[reg - 8];

		if ( reg == 13 || reg == 14 )
			return _bankedRegs[5][reg - 13];

		return R[reg];
	}

	private void SetUserReg( int reg, uint value )
	{
		if ( Mode == CpuMode.User || Mode == CpuMode.System )
		{
			R[reg] = value;
			return;
		}

		if ( reg >= 8 && reg <= 12 && Mode == CpuMode.FIQ )
		{
			_usrRegsHi[reg - 8] = value;
			return;
		}

		if ( reg == 13 || reg == 14 )
		{
			_bankedRegs[5][reg - 13] = value;
			return;
		}

		R[reg] = value;
	}

	private void SetLogicFlags( uint result, bool carry )
	{
		FlagN = (result & 0x80000000) != 0;
		FlagZ = result == 0;
		FlagC = carry;
	}

	private void SetAddFlags( uint a, uint b, uint result )
	{
		FlagN = (result & 0x80000000) != 0;
		FlagZ = result == 0;
		FlagC = result < a;
		FlagV = ((a ^ result) & (b ^ result) & 0x80000000) != 0;
	}

	private void SetSubFlags( uint a, uint b, uint result )
	{
		FlagN = (result & 0x80000000) != 0;
		FlagZ = result == 0;
		FlagC = a >= b;
		FlagV = ((a ^ b) & (a ^ result) & 0x80000000) != 0;
	}

	private void SetAdcFlags( uint a, uint b, bool carry )
	{
		ulong result = (ulong)a + b + (carry ? 1u : 0u);
		uint r = (uint)result;
		FlagN = (r & 0x80000000) != 0;
		FlagZ = r == 0;
		FlagC = result > 0xFFFFFFFF;
		FlagV = ((a ^ r) & (b ^ r) & 0x80000000) != 0;
	}

	private void SetSbcFlags( uint a, uint b, bool carry )
	{
		uint borrow = carry ? 0u : 1u;
		ulong result = (ulong)a - b - borrow;
		uint r = (uint)result;
		FlagN = (r & 0x80000000) != 0;
		FlagZ = r == 0;
		FlagC = a >= (ulong)b + borrow;
		FlagV = ((a ^ b) & (a ^ r) & 0x80000000) != 0;
	}

	private bool CheckCondition( uint cond )
	{
		switch ( cond )
		{
			case 0x0: return FlagZ;
			case 0x1: return !FlagZ;
			case 0x2: return FlagC;
			case 0x3: return !FlagC;
			case 0x4: return FlagN;
			case 0x5: return !FlagN;
			case 0x6: return FlagV;
			case 0x7: return !FlagV;
			case 0x8: return FlagC && !FlagZ;
			case 0x9: return !FlagC || FlagZ;
			case 0xA: return FlagN == FlagV;
			case 0xB: return FlagN != FlagV;
			case 0xC: return !FlagZ && FlagN == FlagV;
			case 0xD: return FlagZ || FlagN != FlagV;
			case 0xE: return true;
			default: return false;
		}
	}

	private static uint Ror( uint val, int amount )
	{
		amount &= 31;
		return (val >> amount) | (val << (32 - amount));
	}

	private uint ReadWordRotated( uint address )
	{
		uint val = Bus.Read32( address );
		int rot = (int)(address & 3) * 8;
		if ( rot != 0 ) val = Ror( val, rot );
		return val;
	}

	private uint ApplyShift( uint val, uint shiftType, uint amount )
	{
		switch ( shiftType )
		{
			case 0: return amount == 0 ? val : val << (int)amount;
			case 1: return amount == 0 ? 0 : val >> (int)amount;
			case 2: return amount == 0 ? (uint)((int)val >> 31) : (uint)((int)val >> (int)amount);
			case 3: return amount == 0 ? (FlagC ? 0x80000000u : 0) | (val >> 1) : Ror( val, (int)amount );
			default: return val;
		}
	}

	private static int BitCount( uint val ) => System.Numerics.BitOperations.PopCount( val );

	private static int MultiplyExtraCycles( uint rs )
	{
		if ( (rs & 0xFFFFFF00) == 0 || (rs & 0xFFFFFF00) == 0xFFFFFF00 ) return 1;
		if ( (rs & 0xFFFF0000) == 0 || (rs & 0xFFFF0000) == 0xFFFF0000 ) return 2;
		if ( (rs & 0xFF000000) == 0 || (rs & 0xFF000000) == 0xFF000000 ) return 3;
		return 4;
	}

	private static int MultiplyExtraCyclesUnsigned( uint rs )
	{
		if ( (rs & 0xFFFFFF00) == 0 ) return 1;
		if ( (rs & 0xFFFF0000) == 0 ) return 2;
		if ( (rs & 0xFF000000) == 0 ) return 3;
		return 4;
	}
}

public enum CpuMode : uint
{
	User = 0x10,
	FIQ = 0x11,
	IRQ = 0x12,
	Supervisor = 0x13,
	Abort = 0x17,
	Undefined = 0x1B,
	System = 0x1F,
}
