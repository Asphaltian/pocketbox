namespace sGBA;

public partial class Arm7Cpu
{
	private void ExecuteArm()
	{
		uint opcode = _pipeline0;
		_pipeline0 = _pipeline1;
		_pipeline1 = Bus.Read32( R[15] );
		OpenBusPrefetch = _pipeline1;
		Cycles += 1 + Bus.WaitstatesSeq32[(R[15] >> 24) & 0xF];

		uint cond = opcode >> 28;
		if ( cond != 0xE && !CheckCondition( cond ) )
		{
			R[15] += 4;
			return;
		}

		uint decodeBits = ((opcode >> 16) & 0xFF0) | ((opcode >> 4) & 0xF);

		uint group = (opcode >> 25) & 7;

		switch ( group )
		{
			case 0:
				if ( (opcode & 0x0FC000F0) == 0x00000090 )
					ArmMultiply( opcode );
				else if ( (opcode & 0x0F8000F0) == 0x00800090 )
					ArmMultiplyLong( opcode );
				else if ( (opcode & 0x0FB00FF0) == 0x01000090 )
					ArmSwap( opcode );
				else if ( (opcode & 0x0E000090) == 0x00000090 && (opcode & 0x00000060) != 0 )
					ArmHalfwordTransfer( opcode );
				else if ( (opcode & 0x0FBF0FFF) == 0x010F0000 )
					ArmMrs( opcode );
				else if ( (opcode & 0x0FFFFFF0) == 0x012FFF10 )
					ArmBx( opcode );
				else if ( (opcode & 0x0DB0F000) == 0x0120F000 )
					ArmMsr( opcode );
				else
					ArmDataProcessing( opcode );
				break;

			case 1:
				if ( (opcode & 0x0FB0F000) == 0x0320F000 )
					ArmMsr( opcode );
				else
					ArmDataProcessing( opcode );
				break;

			case 2:
				ArmSingleTransfer( opcode );
				break;

			case 3:
				if ( (opcode & 0x10) != 0 )
				{
					break;
				}
				ArmSingleTransfer( opcode );
				break;

			case 4:
				ArmBlockTransfer( opcode );
				break;

			case 5:
				ArmBranch( opcode );
				break;

			case 6:
				break;

			case 7:
				if ( (opcode & 0x0F000000) == 0x0F000000 )
					ArmSwi( opcode );
				break;
		}

		if ( !_pipelineFlushed )
			R[15] += 4;
	}

	private void ArmDataProcessing( uint opcode )
	{
		uint op = (opcode >> 21) & 0xF;
		bool setFlags = ((opcode >> 20) & 1) != 0;
		uint rn = (opcode >> 16) & 0xF;
		uint rd = (opcode >> 12) & 0xF;

		uint operand1 = R[rn];
		bool isRegShift = ((opcode >> 25) & 1) == 0 && ((opcode >> 4) & 1) != 0;
		if ( rn == 15 && isRegShift ) operand1 += 4;

		uint operand2;
		bool shiftCarry = FlagC;

		if ( ((opcode >> 25) & 1) != 0 )
		{
			uint imm = opcode & 0xFF;
			uint rotate = ((opcode >> 8) & 0xF) * 2;
			if ( rotate != 0 )
			{
				operand2 = Ror( imm, (int)rotate );
				shiftCarry = (operand2 & 0x80000000) != 0;
			}
			else
			{
				operand2 = imm;
			}
		}
		else
		{
			operand2 = GetShifterOperand( opcode, out shiftCarry );
		}

		uint result;
		bool writeDest = true;

		switch ( op )
		{
			case 0x0:
				result = operand1 & operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				break;
			case 0x1:
				result = operand1 ^ operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				break;
			case 0x2:
				result = operand1 - operand2;
				if ( setFlags ) SetSubFlags( operand1, operand2, result );
				break;
			case 0x3:
				result = operand2 - operand1;
				if ( setFlags ) SetSubFlags( operand2, operand1, result );
				break;
			case 0x4:
				result = operand1 + operand2;
				if ( setFlags ) SetAddFlags( operand1, operand2, result );
				break;
			case 0x5:
				result = operand1 + operand2 + (FlagC ? 1u : 0u);
				if ( setFlags ) SetAdcFlags( operand1, operand2, FlagC );
				break;
			case 0x6:
				result = operand1 - operand2 - (FlagC ? 0u : 1u);
				if ( setFlags ) SetSbcFlags( operand1, operand2, FlagC );
				break;
			case 0x7:
				result = operand2 - operand1 - (FlagC ? 0u : 1u);
				if ( setFlags ) SetSbcFlags( operand2, operand1, FlagC );
				break;
			case 0x8:
				result = operand1 & operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				writeDest = false;
				break;
			case 0x9:
				result = operand1 ^ operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				writeDest = false;
				break;
			case 0xA:
				result = operand1 - operand2;
				if ( setFlags ) SetSubFlags( operand1, operand2, result );
				writeDest = false;
				break;
			case 0xB:
				result = operand1 + operand2;
				if ( setFlags ) SetAddFlags( operand1, operand2, result );
				writeDest = false;
				break;
			case 0xC:
				result = operand1 | operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				break;
			case 0xD:
				result = operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				break;
			case 0xE:
				result = operand1 & ~operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				break;
			case 0xF:
				result = ~operand2;
				if ( setFlags ) SetLogicFlags( result, shiftCarry );
				break;
			default:
				result = 0;
				break;
		}

		if ( writeDest )
		{
			R[rd] = result;
			if ( rd == 15 )
			{
				if ( setFlags )
				{
					uint spsr = GetSpsr();
					SetCpsr( spsr );
					InIrqContext = false;
				}
				_pipelineFlushed = true;
			}
		}
	}

	private uint GetShifterOperand( uint opcode, out bool carryOut )
	{
		uint rm = opcode & 0xF;
		uint val = R[rm];

		uint shiftType = (opcode >> 5) & 3;
		uint shiftAmount;
		bool regShift = ((opcode >> 4) & 1) != 0;

		if ( rm == 15 && regShift ) val += 4;

		if ( regShift )
		{
			uint rs = (opcode >> 8) & 0xF;
			shiftAmount = R[rs] & 0xFF;
			Cycles += 1;

			if ( shiftAmount == 0 )
			{
				carryOut = FlagC;
				return val;
			}

			switch ( shiftType )
			{
				case 0:
					if ( shiftAmount < 32 ) { carryOut = ((val >> (int)(32 - shiftAmount)) & 1) != 0; return val << (int)shiftAmount; }
					if ( shiftAmount == 32 ) { carryOut = (val & 1) != 0; return 0; }
					carryOut = false; return 0;
				case 1:
					if ( shiftAmount < 32 ) { carryOut = ((val >> (int)(shiftAmount - 1)) & 1) != 0; return val >> (int)shiftAmount; }
					if ( shiftAmount == 32 ) { carryOut = (val & 0x80000000) != 0; return 0; }
					carryOut = false; return 0;
				case 2:
					if ( shiftAmount >= 32 ) { carryOut = (val & 0x80000000) != 0; return carryOut ? 0xFFFFFFFF : 0; }
					carryOut = ((val >> (int)(shiftAmount - 1)) & 1) != 0;
					return (uint)((int)val >> (int)shiftAmount);
				case 3:
					shiftAmount &= 31;
					if ( shiftAmount == 0 ) { carryOut = (val & 0x80000000) != 0; return val; }
					uint ror = Ror( val, (int)shiftAmount );
					carryOut = (ror & 0x80000000) != 0;
					return ror;
			}
		}
		else
		{
			shiftAmount = (opcode >> 7) & 0x1F;

			switch ( shiftType )
			{
				case 0:
					if ( shiftAmount == 0 ) { carryOut = FlagC; return val; }
					carryOut = ((val >> (int)(32 - shiftAmount)) & 1) != 0;
					return val << (int)shiftAmount;
				case 1:
					if ( shiftAmount == 0 ) { carryOut = (val & 0x80000000) != 0; return 0; }
					carryOut = ((val >> (int)(shiftAmount - 1)) & 1) != 0;
					return val >> (int)shiftAmount;
				case 2:
					if ( shiftAmount == 0 ) { carryOut = (val & 0x80000000) != 0; return carryOut ? 0xFFFFFFFF : 0; }
					carryOut = ((val >> (int)(shiftAmount - 1)) & 1) != 0;
					return (uint)((int)val >> (int)shiftAmount);
				case 3:
					if ( shiftAmount == 0 )
					{
						carryOut = (val & 1) != 0;
						return (FlagC ? 0x80000000u : 0) | (val >> 1);
					}
					uint ror = Ror( val, (int)shiftAmount );
					carryOut = (ror & 0x80000000) != 0;
					return ror;
			}
		}

		carryOut = FlagC;
		return val;
	}

	private void ArmMultiply( uint opcode )
	{
		uint rd = (opcode >> 16) & 0xF;
		uint rn = (opcode >> 12) & 0xF;
		uint rs = (opcode >> 8) & 0xF;
		uint rm = opcode & 0xF;
		bool accumulate = ((opcode >> 21) & 1) != 0;
		bool setFlags = ((opcode >> 20) & 1) != 0;

		uint rsVal = R[rs];
		uint result = R[rm] * rsVal;
		if ( accumulate ) result += R[rn];

		R[rd] = result;

		if ( setFlags )
		{
			FlagN = (result & 0x80000000) != 0;
			FlagZ = result == 0;
		}

		int mulWait = MultiplyExtraCycles( rsVal ) + (accumulate ? 1 : 0);
		Cycles += Bus.MemoryStall( R[15], mulWait );
		int mulCr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[mulCr] - Bus.WaitstatesSeq32[mulCr];
	}

	private void ArmMultiplyLong( uint opcode )
	{
		uint rdHi = (opcode >> 16) & 0xF;
		uint rdLo = (opcode >> 12) & 0xF;
		uint rs = (opcode >> 8) & 0xF;
		uint rm = opcode & 0xF;
		bool isSigned = ((opcode >> 22) & 1) != 0;
		bool accumulate = ((opcode >> 21) & 1) != 0;
		bool setFlags = ((opcode >> 20) & 1) != 0;

		long result;
		if ( isSigned )
			result = (long)(int)R[rm] * (int)R[rs];
		else
			result = (long)((ulong)R[rm] * R[rs]);

		if ( accumulate )
			result += (long)(((ulong)R[rdHi] << 32) | R[rdLo]);

		R[rdLo] = (uint)result;
		R[rdHi] = (uint)(result >> 32);

		if ( setFlags )
		{
			FlagN = (R[rdHi] & 0x80000000) != 0;
			FlagZ = result == 0;
		}

		int longWait = accumulate ? 2 : 1;
		if ( isSigned )
			longWait += MultiplyExtraCycles( R[rs] );
		else
			longWait += MultiplyExtraCyclesUnsigned( R[rs] );
		Cycles += Bus.MemoryStall( R[15], longWait );
		int longMulCr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[longMulCr] - Bus.WaitstatesSeq32[longMulCr];
	}

	private void ArmSwap( uint opcode )
	{
		uint rn = (opcode >> 16) & 0xF;
		uint rd = (opcode >> 12) & 0xF;
		uint rm = opcode & 0xF;
		bool byteSwap = ((opcode >> 22) & 1) != 0;

		uint addr = R[rn];
		if ( byteSwap )
		{
			byte tmp = Bus.Read8( addr );
			Bus.Write8( addr, (byte)R[rm] );
			R[rd] = tmp;
		}
		else
		{
			uint tmp = ReadWordRotated( addr );
			Bus.Write32( addr, R[rm] );
			R[rd] = tmp;
		}

		int dr = (int)((addr >> 24) & 0xF);
		int wait = byteSwap ? Bus.WaitstatesNonseq16[dr] : Bus.WaitstatesNonseq32[dr];
		Cycles += wait * 2 + 3;
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[cr] - Bus.WaitstatesSeq32[cr];
	}

	private void ArmHalfwordTransfer( uint opcode )
	{
		uint rn = (opcode >> 16) & 0xF;
		uint rd = (opcode >> 12) & 0xF;
		bool preIndex = ((opcode >> 24) & 1) != 0;
		bool addOffset = ((opcode >> 23) & 1) != 0;
		bool immediate = ((opcode >> 22) & 1) != 0;
		bool writeBack = ((opcode >> 21) & 1) != 0;
		bool isLoad = ((opcode >> 20) & 1) != 0;
		uint sh = (opcode >> 5) & 3;

		uint offset;
		if ( immediate )
			offset = ((opcode >> 4) & 0xF0) | (opcode & 0xF);
		else
			offset = R[opcode & 0xF];

		uint addr = R[rn];

		if ( preIndex )
			addr = addOffset ? addr + offset : addr - offset;

		switch ( sh )
		{
			case 1:
				if ( isLoad )
				{
					R[rd] = Bus.Read16( addr );
					if ( (addr & 1) != 0 ) R[rd] = Ror( R[rd], 8 );
				}
				else
					Bus.Write16( addr, (ushort)R[rd] );
				break;
			case 2:
				if ( isLoad )
					R[rd] = (uint)(sbyte)Bus.Read8( addr );
				break;
			case 3:
				if ( isLoad )
				{
					if ( (addr & 1) != 0 )
						R[rd] = (uint)(sbyte)Bus.Read8( addr );
					else
						R[rd] = (uint)(short)Bus.Read16( addr );
				}
				break;
		}

		if ( !preIndex )
		{
			uint newAddr = addOffset ? R[rn] + offset : R[rn] - offset;
			R[rn] = newAddr;
		}
		else if ( writeBack )
		{
			R[rn] = addr;
		}

		if ( isLoad && rd == 15 ) _pipelineFlushed = true;

		int dataRegion = (int)((addr >> 24) & 0xF);
		if ( isLoad )
			Cycles += Bus.WaitstatesNonseq16[dataRegion] + 2;
		else
			Cycles += Bus.WaitstatesNonseq16[dataRegion] + 1;
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[cr] - Bus.WaitstatesSeq32[cr];
	}

	private void ArmSingleTransfer( uint opcode )
	{
		uint rn = (opcode >> 16) & 0xF;
		uint rd = (opcode >> 12) & 0xF;
		bool immediate = ((opcode >> 25) & 1) == 0;
		bool preIndex = ((opcode >> 24) & 1) != 0;
		bool addOffset = ((opcode >> 23) & 1) != 0;
		bool byteTransfer = ((opcode >> 22) & 1) != 0;
		bool writeBack = ((opcode >> 21) & 1) != 0;
		bool isLoad = ((opcode >> 20) & 1) != 0;

		uint offset;
		if ( immediate )
		{
			offset = opcode & 0xFFF;
		}
		else
		{
			uint rm = opcode & 0xF;
			uint shiftType = (opcode >> 5) & 3;
			uint shiftAmount = (opcode >> 7) & 0x1F;
			offset = ApplyShift( R[rm], shiftType, shiftAmount );
		}

		uint addr = R[rn];
		if ( rn == 15 ) addr = R[15];

		if ( preIndex )
			addr = addOffset ? addr + offset : addr - offset;

		if ( isLoad )
		{
			if ( byteTransfer )
				R[rd] = Bus.Read8( addr );
			else
				R[rd] = ReadWordRotated( addr );

			if ( rd == 15 ) _pipelineFlushed = true;
		}
		else
		{
			uint val = R[rd];
			if ( rd == 15 ) val += 4;

			if ( byteTransfer )
				Bus.Write8( addr, (byte)val );
			else
				Bus.Write32( addr, val );
		}

		if ( !preIndex )
		{
			R[rn] = addOffset ? R[rn] + offset : R[rn] - offset;
		}
		else if ( writeBack )
		{
			R[rn] = addr;
		}

		int dataRegion = (int)((addr >> 24) & 0xF);
		if ( isLoad )
		{
			int wait = byteTransfer ? Bus.WaitstatesNonseq16[dataRegion] : Bus.WaitstatesNonseq32[dataRegion];
			Cycles += wait + 2;
		}
		else
		{
			int wait = byteTransfer ? Bus.WaitstatesNonseq16[dataRegion] : Bus.WaitstatesNonseq32[dataRegion];
			Cycles += wait + 1;
		}
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[cr] - Bus.WaitstatesSeq32[cr];
	}

	private void ArmBlockTransfer( uint opcode )
	{
		uint rn = (opcode >> 16) & 0xF;
		bool preIndex = ((opcode >> 24) & 1) != 0;
		bool addOffset = ((opcode >> 23) & 1) != 0;
		bool psr = ((opcode >> 22) & 1) != 0;
		bool writeBack = ((opcode >> 21) & 1) != 0;
		bool isLoad = ((opcode >> 20) & 1) != 0;
		ushort regList = (ushort)(opcode & 0xFFFF);

		if ( regList == 0 )
		{
			if ( isLoad )
			{
				R[15] = Bus.Read32( R[rn] );
				_pipelineFlushed = true;
			}
			else
			{
				Bus.Write32( R[rn], R[15] + 4 );
			}
			if ( addOffset )
				R[rn] += 0x40;
			else
				R[rn] -= 0x40;

			int dr0 = (int)((R[rn] >> 24) & 0xF);
			Cycles += Bus.WaitstatesNonseq32[dr0] + (isLoad ? 3 : 2);
			int cr0 = (int)((R[15] >> 24) & 0xF);
			Cycles += Bus.WaitstatesNonseq32[cr0] - Bus.WaitstatesSeq32[cr0];
			return;
		}

		int count = BitCount( regList );
		uint baseAddr = R[rn];

		uint startAddr;
		if ( addOffset )
			startAddr = preIndex ? baseAddr + 4 : baseAddr;
		else
			startAddr = preIndex ? baseAddr - (uint)(count * 4) : baseAddr - (uint)(count * 4) + 4;

		uint addr = startAddr;

		bool useUserBank = psr && !(isLoad && (regList & (1 << 15)) != 0);

		for ( int i = 0; i < 16; i++ )
		{
			if ( (regList & (1 << i)) == 0 ) continue;

			if ( isLoad )
			{
				uint value = Bus.Read32( addr );
				if ( useUserBank && i >= 8 && i <= 14 )
					SetUserReg( i, value );
				else
					R[i] = value;
				if ( i == 15 )
				{
					_pipelineFlushed = true;
					if ( psr )
					{
						uint spsr = GetSpsr();
						SetCpsr( spsr );
						InIrqContext = false;
					}
				}
			}
			else
			{
				uint val;
				if ( useUserBank && i >= 8 && i <= 14 )
					val = GetUserReg( i );
				else
					val = R[i];
				if ( i == 15 ) val += 4;
				Bus.Write32( addr, val );
			}
			addr += 4;
		}

		if ( writeBack && !(isLoad && (regList & (1 << (int)rn)) != 0) )
		{
			if ( addOffset )
				R[rn] = baseAddr + (uint)(count * 4);
			else
				R[rn] = baseAddr - (uint)(count * 4);
		}

		{
			uint wAddr = startAddr;
			int prevRegion = -1;
			int dataCycles = 0;
			for ( int w = 0; w < count; w++ )
			{
				int r = (int)((wAddr >> 24) & 0xF);
				if ( w == 0 || r != prevRegion )
					dataCycles += 1 + Bus.WaitstatesNonseq32[r];
				else
					dataCycles += 1 + Bus.WaitstatesSeq32[r];
				prevRegion = r;
				wAddr += 4;
			}
			Cycles += dataCycles + (isLoad ? 1 : 0);
		}
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[cr] - Bus.WaitstatesSeq32[cr];
	}

	private void ArmBranch( uint opcode )
	{
		bool link = ((opcode >> 24) & 1) != 0;
		int offset = (int)(opcode & 0x00FFFFFF);
		if ( (offset & 0x800000) != 0 )
			offset |= unchecked((int)0xFF000000);
		offset <<= 2;

		if ( link )
			R[14] = R[15] - 4;

		R[15] = (uint)(R[15] + offset);
		_pipelineFlushed = true;
	}

	private void ArmBx( uint opcode )
	{
		uint rm = opcode & 0xF;
		uint addr = R[rm];
		ThumbMode = (addr & 1) != 0;
		R[15] = addr & ~1u;
		_pipelineFlushed = true;
	}

	private void ArmSwi( uint opcode )
	{
		uint comment = (opcode >> 16) & 0xFF;
		if ( Gba.HleBios.HandleSwi( comment ) )
		{
			int biosStall = Gba.HleBios.BiosStall;
			if ( biosStall > 0 )
			{
				int region = (int)((R[15] >> 24) & 0xF);
				Cycles += biosStall + 45
					+ Bus.WaitstatesNonseq16[region]
					+ Bus.WaitstatesNonseq32[region]
					+ Bus.WaitstatesSeq32[region];
			}
			return;
		}

		uint savedCpsr = GetCpsrRaw(); SwitchMode( CpuMode.Supervisor );
		SetSpsr( savedCpsr );
		R[14] = R[15] - 4;
		IrqDisable = true;
		ThumbMode = false;
		R[15] = GbaConstants.VectorSwi;
		_pipelineFlushed = true;
	}

	private void ArmMrs( uint opcode )
	{
		uint rd = (opcode >> 12) & 0xF;
		bool useSPSR = ((opcode >> 22) & 1) != 0;
		R[rd] = useSPSR ? GetSpsr() : GetCpsrRaw();
	}

	private void ArmMsr( uint opcode )
	{
		bool useSPSR = ((opcode >> 22) & 1) != 0;
		uint mask = 0;
		if ( (opcode & (1 << 19)) != 0 ) mask |= 0xFF000000;
		if ( (opcode & (1 << 16)) != 0 ) mask |= 0x000000FF;

		uint value;
		if ( ((opcode >> 25) & 1) != 0 )
		{
			uint imm = opcode & 0xFF;
			uint rotate = ((opcode >> 8) & 0xF) * 2;
			value = Ror( imm, (int)rotate );
		}
		else
		{
			value = R[opcode & 0xF];
		}

		if ( Mode == CpuMode.User )
			mask &= 0xFF000000;

		if ( useSPSR )
		{
			uint spsr = GetSpsr();
			spsr = (spsr & ~mask) | (value & mask);
			SetSpsr( spsr );
		}
		else
		{
			uint oldCpsr = GetCpsrRaw();
			bool oldThumb = (oldCpsr & 0x20) != 0;
			uint cpsr = (oldCpsr & ~mask) | (value & mask);
			SetCpsr( cpsr );
			bool newThumb = (cpsr & 0x20) != 0;
			if ( oldThumb != newThumb )
				_pipelineFlushed = true;
		}
	}
}
