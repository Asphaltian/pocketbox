namespace sGBA;

public partial class GbaBios
{
	private void CpuSet()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];
		uint control = Gba.Cpu.Registers[2];

		if ( (src >> 24) < 2 ) return;

		int count = (int)(control & 0x1FFFFF);
		bool is32 = (control & (1 << 26)) != 0;
		bool fill = (control & (1 << 24)) != 0;

		if ( is32 )
		{
			uint fillVal = fill ? Gba.Memory.Load32( src ) : 0;
			for ( int i = 0; i < count; i++ )
			{
				uint val = fill ? fillVal : Gba.Memory.Load32( src );
				Gba.Memory.Store32( dst, val );
				if ( !fill ) src += 4;
				dst += 4;
			}
		}
		else
		{
			if ( fill )
			{
				ushort fillVal = Gba.Memory.Load16( src & ~1u );
				for ( int i = 0; i < count; i++ )
				{
					Gba.Memory.Store16( dst, fillVal );
					dst += 2;
				}
			}
			else
			{
				for ( int i = 0; i < count; i++ )
				{
					ushort val;
					if ( (src & 1) != 0 )
						val = Gba.Memory.Load8( src );
					else
						val = Gba.Memory.Load16( src );
					Gba.Memory.Store16( dst, val );
					src += 2;
					dst += 2;
				}
			}
		}
	}

	private void CpuFastSet()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];
		uint control = Gba.Cpu.Registers[2];

		if ( (src >> 24) < 2 ) return;

		int count = (int)(control & 0x1FFFFF);
		count = (count + 7) & ~7;
		bool fill = (control & (1 << 24)) != 0;

		uint fillVal = fill ? Gba.Memory.Load32( src ) : 0;
		for ( int i = 0; i < count; i++ )
		{
			uint val = fill ? fillVal : Gba.Memory.Load32( src );
			Gba.Memory.Store32( dst, val );
			if ( !fill ) src += 4;
			dst += 4;
		}

		int srcRegion = (int)(Gba.Cpu.Registers[0] >> 24) & 0xF;
		int dstRegion = (int)(Gba.Cpu.Registers[1] >> 24) & 0xF;
		int iterations = count / 8;
		if ( iterations > 0 )
		{
			int ldmCost = 1 + 1 + Gba.Memory.WaitstatesNonseq32[srcRegion]
				+ 7 * (1 + Gba.Memory.WaitstatesSeq32[srcRegion]) + 1;
			int stmCost = 1 + 1 + Gba.Memory.WaitstatesNonseq32[dstRegion]
				+ 7 * (1 + Gba.Memory.WaitstatesSeq32[dstRegion]);

			int perIterTaken = (fill ? 0 : ldmCost) + stmCost + 4;
			int lastIter = (fill ? 0 : ldmCost) + stmCost + 2;

			BiosStall = 50 + (iterations - 1) * perIterTaken + lastIter;
		}
	}

	private void BgAffineSet()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];
		int count = (int)Gba.Cpu.Registers[2];

		for ( int i = 0; i < count; i++ )
		{
			float ox = (int)Gba.Memory.Load32( src ) / 256.0f;
			float oy = (int)Gba.Memory.Load32( src + 4 ) / 256.0f;
			short cx = (short)Gba.Memory.Load16( src + 8 );
			short cy = (short)Gba.Memory.Load16( src + 10 );
			float sx = (short)Gba.Memory.Load16( src + 12 ) / 256.0f;
			float sy = (short)Gba.Memory.Load16( src + 14 ) / 256.0f;
			double theta = (Gba.Memory.Load16( src + 16 ) >> 8) / 128.0 * Math.PI;

			float cosA = (float)Math.Cos( theta );
			float sinA = (float)Math.Sin( theta );

			float a = cosA * sx;
			float b = -sinA * sx;
			float c = sinA * sy;
			float d = cosA * sy;

			float rx = ox - (a * cx + b * cy);
			float ry = oy - (c * cx + d * cy);

			Gba.Memory.Store16( dst + 0, (ushort)(short)(a * 256) );
			Gba.Memory.Store16( dst + 2, (ushort)(short)(b * 256) );
			Gba.Memory.Store16( dst + 4, (ushort)(short)(c * 256) );
			Gba.Memory.Store16( dst + 6, (ushort)(short)(d * 256) );
			Gba.Memory.Store32( dst + 8, (uint)(int)(rx * 256) );
			Gba.Memory.Store32( dst + 12, (uint)(int)(ry * 256) );

			src += 20;
			dst += 16;
		}
	}

	private void ObjAffineSet()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];
		int count = (int)Gba.Cpu.Registers[2];
		int dstStride = (int)Gba.Cpu.Registers[3];

		for ( int i = 0; i < count; i++ )
		{
			short sx = (short)Gba.Memory.Load16( src );
			short sy = (short)Gba.Memory.Load16( src + 2 );
			ushort angle = Gba.Memory.Load16( src + 4 );

			double theta = (angle >> 8) / 128.0 * Math.PI;
			double cosA = Math.Cos( theta );
			double sinA = Math.Sin( theta );

			short pa = (short)(cosA * sx);
			short pb = (short)(-sinA * sx);
			short pc = (short)(sinA * sy);
			short pd = (short)(cosA * sy);

			Gba.Memory.Store16( dst + (uint)(dstStride * 0), (ushort)pa );
			Gba.Memory.Store16( dst + (uint)(dstStride * 1), (ushort)pb );
			Gba.Memory.Store16( dst + (uint)(dstStride * 2), (ushort)pc );
			Gba.Memory.Store16( dst + (uint)(dstStride * 3), (ushort)pd );

			src += 8;
			dst += (uint)(dstStride * 4);
		}
	}

	private void BitUnPack()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];
		uint info = Gba.Cpu.Registers[2];

		ushort srcLen = Gba.Memory.Load16( info );
		byte srcBpp = Gba.Memory.Load8( info + 2 );
		byte dstBpp = Gba.Memory.Load8( info + 3 );

		switch ( srcBpp )
		{
			case 1: case 2: case 4: case 8: break;
			default: return;
		}
		switch ( dstBpp )
		{
			case 1: case 2: case 4: case 8: case 16: case 32: break;
			default: return;
		}

		uint dataOffset = Gba.Memory.Load32( info + 4 );
		bool zeroFlag = (dataOffset & 0x80000000) != 0;
		dataOffset &= 0x7FFFFFFF;

		int srcMask = (1 << srcBpp) - 1;
		uint buffer = 0;
		int bitsInBuffer = 0;
		int bitsRemaining = 0;
		byte inByte = 0;

		while ( srcLen > 0 || bitsRemaining > 0 )
		{
			if ( bitsRemaining == 0 )
			{
				inByte = Gba.Memory.Load8( src++ );
				bitsRemaining = 8;
				srcLen--;
			}

			int val = inByte & srcMask;
			inByte >>= srcBpp;
			if ( val != 0 || zeroFlag )
				val += (int)dataOffset;
			bitsRemaining -= srcBpp;

			buffer |= (uint)val << bitsInBuffer;
			bitsInBuffer += dstBpp;

			if ( bitsInBuffer == 32 )
			{
				Gba.Memory.Store32( dst, buffer );
				dst += 4;
				buffer = 0;
				bitsInBuffer = 0;
			}
		}

		Gba.Cpu.Registers[0] = src;
		Gba.Cpu.Registers[1] = dst;
	}
}
