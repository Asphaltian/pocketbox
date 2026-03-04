namespace sGBA;

public partial class HleBios
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
			uint fillVal = fill ? Gba.Bus.Read32( src ) : 0;
			for ( int i = 0; i < count; i++ )
			{
				uint val = fill ? fillVal : Gba.Bus.Read32( src );
				Gba.Bus.Write32( dst, val );
				if ( !fill ) src += 4;
				dst += 4;
			}
		}
		else
		{
			if ( fill )
			{
				ushort fillVal = Gba.Bus.Read16( src & ~1u );
				for ( int i = 0; i < count; i++ )
				{
					Gba.Bus.Write16( dst, fillVal );
					dst += 2;
				}
			}
			else
			{
				for ( int i = 0; i < count; i++ )
				{
					ushort val;
					if ( (src & 1) != 0 )
						val = Gba.Bus.Read8( src );
					else
						val = Gba.Bus.Read16( src );
					Gba.Bus.Write16( dst, val );
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

		uint fillVal = fill ? Gba.Bus.Read32( src ) : 0;
		for ( int i = 0; i < count; i++ )
		{
			uint val = fill ? fillVal : Gba.Bus.Read32( src );
			Gba.Bus.Write32( dst, val );
			if ( !fill ) src += 4;
			dst += 4;
		}

		int srcRegion = (int)(Gba.Cpu.Registers[0] >> 24) & 0xF;
		int dstRegion = (int)(Gba.Cpu.Registers[1] >> 24) & 0xF;
		int iterations = count / 8;
		if ( iterations > 0 )
		{
			int ldmCost = 1 + 1 + Gba.Bus.WaitstatesNonseq32[srcRegion]
				+ 7 * (1 + Gba.Bus.WaitstatesSeq32[srcRegion]) + 1;
			int stmCost = 1 + 1 + Gba.Bus.WaitstatesNonseq32[dstRegion]
				+ 7 * (1 + Gba.Bus.WaitstatesSeq32[dstRegion]);

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
			float ox = (int)Gba.Bus.Read32( src ) / 256.0f;
			float oy = (int)Gba.Bus.Read32( src + 4 ) / 256.0f;
			short cx = (short)Gba.Bus.Read16( src + 8 );
			short cy = (short)Gba.Bus.Read16( src + 10 );
			float sx = (short)Gba.Bus.Read16( src + 12 ) / 256.0f;
			float sy = (short)Gba.Bus.Read16( src + 14 ) / 256.0f;
			double theta = (Gba.Bus.Read16( src + 16 ) >> 8) / 128.0 * Math.PI;

			float cosA = (float)Math.Cos( theta );
			float sinA = (float)Math.Sin( theta );

			float a = cosA * sx;
			float b = -sinA * sx;
			float c = sinA * sy;
			float d = cosA * sy;

			float rx = ox - (a * cx + b * cy);
			float ry = oy - (c * cx + d * cy);

			Gba.Bus.Write16( dst + 0, (ushort)(short)(a * 256) );
			Gba.Bus.Write16( dst + 2, (ushort)(short)(b * 256) );
			Gba.Bus.Write16( dst + 4, (ushort)(short)(c * 256) );
			Gba.Bus.Write16( dst + 6, (ushort)(short)(d * 256) );
			Gba.Bus.Write32( dst + 8, (uint)(int)(rx * 256) );
			Gba.Bus.Write32( dst + 12, (uint)(int)(ry * 256) );

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
			short sx = (short)Gba.Bus.Read16( src );
			short sy = (short)Gba.Bus.Read16( src + 2 );
			ushort angle = Gba.Bus.Read16( src + 4 );

			double theta = (angle >> 8) / 128.0 * Math.PI;
			double cosA = Math.Cos( theta );
			double sinA = Math.Sin( theta );

			short pa = (short)(cosA * sx);
			short pb = (short)(-sinA * sx);
			short pc = (short)(sinA * sy);
			short pd = (short)(cosA * sy);

			Gba.Bus.Write16( dst + (uint)(dstStride * 0), (ushort)pa );
			Gba.Bus.Write16( dst + (uint)(dstStride * 1), (ushort)pb );
			Gba.Bus.Write16( dst + (uint)(dstStride * 2), (ushort)pc );
			Gba.Bus.Write16( dst + (uint)(dstStride * 3), (ushort)pd );

			src += 8;
			dst += (uint)(dstStride * 4);
		}
	}

	private void BitUnPack()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];
		uint info = Gba.Cpu.Registers[2];

		ushort srcLen = Gba.Bus.Read16( info );
		byte srcBpp = Gba.Bus.Read8( info + 2 );
		byte dstBpp = Gba.Bus.Read8( info + 3 );
		uint dataOffset = Gba.Bus.Read32( info + 4 );
		bool zeroFlag = (dataOffset & 0x80000000) != 0;
		dataOffset &= 0x7FFFFFFF;

		int srcMask = (1 << srcBpp) - 1;
		int buffer = 0;
		int bitsInBuffer = 0;

		for ( int i = 0; i < srcLen; i++ )
		{
			byte srcByte = Gba.Bus.Read8( src++ );
			for ( int bit = 0; bit < 8; bit += srcBpp )
			{
				int val = (srcByte >> bit) & srcMask;
				if ( val != 0 || zeroFlag )
					val += (int)dataOffset;

				buffer |= val << bitsInBuffer;
				bitsInBuffer += dstBpp;

				if ( bitsInBuffer >= 32 )
				{
					Gba.Bus.Write32( dst, (uint)buffer );
					dst += 4;
					buffer = 0;
					bitsInBuffer = 0;
				}
			}
		}

		if ( bitsInBuffer > 0 )
		{
			Gba.Bus.Write32( dst, (uint)buffer );
		}
	}
}
