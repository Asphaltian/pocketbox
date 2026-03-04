namespace sGBA;

public partial class Arm7Cpu
{
	private void ExecuteThumb()
	{
		uint opcode = _pipeline0 & 0xFFFF;
		_pipeline0 = _pipeline1;
		_pipeline1 = Bus.Read16( R[15] );
		OpenBusPrefetch = _pipeline1 | (_pipeline1 << 16);
		Cycles += 1 + Bus.WaitstatesSeq16[(R[15] >> 24) & 0xF];

		uint top = opcode >> 8;

		switch ( opcode >> 12 )
		{
			case 0:
				ThumbShiftImm( opcode );
				break;
			case 1:
				if ( (opcode & 0x1800) == 0x1800 )
					ThumbAddSub( opcode );
				else
					ThumbShiftImm( opcode );
				break;
			case 2:
				ThumbImmOp( opcode );
				break;
			case 3:
				ThumbImmOp( opcode );
				break;
			case 4:
				if ( (opcode & 0x0800) == 0 )
				{
					if ( (opcode & 0x0400) == 0 )
						ThumbAluOp( opcode );
					else
						ThumbHiRegBx( opcode );
				}
				else
					ThumbPcRelLoad( opcode );
				break;
			case 5:
				if ( (opcode & 0xF200) == 0x5000 )
					ThumbLoadStoreReg( opcode );
				else if ( (opcode & 0xF200) == 0x5200 )
					ThumbLoadStoreSignedHalf( opcode );
				else
					ThumbLoadStoreImmWord( opcode );
				break;
			case 6:
			case 7:
				ThumbLoadStoreImmWord( opcode );
				break;
			case 8:
				ThumbLoadStoreHalf( opcode );
				break;
			case 9:
				ThumbSpRelLoadStore( opcode );
				break;
			case 10:
				ThumbLoadAddress( opcode );
				break;
			case 11:
				if ( (opcode & 0x0600) == 0 )
					ThumbSpOffset( opcode );
				else if ( (opcode & 0x0600) == 0x0400 )
					ThumbPushPop( opcode );
				else if ( (opcode & 0xFF00) == 0xBE00 )
				{ /* BKPT - ignore */ }
				break;
			case 12:
				ThumbBlockTransfer( opcode );
				break;
			case 13:
				if ( (opcode & 0x0F00) == 0x0F00 )
					ThumbSwi( opcode );
				else
					ThumbCondBranch( opcode );
				break;
			case 14:
				ThumbBranch( opcode );
				break;
			case 15:
				ThumbBranchLink( opcode );
				break;
		}

		if ( !_pipelineFlushed )
			R[15] += 2;
	}

	private void ThumbShiftImm( uint opcode )
	{
		uint rd = opcode & 7;
		uint rm = (opcode >> 3) & 7;
		uint amount = (opcode >> 6) & 0x1F;
		uint op = (opcode >> 11) & 3;
		uint val = R[rm];

		switch ( op )
		{
			case 0:
				if ( amount == 0 ) { R[rd] = val; }
				else { FlagC = ((val >> (int)(32 - amount)) & 1) != 0; R[rd] = val << (int)amount; }
				break;
			case 1:
				if ( amount == 0 ) { FlagC = (val & 0x80000000) != 0; R[rd] = 0; }
				else { FlagC = ((val >> (int)(amount - 1)) & 1) != 0; R[rd] = val >> (int)amount; }
				break;
			case 2:
				if ( amount == 0 ) { FlagC = (val & 0x80000000) != 0; R[rd] = FlagC ? 0xFFFFFFFF : 0; }
				else { FlagC = ((val >> (int)(amount - 1)) & 1) != 0; R[rd] = (uint)((int)val >> (int)amount); }
				break;
		}

		FlagN = (R[rd] & 0x80000000) != 0;
		FlagZ = R[rd] == 0;
	}

	private void ThumbAddSub( uint opcode )
	{
		uint rd = opcode & 7;
		uint rn = (opcode >> 3) & 7;
		bool isImm = ((opcode >> 10) & 1) != 0;
		bool isSub = ((opcode >> 9) & 1) != 0;

		uint operand = isImm ? (opcode >> 6) & 7 : R[(opcode >> 6) & 7];
		uint a = R[rn];

		if ( isSub )
		{
			R[rd] = a - operand;
			SetSubFlags( a, operand, R[rd] );
		}
		else
		{
			R[rd] = a + operand;
			SetAddFlags( a, operand, R[rd] );
		}
	}

	private void ThumbImmOp( uint opcode )
	{
		uint rd = (opcode >> 8) & 7;
		uint imm = opcode & 0xFF;
		uint op = (opcode >> 11) & 3;

		switch ( op )
		{
			case 0:
				R[rd] = imm;
				FlagN = false;
				FlagZ = imm == 0;
				break;
			case 1:
				uint cmpResult = R[rd] - imm;
				SetSubFlags( R[rd], imm, cmpResult );
				break;
			case 2:
				{
					uint old = R[rd];
					R[rd] += imm;
					SetAddFlags( old, imm, R[rd] );
				}
				break;
			case 3:
				{
					uint old = R[rd];
					R[rd] -= imm;
					SetSubFlags( old, imm, R[rd] );
				}
				break;
		}
	}

	private void ThumbAluOp( uint opcode )
	{
		uint rd = opcode & 7;
		uint rm = (opcode >> 3) & 7;
		uint op = (opcode >> 6) & 0xF;
		uint a = R[rd], b = R[rm];
		uint result;

		switch ( op )
		{
			case 0x0: result = a & b; SetLogicFlags( result, FlagC ); R[rd] = result; break;
			case 0x1: result = a ^ b; SetLogicFlags( result, FlagC ); R[rd] = result; break;
			case 0x2:
				{
					uint shift = b & 0xFF;
					if ( shift == 0 ) { result = a; }
					else if ( shift < 32 ) { FlagC = ((a >> (int)(32 - shift)) & 1) != 0; result = a << (int)shift; }
					else if ( shift == 32 ) { FlagC = (a & 1) != 0; result = 0; }
					else { FlagC = false; result = 0; }
					FlagN = (result & 0x80000000) != 0; FlagZ = result == 0;
					R[rd] = result; Cycles++;
				}
				break;
			case 0x3:
				{
					uint shift = b & 0xFF;
					if ( shift == 0 ) { result = a; }
					else if ( shift < 32 ) { FlagC = ((a >> (int)(shift - 1)) & 1) != 0; result = a >> (int)shift; }
					else if ( shift == 32 ) { FlagC = (a & 0x80000000) != 0; result = 0; }
					else { FlagC = false; result = 0; }
					FlagN = (result & 0x80000000) != 0; FlagZ = result == 0;
					R[rd] = result; Cycles++;
				}
				break;
			case 0x4:
				{
					uint shift = b & 0xFF;
					if ( shift == 0 ) { result = a; }
					else if ( shift < 32 ) { FlagC = ((a >> (int)(shift - 1)) & 1) != 0; result = (uint)((int)a >> (int)shift); }
					else { FlagC = (a & 0x80000000) != 0; result = FlagC ? 0xFFFFFFFF : 0u; }
					FlagN = (result & 0x80000000) != 0; FlagZ = result == 0;
					R[rd] = result; Cycles++;
				}
				break;
			case 0x5: result = a + b + (FlagC ? 1u : 0); SetAdcFlags( a, b, FlagC ); R[rd] = result; break;
			case 0x6: result = a - b - (FlagC ? 0u : 1u); SetSbcFlags( a, b, FlagC ); R[rd] = result; break;
			case 0x7:
				{
					uint shift = b & 0xFF;
					if ( shift == 0 ) { result = a; }
					else { shift &= 31; if ( shift == 0 ) { FlagC = (a & 0x80000000) != 0; result = a; } else { result = Ror( a, (int)shift ); FlagC = (result & 0x80000000) != 0; } }
					FlagN = (result & 0x80000000) != 0; FlagZ = result == 0;
					R[rd] = result; Cycles++;
				}
				break;
			case 0x8: result = a & b; SetLogicFlags( result, FlagC ); break;
			case 0x9: result = 0 - b; SetSubFlags( 0, b, result ); R[rd] = result; break;
			case 0xA: result = a - b; SetSubFlags( a, b, result ); break;
			case 0xB: result = a + b; SetAddFlags( a, b, result ); break;
			case 0xC: result = a | b; SetLogicFlags( result, FlagC ); R[rd] = result; break;
			case 0xD:
				result = a * b;
				FlagN = (result & 0x80000000) != 0;
				FlagZ = result == 0;
				R[rd] = result;
				Cycles += Bus.MemoryStall( R[15], MultiplyExtraCycles( a ) );
				{ int thumbMulCr = (int)((R[15] >> 24) & 0xF); Cycles += Bus.WaitstatesNonseq16[thumbMulCr] - Bus.WaitstatesSeq16[thumbMulCr]; }
				break;
			case 0xE: result = a & ~b; SetLogicFlags( result, FlagC ); R[rd] = result; break;
			case 0xF: result = ~b; SetLogicFlags( result, FlagC ); R[rd] = result; break;
		}
	}

	private void ThumbHiRegBx( uint opcode )
	{
		uint op = (opcode >> 8) & 3;
		uint rd = (opcode & 7) | ((opcode >> 4) & 8);
		uint rm = (opcode >> 3) & 0xF;

		switch ( op )
		{
			case 0:
				R[rd] += R[rm];
				if ( rd == 15 ) _pipelineFlushed = true;
				break;
			case 1:
				{
					uint result = R[rd] - R[rm];
					SetSubFlags( R[rd], R[rm], result );
				}
				break;
			case 2:
				R[rd] = R[rm];
				if ( rd == 15 ) _pipelineFlushed = true;
				break;
			case 3:
				ThumbMode = (R[rm] & 1) != 0;
				R[15] = R[rm] & ~1u;
				_pipelineFlushed = true;
				break;
		}
	}

	private void ThumbPcRelLoad( uint opcode )
	{
		uint rd = (opcode >> 8) & 7;
		uint offset = (opcode & 0xFF) << 2;
		uint addr = (R[15] & ~3u) + offset;
		R[rd] = Bus.Read32( addr );

		int dr = (int)((addr >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[dr] + 2;
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbLoadStoreReg( uint opcode )
	{
		uint rd = opcode & 7;
		uint rn = (opcode >> 3) & 7;
		uint rm = (opcode >> 6) & 7;
		uint addr = R[rn] + R[rm];
		bool isLoad = ((opcode >> 11) & 1) != 0;
		bool isByte = ((opcode >> 10) & 1) != 0;

		if ( isLoad )
		{
			if ( isByte )
				R[rd] = Bus.Read8( addr );
			else
				R[rd] = ReadWordRotated( addr );
		}
		else
		{
			if ( isByte )
				Bus.Write8( addr, (byte)R[rd] );
			else
				Bus.Write32( addr, R[rd] );
		}

		int dr = (int)((addr >> 24) & 0xF);
		if ( isLoad )
		{
			int wait = isByte ? Bus.WaitstatesNonseq16[dr] : Bus.WaitstatesNonseq32[dr];
			Cycles += wait + 2;
		}
		else
		{
			int wait = isByte ? Bus.WaitstatesNonseq16[dr] : Bus.WaitstatesNonseq32[dr];
			Cycles += wait + 1;
		}
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbLoadStoreSignedHalf( uint opcode )
	{
		uint rd = opcode & 7;
		uint rn = (opcode >> 3) & 7;
		uint rm = (opcode >> 6) & 7;
		uint addr = R[rn] + R[rm];
		uint op = (opcode >> 10) & 3;

		switch ( op )
		{
			case 0: Bus.Write16( addr, (ushort)R[rd] ); break;
			case 1: R[rd] = (uint)(sbyte)Bus.Read8( addr ); break;
			case 2: R[rd] = Bus.Read16( addr ); break;
			case 3:
				if ( (addr & 1) != 0 )
					R[rd] = (uint)(sbyte)Bus.Read8( addr );
				else
					R[rd] = (uint)(short)Bus.Read16( addr );
				break;
		}

		int dr = (int)((addr >> 24) & 0xF); bool isStore = op == 0;
		Cycles += Bus.WaitstatesNonseq16[dr] + (isStore ? 1 : 2);
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbLoadStoreImmWord( uint opcode )
	{
		uint top3 = (opcode >> 13) & 7;
		uint rd = opcode & 7;
		uint rn = (opcode >> 3) & 7;
		uint offset5 = (opcode >> 6) & 0x1F;
		bool isLoad = ((opcode >> 11) & 1) != 0;
		uint addr = 0;
		bool isByte = false;

		if ( top3 == 3 )
		{
			isByte = ((opcode >> 12) & 1) != 0;
			if ( isByte )
			{
				addr = R[rn] + offset5;
				if ( isLoad ) R[rd] = Bus.Read8( addr );
				else Bus.Write8( addr, (byte)R[rd] );
			}
			else
			{
				addr = R[rn] + offset5 * 4;
				if ( isLoad ) R[rd] = ReadWordRotated( addr );
				else Bus.Write32( addr, R[rd] );
			}
		}
		else if ( top3 == 2 )
		{
			uint rm = (opcode >> 6) & 7;
			addr = R[rn] + R[rm];
			isByte = ((opcode >> 10) & 1) != 0;
			if ( isByte )
			{
				if ( isLoad ) R[rd] = Bus.Read8( addr );
				else Bus.Write8( addr, (byte)R[rd] );
			}
			else
			{
				if ( isLoad ) R[rd] = ReadWordRotated( addr );
				else Bus.Write32( addr, R[rd] );
			}
		}

		int dr = (int)((addr >> 24) & 0xF);
		int wait = isByte ? Bus.WaitstatesNonseq16[dr] : Bus.WaitstatesNonseq32[dr];
		Cycles += wait + (isLoad ? 2 : 1);
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbLoadStoreHalf( uint opcode )
	{
		uint rd = opcode & 7;
		uint rn = (opcode >> 3) & 7;
		uint offset = ((opcode >> 6) & 0x1F) << 1;
		uint addr = R[rn] + offset;
		bool isLoad = ((opcode >> 11) & 1) != 0;

		if ( isLoad )
			R[rd] = Bus.Read16( addr );
		else
			Bus.Write16( addr, (ushort)R[rd] );

		int dr = (int)((addr >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[dr] + (isLoad ? 2 : 1);
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbSpRelLoadStore( uint opcode )
	{
		uint rd = (opcode >> 8) & 7;
		uint offset = (opcode & 0xFF) << 2;
		uint addr = R[13] + offset;
		bool isLoad = ((opcode >> 11) & 1) != 0;

		if ( isLoad )
			R[rd] = ReadWordRotated( addr );
		else
			Bus.Write32( addr, R[rd] );

		int dr = (int)((addr >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq32[dr] + (isLoad ? 2 : 1);
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbLoadAddress( uint opcode )
	{
		uint rd = (opcode >> 8) & 7;
		uint offset = (opcode & 0xFF) << 2;
		bool useSP = ((opcode >> 11) & 1) != 0;

		if ( useSP )
			R[rd] = R[13] + offset;
		else
			R[rd] = (R[15] & ~3u) + offset;
	}

	private void ThumbSpOffset( uint opcode )
	{
		uint offset = (opcode & 0x7F) << 2;
		if ( (opcode & 0x80) != 0 )
			R[13] -= offset;
		else
			R[13] += offset;
	}

	private void ThumbPushPop( uint opcode )
	{
		bool isLoad = ((opcode >> 11) & 1) != 0;
		bool extraReg = ((opcode >> 8) & 1) != 0;
		byte regList = (byte)(opcode & 0xFF);
		int count = BitCount( regList ) + (extraReg ? 1 : 0);

		if ( isLoad )
		{
			uint addr = R[13];
			for ( int i = 0; i < 8; i++ )
			{
				if ( (regList & (1 << i)) != 0 )
				{
					R[i] = Bus.Read32( addr );
					addr += 4;
				}
			}
			if ( extraReg )
			{
				R[15] = Bus.Read32( addr );
				addr += 4;
				_pipelineFlushed = true;
			}
			R[13] = addr;
		}
		else
		{
			uint addr = R[13] - (uint)(count * 4);
			R[13] = addr;
			for ( int i = 0; i < 8; i++ )
			{
				if ( (regList & (1 << i)) != 0 )
				{
					Bus.Write32( addr, R[i] );
					addr += 4;
				}
			}
			if ( extraReg )
			{
				Bus.Write32( addr, R[14] );
			}
		}

		int dr = (int)((R[13] >> 24) & 0xF);
		if ( count > 0 )
		{
			int firstWait = Bus.WaitstatesNonseq32[dr];
			int seqWait = Bus.WaitstatesSeq32[dr];
			Cycles += firstWait + 1 + (count - 1) * (seqWait + 1) + (isLoad ? 1 : 0);
		}
		int cr = (int)((R[15] >> 24) & 0xF);
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbBlockTransfer( uint opcode )
	{
		uint rn = (opcode >> 8) & 7;
		bool isLoad = ((opcode >> 11) & 1) != 0;
		byte regList = (byte)(opcode & 0xFF);
		uint addr = R[rn];

		if ( regList == 0 )
		{
			if ( isLoad )
			{
				R[15] = Bus.Read32( addr );
				_pipelineFlushed = true;
			}
			else
			{
				Bus.Write32( addr, R[15] + 2 );
			}
			R[rn] += 0x40;

			int dr0 = (int)((addr >> 24) & 0xF); Cycles += Bus.WaitstatesNonseq32[dr0] + (isLoad ? 3 : 2);
			int cr0 = (int)((R[15] >> 24) & 0xF);
			Cycles += Bus.WaitstatesNonseq16[cr0] - Bus.WaitstatesSeq16[cr0];
			return;
		}

		int count = BitCount( regList );
		uint startAddr = addr;

		for ( int i = 0; i < 8; i++ )
		{
			if ( (regList & (1 << i)) == 0 ) continue;

			if ( isLoad )
				R[i] = Bus.Read32( addr );
			else
				Bus.Write32( addr, R[i] );
			addr += 4;
		}

		if ( !isLoad || (regList & (1 << (int)rn)) == 0 )
			R[rn] = addr;

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
		Cycles += Bus.WaitstatesNonseq16[cr] - Bus.WaitstatesSeq16[cr];
	}

	private void ThumbCondBranch( uint opcode )
	{
		uint cond = (opcode >> 8) & 0xF;
		if ( !CheckCondition( cond ) ) return;

		int offset = (sbyte)(opcode & 0xFF);
		offset <<= 1;
		R[15] = (uint)(R[15] + offset);
		_pipelineFlushed = true;
	}

	private void ThumbBranch( uint opcode )
	{
		int offset = (int)(opcode & 0x7FF);
		if ( (offset & 0x400) != 0 ) offset |= unchecked((int)0xFFFFF800);
		offset <<= 1;
		R[15] = (uint)(R[15] + offset);
		_pipelineFlushed = true;
	}

	private void ThumbBranchLink( uint opcode )
	{
		bool isSecond = ((opcode >> 11) & 1) != 0;

		if ( !isSecond )
		{
			int offset = (int)(opcode & 0x7FF);
			if ( (offset & 0x400) != 0 ) offset |= unchecked((int)0xFFFFF800);
			R[14] = (uint)(R[15] + (offset << 12));
		}
		else
		{
			uint temp = R[14] + ((opcode & 0x7FF) << 1);
			R[14] = (R[15] - 2) | 1;
			R[15] = temp;
			_pipelineFlushed = true;
		}
	}

	private void ThumbSwi( uint opcode )
	{
		uint comment = opcode & 0xFF;
		if ( Gba.HleBios.HandleSwi( comment ) )
		{
			int biosStall = Gba.HleBios.BiosStall;
			if ( biosStall > 0 )
			{
				int region = (int)((R[15] >> 24) & 0xF);
				Cycles += biosStall + 45
					+ Bus.WaitstatesNonseq16[region]
					+ Bus.WaitstatesNonseq16[region]
					+ Bus.WaitstatesSeq16[region];
			}
			return;
		}

		uint savedCpsr = GetCpsrRaw();
		SwitchMode( CpuMode.Supervisor );
		SetSpsr( savedCpsr );
		R[14] = R[15] - 2;
		IrqDisable = true;
		ThumbMode = false;
		R[15] = GbaConstants.VectorSwi;
		_pipelineFlushed = true;
	}
}
