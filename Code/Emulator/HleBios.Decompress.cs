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

		if ( vram && (dst & 1) != 0 )
		{
			Gba.Bus.Write16( dst & ~1u, (ushort)(halfword | (halfword << 8)) );
		}
	}

	private void WriteVramByte( ref uint dst, byte val, ref byte buffer, ref bool hasBuffered )
	{
		if ( !hasBuffered )
		{
			buffer = val;
			hasBuffered = true;
		}
		else
		{
			ushort halfword = (ushort)(buffer | (val << 8));
			Gba.Bus.Write16( dst & ~1u, halfword );
			dst += 2;
			hasBuffered = false;
		}
	}

	private void HuffUnComp()
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];

		uint header = Gba.Bus.Read32( src );
		int bitSize = (int)(header & 0xF);
		int decompSize = (int)(header >> 8);
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
				outBuffer |= (uint)data << outBits;
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
	}

	private void RLUnCompWram() { RLDecompress( false ); }
	private void RLUnCompVram() { RLDecompress( true ); }

	private void RLDecompress( bool vram )
	{
		uint src = Gba.Cpu.Registers[0];
		uint dst = Gba.Cpu.Registers[1];

		uint header = Gba.Bus.Read32( src );
		src += 4;
		int decompSize = (int)(header >> 8);

		int written = 0;
		byte halfwordBuffer = 0;
		bool hasBufferedByte = false;

		while ( written < decompSize )
		{
			byte flag = Gba.Bus.Read8( src++ );
			if ( (flag & 0x80) != 0 )
			{
				int length = (flag & 0x7F) + 3;
				byte data = Gba.Bus.Read8( src++ );
				for ( int i = 0; i < length && written < decompSize; i++ )
				{
					if ( vram )
						WriteVramByte( ref dst, data, ref halfwordBuffer, ref hasBufferedByte );
					else
						Gba.Bus.Write8( dst++, data );
					written++;
				}
			}
			else
			{
				int length = (flag & 0x7F) + 1;
				for ( int i = 0; i < length && written < decompSize; i++ )
				{
					byte data = Gba.Bus.Read8( src++ );
					if ( vram )
						WriteVramByte( ref dst, data, ref halfwordBuffer, ref hasBufferedByte );
					else
						Gba.Bus.Write8( dst++, data );
					written++;
				}
			}
		}

		if ( vram && hasBufferedByte )
		{
			Gba.Bus.Write16( dst & ~1u, (ushort)(halfwordBuffer | (halfwordBuffer << 8)) );
		}
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
