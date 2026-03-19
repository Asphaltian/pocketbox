namespace sGBA;

public partial class HleBios
{
	private void LZ77UnCompWram() { LZ77Decompress( false ); }
	private void LZ77UnCompVram() { LZ77Decompress( true ); }

	private void LZ77Decompress( bool vram )
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];

		uint header = Gba.Bus.Read32( src );
		src += 4;
		int decompSize = (int)(header >> 8);

		int written = 0;
		int halfword = 0;

		while ( written < decompSize )
		{
			byte flags = Gba.Bus.Read8( src++ );
			for ( int i = 7; i >= 0 && written < decompSize; i-- )
			{
				if ( (flags & (1 << i)) != 0 )
				{
					byte b1 = Gba.Bus.Read8( src++ );
					byte b2 = Gba.Bus.Read8( src++ );
					int length = ((b1 >> 4) & 0xF) + 3;
					int offset = ((b1 & 0xF) << 8) | b2;
					offset++;

					uint disp = dst - (uint)offset;
					for ( int j = 0; j < length && written < decompSize; j++ )
					{
						byte val = Gba.Bus.Read8( disp );
						if ( vram )
						{
							if ( (dst & 1) == 0 )
							{
								halfword = val;
							}
							else
							{
								halfword |= val << 8;
								Gba.Bus.Write16( dst ^ 1, (ushort)halfword );
							}
						}
						else
						{
							Gba.Bus.Write8( dst, val );
						}
						disp++;
						dst++;
						written++;
					}
				}
				else
				{
					byte val = Gba.Bus.Read8( src++ );
					if ( vram )
					{
						if ( (dst & 1) == 0 )
						{
							halfword = val;
						}
						else
						{
							halfword |= val << 8;
							Gba.Bus.Write16( dst ^ 1, (ushort)halfword );
						}
					}
					else
					{
						Gba.Bus.Write8( dst, val );
					}
					dst++;
					written++;
				}
			}
		}

		Gba.Cpu.Registers[0] = src;
		Gba.Cpu.Registers[1] = dst;
		Gba.Cpu.Registers[3] = 0;
	}

	private void HuffUnComp()
	{
		uint src = Gba.Cpu.Registers[0] & 0xFFFFFFFC;
		uint dst = Gba.Cpu.Registers[1];

		uint header = Gba.Bus.Read32( src );
		int bitSize = (int)(header & 0xF);
		int decompSize = (int)(header >> 8);

		if ( bitSize == 0 )
			bitSize = 8;
		if ( 32 % bitSize != 0 || bitSize == 1 )
			return;

		src += 4;

		uint treeSize = Gba.Bus.Read8( src ) * 2u + 1;
		uint treeBase = src + 1;
		src += treeSize + 1;

		int written = 0;
		uint outBuffer = 0;
		int outBits = 0;
		uint bits = 0;
		int bitsLeft = 0;

		uint treeNode = treeBase;
		while ( written < decompSize )
		{
			if ( bitsLeft == 0 )
			{
				bits = Gba.Bus.Read32( src );
				src += 4;
				bitsLeft = 32;
			}

			bool goRight = (bits & 0x80000000) != 0;
			bits <<= 1;
			bitsLeft--;

			byte nodeVal = Gba.Bus.Read8( treeNode );
			uint childOffset = (treeNode & ~1u) + (uint)(nodeVal & 0x3F) * 2 + 2;

			if ( goRight )
				childOffset++;

			int endFlag = goRight ? ((nodeVal >> 6) & 1) : (nodeVal >> 7);

			if ( endFlag != 0 )
			{
				byte data = Gba.Bus.Read8( childOffset );
				outBuffer |= (uint)(data & ((1 << bitSize) - 1)) << outBits;
				outBits += bitSize;

				if ( outBits >= 32 )
				{
					Gba.Bus.Write32( dst, outBuffer );
					dst += 4;
					written += 4;
					outBuffer = 0;
					outBits = 0;
				}

				treeNode = treeBase;
			}
			else
			{
				treeNode = childOffset;
			}
		}

		Gba.Cpu.Registers[0] = src;
		Gba.Cpu.Registers[1] = dst;
	}

	private void RLUnCompWram() { RLDecompress( false ); }
	private void RLUnCompVram() { RLDecompress( true ); }

	private void RLDecompress( bool vram )
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];

		uint header = Gba.Bus.Read32( src & 0xFFFFFFFC );
		src += 4;
		int remaining = (int)(header >> 8);
		int padding = (4 - remaining) & 0x3;

		int halfword = 0;

		while ( remaining > 0 )
		{
			byte flag = Gba.Bus.Read8( src++ );
			if ( (flag & 0x80) != 0 )
			{
				int length = (flag & 0x7F) + 3;
				byte data = Gba.Bus.Read8( src++ );
				for ( int i = 0; i < length && remaining > 0; i++ )
				{
					remaining--;
					if ( vram )
					{
						if ( (dst & 1) != 0 )
						{
							halfword |= data << 8;
							Gba.Bus.Write16( dst ^ 1, (ushort)halfword );
						}
						else
						{
							halfword = data;
						}
					}
					else
					{
						Gba.Bus.Write8( dst, data );
					}
					dst++;
				}
			}
			else
			{
				int length = (flag & 0x7F) + 1;
				for ( int i = 0; i < length && remaining > 0; i++ )
				{
					byte data = Gba.Bus.Read8( src++ );
					remaining--;
					if ( vram )
					{
						if ( (dst & 1) != 0 )
						{
							halfword |= data << 8;
							Gba.Bus.Write16( dst ^ 1, (ushort)halfword );
						}
						else
						{
							halfword = data;
						}
					}
					else
					{
						Gba.Bus.Write8( dst, data );
					}
					dst++;
				}
			}
		}

		if ( vram )
		{
			if ( (dst & 1) != 0 )
			{
				padding--;
				dst++;
			}
			for ( ; padding > 0; padding -= 2, dst += 2 )
				Gba.Bus.Write16( dst, 0 );
		}
		else
		{
			for ( ; padding > 0; padding-- )
				Gba.Bus.Write8( dst++, 0 );
		}

		Gba.Cpu.Registers[0] = src;
		Gba.Cpu.Registers[1] = dst;
	}

	private void DiffUnFilterWram() { DiffUnFilter( 1, 1 ); }
	private void DiffUnFilterVram() { DiffUnFilter( 1, 2 ); }
	private void DiffUnFilter16() { DiffUnFilter( 2, 2 ); }

	private void DiffUnFilter( int inWidth, int outWidth )
	{
		uint src = Gba.Cpu.Registers[0] & 0xFFFFFFFC;
		uint dst = Gba.Cpu.Registers[1];

		uint header = Gba.Bus.Read32( src );
		int remaining = (int)(header >> 8);
		ushort halfword = 0;
		ushort old = 0;
		src += 4;

		while ( remaining > 0 )
		{
			ushort next;
			if ( inWidth == 1 )
				next = Gba.Bus.Read8( src );
			else
				next = Gba.Bus.Read16( src );
			next = (ushort)(next + old);

			if ( outWidth > inWidth )
			{
				halfword >>= 8;
				halfword |= (ushort)(next << 8);
				if ( (src & 1) != 0 )
				{
					Gba.Bus.Write16( dst, halfword );
					dst += (uint)outWidth;
					remaining -= outWidth;
				}
			}
			else if ( outWidth == 1 )
			{
				Gba.Bus.Write8( dst, (byte)next );
				dst += (uint)outWidth;
				remaining -= outWidth;
			}
			else
			{
				Gba.Bus.Write16( dst, next );
				dst += (uint)outWidth;
				remaining -= outWidth;
			}

			old = next;
			src += (uint)inWidth;
		}

		Gba.Cpu.Registers[0] = src;
		Gba.Cpu.Registers[1] = dst;
	}

	private void SoundDriverMain() { }
	private void SoundDriverVSync() { }
	private void SoundChannelClear() { }
	private void MidiKey2Freq()
	{
		uint waveDataPtr = Gba.Cpu.Registers[0];
		uint key = Gba.Bus.Read32( waveDataPtr + 4 );
		int midiKey = (int)Gba.Cpu.Registers[1];
		int pitchAdj = (int)Gba.Cpu.Registers[2];
		float exponent = (180f - midiKey - pitchAdj / 256f) / 12f;
		Gba.Cpu.Registers[0] = (uint)(key / MathF.Pow( 2f, exponent ));
	}
}
