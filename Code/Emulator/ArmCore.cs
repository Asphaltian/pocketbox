using System.IO;

namespace sGBA;

public partial class ArmCore
{
	public uint[] Gprs = new uint[16];
	public uint[] Registers => Gprs;

	public bool FlagN, FlagZ, FlagC, FlagV;
	public bool IrqDisable = true;
	public bool FiqDisable = true;
	public bool ThumbMode;
	public PrivilegeMode PrivilegeMode = PrivilegeMode.System;

	private readonly uint[][] _bankedRegisters = new uint[6][];
	private readonly uint[] _bankedSPSRs = new uint[6];
	private readonly uint[] _fiqRegsHi = new uint[5];
	private readonly uint[] _usrRegsHi = new uint[5];

	public long Cycles;
	public long InstructionStartCycles;
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

	public uint[] BankedSPSRs => _bankedSPSRs;

	private uint _prefetch0;
	private uint _prefetch1;
	private bool _prefetchFlushed = true;

	public Gba Gba { get; }
	private readonly GbaMemory Memory;

	public ArmCore( Gba gba )
	{
		Gba = gba;
		Memory = gba.Memory;
		for ( int i = 0; i < 6; i++ )
			_bankedRegisters[i] = new uint[2];
	}

	public void Reset()
	{
		Array.Clear( Gprs );
		FlagN = FlagZ = FlagC = FlagV = false;
		IrqDisable = true;
		FiqDisable = true;
		ThumbMode = false;
		PrivilegeMode = PrivilegeMode.System;
		Halted = false;
		IrqPending = false;
		InIntrWait = false;
		IntrWaitFlags = 0;
		_prefetchFlushed = true;
		Cycles = 0;

		for ( int i = 0; i < 6; i++ )
		{
			_bankedRegisters[i][0] = 0;
			_bankedRegisters[i][1] = 0;
			_bankedSPSRs[i] = 0;
		}
	}

	public void SkipBios()
	{
		SetPrivilegeMode( PrivilegeMode.IRQ );
		Gprs[13] = GbaConstants.SpBaseIrq;
		SetPrivilegeMode( PrivilegeMode.Supervisor );
		Gprs[13] = GbaConstants.SpBaseSupervisor;
		SetPrivilegeMode( PrivilegeMode.System );
		Gprs[13] = GbaConstants.SpBaseSystem;

		IrqDisable = false;
		FiqDisable = false;
		Gprs[15] = 0x08000000;
		_prefetchFlushed = true;
	}

	public void Run( long targetCycles )
	{
		if ( CrashDetected )
		{
			Cycles = targetCycles;
			return;
		}

		if ( Halted )
			return;

		var timers = Gba.Timers;
		var apu = Gba.Audio;
		var io = Gba.Io;
		var dma = Gba.Dma;

		while ( Cycles < targetCycles )
		{
			long cyclesBefore = Cycles;

			if ( _prefetchFlushed )
			{
				FlushPipeline();
				_prefetchFlushed = false;
			}

			if ( IrqPending && !IrqDisable )
			{
				RaiseIrq();
				IrqPending = false;
				FlushPipeline();
				_prefetchFlushed = false;
			}

			if ( InIntrWait && !Halted && !IrqDisable && !InIrqContext )
			{
				Halted = true;
				return;
			}

			uint instrAddr = ThumbMode ? Gprs[15] - 4 : Gprs[15] - 8;

			if ( !IsExecutableAddress( instrAddr ) )
			{
				if ( !CrashDetected )
				{
					CrashDetected = true;
					CrashPc = instrAddr;
					CrashCpsr = GetCpsrRaw();
					CrashRegs = new uint[16];
					Array.Copy( Gprs, CrashRegs, 16 );
					CrashThumb = ThumbMode;
				}
				Cycles = targetCycles;
				return;
			}

			InstructionStartCycles = Cycles;

			if ( ThumbMode )
				ExecuteThumb();
			else
				ExecuteArm();

			int delta = (int)(Cycles - cyclesBefore);
			timers.Tick( delta );
			apu.Tick( delta );
			io.FinishSioTransfer();
			io.TickIrqDelay( delta );

			if ( Halted || CrashDetected )
				return;

			if ( dma.ActiveDma >= 0 )
				return;
		}
	}

	private static bool IsExecutableAddress( uint addr )
	{
		int region = (int)(addr >> 24);
		return region == 0x00 || region == 0x02 || region == 0x03 ||
			   (region >= 0x08 && region <= 0x0D);
	}

	public void FlushPipeline()
	{
		Memory.LastPrefetchedPc = 0;

		if ( ThumbMode )
		{
			Gprs[15] &= ~1u;
			_prefetch0 = Memory.Load16( Gprs[15] );
			Gprs[15] += 2;
			_prefetch1 = Memory.Load16( Gprs[15] );
			Gprs[15] += 2;
		}
		else
		{
			Gprs[15] &= ~3u;
			_prefetch0 = Memory.Load32( Gprs[15] );
			Gprs[15] += 4;
			_prefetch1 = Memory.Load32( Gprs[15] );
			Gprs[15] += 4;
		}

		int region = (int)((Gprs[15] >> 24) & 0xF);
		if ( ThumbMode )
			Cycles += 2 + Memory.WaitstatesNonseq16[region] + Memory.WaitstatesSeq16[region];
		else
			Cycles += 2 + Memory.WaitstatesNonseq32[region] + Memory.WaitstatesSeq32[region];
	}

	public void SerializePipeline( BinaryWriter w )
	{
		w.Write( _prefetchFlushed );
		w.Write( _prefetch0 );
		w.Write( _prefetch1 );
	}

	public void DeserializePipeline( BinaryReader r )
	{
		_prefetchFlushed = r.ReadBoolean();
		_prefetch0 = r.ReadUInt32();
		_prefetch1 = r.ReadUInt32();
	}

	public void RaiseIrq()
	{
		uint savedCpsr = GetCpsrRaw();
		SetPrivilegeMode( PrivilegeMode.IRQ );
		SetSpsr( savedCpsr );
		Gprs[14] = Gprs[15] - (ThumbMode ? 0u : 4u);
		IrqDisable = true;
		ThumbMode = false;
		Gprs[15] = GbaConstants.BaseIrq;
		_prefetchFlushed = true;
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
		cpsr |= (uint)PrivilegeMode;
		return cpsr;
	}

	public void SetCpsr( uint cpsr )
	{
		FlagN = (cpsr & 0x80000000) != 0;
		FlagZ = (cpsr & 0x40000000) != 0;
		FlagC = (cpsr & 0x20000000) != 0;
		FlagV = (cpsr & 0x10000000) != 0;
		bool wasIrqDisabled = IrqDisable;
		IrqDisable = (cpsr & 0x80) != 0;
		FiqDisable = (cpsr & 0x40) != 0;
		ThumbMode = (cpsr & 0x20) != 0;

		PrivilegeMode newMode = (PrivilegeMode)(cpsr & 0x1F);
		if ( newMode != PrivilegeMode && IsValidMode( newMode ) )
			SetPrivilegeMode( newMode );

		if ( wasIrqDisabled && !IrqDisable )
			Gba.Io.TestIrq();
	}

	private static bool IsValidMode( PrivilegeMode mode )
	{
		return mode == PrivilegeMode.User || mode == PrivilegeMode.FIQ || mode == PrivilegeMode.IRQ ||
			   mode == PrivilegeMode.Supervisor || mode == PrivilegeMode.Abort ||
			   mode == PrivilegeMode.Undefined || mode == PrivilegeMode.System;
	}

	public void SetPrivilegeMode( PrivilegeMode newMode )
	{
		int oldBank = GetBankIndex( PrivilegeMode );
		int newBank = GetBankIndex( newMode );

		if ( oldBank != newBank )
		{
			_bankedRegisters[oldBank][0] = Gprs[13];
			_bankedRegisters[oldBank][1] = Gprs[14];

			if ( PrivilegeMode == PrivilegeMode.FIQ )
			{
				for ( int i = 0; i < 5; i++ ) { _fiqRegsHi[i] = Gprs[8 + i]; Gprs[8 + i] = _usrRegsHi[i]; }
			}

			Gprs[13] = _bankedRegisters[newBank][0];
			Gprs[14] = _bankedRegisters[newBank][1];

			if ( newMode == PrivilegeMode.FIQ )
			{
				for ( int i = 0; i < 5; i++ ) { _usrRegsHi[i] = Gprs[8 + i]; Gprs[8 + i] = _fiqRegsHi[i]; }
			}
		}

		PrivilegeMode = newMode;
	}

	private int GetBankIndex( PrivilegeMode mode )
	{
		switch ( mode )
		{
			case PrivilegeMode.FIQ: return 0;
			case PrivilegeMode.IRQ: return 1;
			case PrivilegeMode.Supervisor: return 2;
			case PrivilegeMode.Abort: return 3;
			case PrivilegeMode.Undefined: return 4;
			default: return 5;
		}
	}

	public uint GetSpsr()
	{
		if ( PrivilegeMode == PrivilegeMode.User || PrivilegeMode == PrivilegeMode.System )
			return GetCpsrRaw();
		int bank = GetBankIndex( PrivilegeMode );
		return _bankedSPSRs[bank];
	}

	public void SetSpsr( uint value )
	{
		int bank = GetBankIndex( PrivilegeMode );
		_bankedSPSRs[bank] = value;
	}

	private uint GetUserReg( int reg )
	{
		if ( PrivilegeMode == PrivilegeMode.User || PrivilegeMode == PrivilegeMode.System )
			return Gprs[reg];

		if ( reg >= 8 && reg <= 12 && PrivilegeMode == PrivilegeMode.FIQ )
			return _usrRegsHi[reg - 8];

		if ( reg == 13 || reg == 14 )
			return _bankedRegisters[5][reg - 13];

		return Gprs[reg];
	}

	private void SetUserReg( int reg, uint value )
	{
		if ( PrivilegeMode == PrivilegeMode.User || PrivilegeMode == PrivilegeMode.System )
		{
			Gprs[reg] = value;
			return;
		}

		if ( reg >= 8 && reg <= 12 && PrivilegeMode == PrivilegeMode.FIQ )
		{
			_usrRegsHi[reg - 8] = value;
			return;
		}

		if ( reg == 13 || reg == 14 )
		{
			_bankedRegisters[5][reg - 13] = value;
			return;
		}

		Gprs[reg] = value;
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
		uint val = Memory.Load32( address );
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

public enum PrivilegeMode : uint
{
	User = 0x10,
	FIQ = 0x11,
	IRQ = 0x12,
	Supervisor = 0x13,
	Abort = 0x17,
	Undefined = 0x1B,
	System = 0x1F,
}
