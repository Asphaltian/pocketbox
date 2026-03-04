namespace sGBA;

public partial class Ppu
{
	private void CompositePixel( int x, ushort color, int priority, byte layer )
	{
		if ( priority <= _priorityBuffer[x] )
		{
			_secondBuffer[x] = _scanlineBuffer[x];
			_secondLayerBuffer[x] = _layerBuffer[x];
			_scanlineBuffer[x] = color;
			_priorityBuffer[x] = (byte)priority;
			_layerBuffer[x] = layer;
		}
		else if ( priority <= 4 )
		{
			_secondBuffer[x] = color;
			_secondLayerBuffer[x] = layer;
		}
	}

	private void RenderTextBg( int y, int bg )
	{
		ushort cnt = BgCnt[bg];
		int priority = cnt & 3;
		int charBase = ((cnt >> 2) & 3) * 0x4000;
		bool mosaic = (cnt & 0x40) != 0;
		bool is8bpp = (cnt & 0x80) != 0;
		int mapBase = ((cnt >> 8) & 0x1F) * 0x800;
		int screenSize = (cnt >> 14) & 3;

		int bgWidth = (screenSize & 1) != 0 ? 512 : 256;
		int bgHeight = (screenSize & 2) != 0 ? 512 : 256;

		int scrollX = BgHOfs[bg];
		int scrollY = BgVOfs[bg];

		int mosaicH = 1;
		if ( mosaic )
		{
			int mosaicV = ((Mosaic >> 4) & 0xF) + 1;
			mosaicH = (Mosaic & 0xF) + 1;
			y -= y % mosaicV;
		}

		int py = (y + scrollY) % bgHeight;
		int tileY = py / 8;
		int pixelY = py % 8;

		int bgBit = 1 << bg;

		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
		{
			if ( (_windowMask[x] & bgBit) == 0 ) continue;

			int effectiveX = mosaicH > 1 ? x - (x % mosaicH) : x;
			int px = (effectiveX + scrollX) % bgWidth;
			int tileX = px / 8;
			int pixelX = px % 8;

			int screenBlock = 0;
			int localTileX = tileX;
			int localTileY = tileY;
			if ( bgWidth == 512 && tileX >= 32 ) { screenBlock += 1; localTileX -= 32; }
			if ( bgHeight == 512 && tileY >= 32 ) { screenBlock += (bgWidth == 512) ? 2 : 1; localTileY -= 32; }

			int mapOffset = mapBase + screenBlock * 0x800 + (localTileY * 32 + localTileX) * 2;
			ushort tileEntry = ReadVramHalf( (uint)mapOffset );

			int tileNum = tileEntry & 0x3FF;
			bool flipH = (tileEntry & 0x400) != 0;
			bool flipV = (tileEntry & 0x800) != 0;
			int palette = (tileEntry >> 12) & 0xF;

			int finalPixelX = flipH ? 7 - pixelX : pixelX;
			int finalPixelY = flipV ? 7 - pixelY : pixelY;

			ushort color;
			if ( is8bpp )
			{
				int tileAddr = charBase + tileNum * 64 + finalPixelY * 8 + finalPixelX;
				if ( tileAddr >= 0x10000 ) continue;
				byte palIdx = ReadVram( (uint)tileAddr );
				if ( palIdx == 0 ) continue;
				color = ReadPalette( palIdx );
			}
			else
			{
				int tileAddr = charBase + tileNum * 32 + finalPixelY * 4 + finalPixelX / 2;
				if ( tileAddr >= 0x10000 ) continue;
				byte data = ReadVram( (uint)tileAddr );
				int palIdx = (finalPixelX & 1) != 0 ? (data >> 4) : (data & 0xF);
				if ( palIdx == 0 ) continue;
				color = ReadPalette( palette * 16 + palIdx );
			}

			CompositePixel( x, color, priority, (byte)bg );
		}
	}

	private void RenderAffineBg( int y, int bg )
	{
		int idx = bg - 2;
		ushort cnt = BgCnt[bg];
		int priority = cnt & 3;
		int charBase = ((cnt >> 2) & 3) * 0x4000;
		int mapBase = ((cnt >> 8) & 0x1F) * 0x800;
		bool wrap = (cnt & 0x2000) != 0;
		bool mosaic = (cnt & 0x40) != 0;
		int screenSize = (cnt >> 14) & 3;

		int size = 128 << screenSize;
		int tilesPerRow = size / 8;

		int refX = BgX[idx];
		int refY = BgY[idx];
		short pa = BgPA[idx];
		short pc = BgPC[idx];

		int mosaicH = 1;
		if ( mosaic )
		{
			int mosaicV = ((Mosaic >> 4) & 0xF) + 1;
			mosaicH = (Mosaic & 0xF) + 1;
			if ( mosaicV > 1 )
			{
				int rewind = y % mosaicV;
				refX -= rewind * BgPB[idx];
				refY -= rewind * BgPD[idx];
			}
		}

		int bgBit = 1 << bg;

		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
		{
			if ( (_windowMask[x] & bgBit) == 0 ) continue;

			int effectiveX = mosaicH > 1 ? x - (x % mosaicH) : x;
			int texelX = (refX + pa * effectiveX) >> 8;
			int texelY = (refY + pc * effectiveX) >> 8;

			if ( wrap )
			{
				texelX &= size - 1;
				texelY &= size - 1;
			}
			else if ( texelX < 0 || texelX >= size || texelY < 0 || texelY >= size )
			{
				continue;
			}

			int tileX = texelX / 8;
			int tileY = texelY / 8;
			int pixelX = texelX & 7;
			int pixelY = texelY & 7;

			int mapOffset = mapBase + tileY * tilesPerRow + tileX;
			byte tileNum = ReadVram( (uint)mapOffset );

			int tileAddr = charBase + tileNum * 64 + pixelY * 8 + pixelX;
			byte palIdx = ReadVram( (uint)tileAddr );
			if ( palIdx == 0 ) continue;

			ushort color = ReadPalette( palIdx );
			CompositePixel( x, color, priority, (byte)bg );
		}
	}

	private void RenderMode3( int y )
	{
		ushort cnt = BgCnt[2];
		int priority = cnt & 3;
		bool mosaic = (cnt & 0x40) != 0;
		int sx = BgX[0];
		int sy = BgY[0];
		short dx = BgPA[0];
		short dy = BgPC[0];

		int mosaicH = 1;
		if ( mosaic )
		{
			int mosaicV = ((Mosaic >> 4) & 0xF) + 1;
			mosaicH = (Mosaic & 0xF) + 1;
			if ( mosaicV > 1 )
			{
				int rewind = y % mosaicV;
				sx -= rewind * BgPB[0];
				sy -= rewind * BgPD[0];
			}
		}

		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
		{
			if ( (_windowMask[x] & 4) == 0 ) continue;
			int effectiveX = mosaicH > 1 ? x - (x % mosaicH) : x;
			int texelX = (sx + dx * effectiveX) >> 8;
			int texelY = (sy + dy * effectiveX) >> 8;
			if ( texelX < 0 || texelX >= GbaConstants.ScreenWidth || texelY < 0 || texelY >= GbaConstants.ScreenHeight )
				continue;
			int addr = (texelY * GbaConstants.ScreenWidth + texelX) * 2;
			ushort color = ReadVramHalf( (uint)addr );
			CompositePixel( x, color, priority, 2 );
		}
	}

	private void RenderMode4( int y )
	{
		int page = (DispCnt & 0x10) != 0 ? 0xA000 : 0;
		ushort cnt = BgCnt[2];
		int priority = cnt & 3;
		bool mosaic = (cnt & 0x40) != 0;
		int sx = BgX[0];
		int sy = BgY[0];
		short dx = BgPA[0];
		short dy = BgPC[0];

		int mosaicH = 1;
		if ( mosaic )
		{
			int mosaicV = ((Mosaic >> 4) & 0xF) + 1;
			mosaicH = (Mosaic & 0xF) + 1;
			if ( mosaicV > 1 )
			{
				int rewind = y % mosaicV;
				sx -= rewind * BgPB[0];
				sy -= rewind * BgPD[0];
			}
		}

		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
		{
			if ( (_windowMask[x] & 4) == 0 ) continue;
			int effectiveX = mosaicH > 1 ? x - (x % mosaicH) : x;
			int texelX = (sx + dx * effectiveX) >> 8;
			int texelY = (sy + dy * effectiveX) >> 8;
			if ( texelX < 0 || texelX >= GbaConstants.ScreenWidth || texelY < 0 || texelY >= GbaConstants.ScreenHeight )
				continue;
			int addr = page + texelY * GbaConstants.ScreenWidth + texelX;
			byte palIdx = ReadVram( (uint)addr );
			if ( palIdx == 0 ) continue;
			ushort color = ReadPalette( palIdx );
			CompositePixel( x, color, priority, 2 );
		}
	}

	private void RenderMode5( int y )
	{
		int page = (DispCnt & 0x10) != 0 ? 0xA000 : 0;
		ushort cnt = BgCnt[2];
		int priority = cnt & 3;
		bool mosaic = (cnt & 0x40) != 0;
		int sx = BgX[0];
		int sy = BgY[0];
		short dx = BgPA[0];
		short dy = BgPC[0];

		int mosaicH = 1;
		if ( mosaic )
		{
			int mosaicV = ((Mosaic >> 4) & 0xF) + 1;
			mosaicH = (Mosaic & 0xF) + 1;
			if ( mosaicV > 1 )
			{
				int rewind = y % mosaicV;
				sx -= rewind * BgPB[0];
				sy -= rewind * BgPD[0];
			}
		}

		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
		{
			if ( (_windowMask[x] & 4) == 0 ) continue;
			int effectiveX = mosaicH > 1 ? x - (x % mosaicH) : x;
			int texelX = (sx + dx * effectiveX) >> 8;
			int texelY = (sy + dy * effectiveX) >> 8;
			if ( texelX < 0 || texelX >= 160 || texelY < 0 || texelY >= 128 )
				continue;
			int addr = page + (texelY * 160 + texelX) * 2;
			ushort color = ReadVramHalf( (uint)addr );
			CompositePixel( x, color, priority, 2 );
		}
	}

	private void RenderSprites( int y )
	{
		byte[] oam = Gba.Bus.Oam;

		bool objWinEnabled = (DispCnt & 0x8000) != 0;

		int objMosaicH = ((Mosaic >> 8) & 0xF) + 1;
		int objMosaicV = ((Mosaic >> 12) & 0xF) + 1;
		int mosaicY = objMosaicV > 1 ? y - (y % objMosaicV) : y;

		for ( int i = 0; i < 128; i++ )
		{
			int oamOff = i * 8;
			ushort attr0 = (ushort)(oam[oamOff] | (oam[oamOff + 1] << 8));
			ushort attr1 = (ushort)(oam[oamOff + 2] | (oam[oamOff + 3] << 8));
			ushort attr2 = (ushort)(oam[oamOff + 4] | (oam[oamOff + 5] << 8));

			int objMode = (attr0 >> 10) & 3;
			if ( objMode == 3 ) continue;
			if ( objMode == 2 && !objWinEnabled ) continue;

			bool isAffine = (attr0 & 0x100) != 0;
			bool doubleSize = isAffine && (attr0 & 0x200) != 0;
			if ( !isAffine && (attr0 & 0x200) != 0 ) continue;

			bool isMosaic = (attr0 & 0x1000) != 0;

			int shape = (attr0 >> 14) & 3;
			int sizeParam = (attr1 >> 14) & 3;

			GetSpriteSize( shape, sizeParam, out int sprWidth, out int sprHeight );

			int sprY = attr0 & 0xFF;
			if ( sprY >= 160 ) sprY -= 256;
			int sprX = attr1 & 0x1FF;
			if ( sprX >= 240 ) sprX -= 512;
			int renderWidth = sprWidth;
			int renderHeight = sprHeight;
			if ( doubleSize ) { renderWidth *= 2; renderHeight *= 2; }

			int localY = y - sprY;
			if ( localY < 0 || localY >= renderHeight ) continue;

			// OBJ vertical mosaic: snap to mosaic grid, clamp to sprite bounds
			if ( isMosaic && objMosaicV > 1 )
			{
				localY = mosaicY - sprY;
				if ( localY < 0 ) localY = 0;
				if ( localY >= renderHeight ) localY = renderHeight - 1;
			}

			int priority = (attr2 >> 10) & 3;
			int tileNum = attr2 & 0x3FF;
			int palette = (attr2 >> 12) & 0xF;
			bool is8bpp = (attr0 & 0x2000) != 0;
			bool flipH = !isAffine && (attr1 & 0x1000) != 0;
			bool flipV = !isAffine && (attr1 & 0x2000) != 0;

			bool objMapping1D = (DispCnt & 0x40) != 0;

			if ( is8bpp && !objMapping1D ) tileNum &= ~1;

			int bgModeForObj = DispCnt & 7;
			if ( bgModeForObj >= 3 && tileNum < 512 ) continue;

			short sPa = 0, sPb = 0, sPc = 0, sPd = 0;
			int cx = 0, cy = 0;
			if ( isAffine )
			{
				int affineIdx = (attr1 >> 9) & 0x1F;
				int paOff = affineIdx * 32 + 6;
				int pbOff = affineIdx * 32 + 14;
				int pcOff = affineIdx * 32 + 22;
				int pdOff = affineIdx * 32 + 30;
				sPa = (short)(oam[paOff] | (oam[paOff + 1] << 8));
				sPb = (short)(oam[pbOff] | (oam[pbOff + 1] << 8));
				sPc = (short)(oam[pcOff] | (oam[pcOff + 1] << 8));
				sPd = (short)(oam[pdOff] | (oam[pdOff + 1] << 8));
				cx = renderWidth / 2;
				cy = renderHeight / 2;
			}

			int pxStart = 0;
			int pxEnd = renderWidth;
			if ( sprX < 0 ) pxStart = -sprX;
			if ( sprX + pxEnd > GbaConstants.ScreenWidth ) pxEnd = GbaConstants.ScreenWidth - sprX;
			if ( pxStart >= pxEnd ) continue;

			for ( int px = pxStart; px < pxEnd; px++ )
			{
				int screenX = sprX + px;

				int texX, texY;
				if ( isAffine )
				{
					int effectivePx = px;
					if ( isMosaic && objMosaicH > 1 )
					{
						effectivePx = px - (screenX % objMosaicH);
						if ( effectivePx < 0 ) effectivePx = 0;
						else if ( effectivePx >= renderWidth ) effectivePx = renderWidth - 1;
					}

					int dx = effectivePx - cx;
					int dy = localY - cy;
					texX = ((sPa * dx + sPb * dy) >> 8) + sprWidth / 2;
					texY = ((sPc * dx + sPd * dy) >> 8) + sprHeight / 2;
				}
				else
				{
					int effectivePx = px;
					if ( isMosaic && objMosaicH > 1 )
					{
						effectivePx = px - (screenX % objMosaicH);
						if ( effectivePx < 0 ) effectivePx = 0;
						else if ( effectivePx >= renderWidth ) effectivePx = renderWidth - 1;
					}

					texX = flipH ? sprWidth - 1 - effectivePx : effectivePx;
					texY = flipV ? sprHeight - 1 - localY : localY;
				}

				if ( texX < 0 || texX >= sprWidth || texY < 0 || texY >= sprHeight ) continue;

				int tileX = texX / 8;
				int tileY = texY / 8;
				int pixelInTileX = texX & 7;
				int pixelInTileY = texY & 7;

				int tile;
				if ( objMapping1D )
				{
					tile = tileNum + tileY * (sprWidth / 8) * (is8bpp ? 2 : 1) + tileX * (is8bpp ? 2 : 1);
				}
				else
				{
					tile = tileNum + tileY * 32 * (is8bpp ? 2 : 1) + tileX * (is8bpp ? 2 : 1);
				}

				byte palIdx;
				if ( is8bpp )
				{
					int tileAddr = 0x10000 + tile * 32 + pixelInTileY * 8 + pixelInTileX;
					palIdx = ReadVram( (uint)tileAddr );
				}
				else
				{
					int tileAddr = 0x10000 + tile * 32 + pixelInTileY * 4 + pixelInTileX / 2;
					byte data = ReadVram( (uint)tileAddr );
					palIdx = (byte)((pixelInTileX & 1) != 0 ? (data >> 4) : (data & 0xF));
				}

				if ( palIdx == 0 )
				{
					if ( objMode != 2 && _spriteDrawn[screenX] && priority < _spritePrioBuffer[screenX] )
					{
						_spritePrioBuffer[screenX] = (byte)priority;
						_spriteSemiTransparent[screenX] = objMode == 1;
					}
					continue;
				}

				if ( objMode == 2 )
				{
					_objWindowMask[screenX] = true;
					continue;
				}

				ushort color;
				if ( is8bpp )
					color = ReadPalette( 256 + palIdx );
				else
					color = ReadPalette( 256 + palette * 16 + palIdx );

				if ( !_spriteDrawn[screenX] || priority < _spritePrioBuffer[screenX] )
				{
					_spriteBuffer[screenX] = color;
					_spritePrioBuffer[screenX] = (byte)priority;
					_spriteDrawn[screenX] = true;
					_spriteSemiTransparent[screenX] = objMode == 1;
				}
			}
		}
	}

	private static void GetSpriteSize( int shape, int size, out int w, out int h )
	{
		switch ( shape )
		{
			case 0:
				w = h = 8 << size;
				break;
			case 1:
				switch ( size )
				{
					case 0: w = 16; h = 8; break;
					case 1: w = 32; h = 8; break;
					case 2: w = 32; h = 16; break;
					case 3: w = 64; h = 32; break;
					default: w = h = 8; break;
				}
				break;
			case 2:
				switch ( size )
				{
					case 0: w = 8; h = 16; break;
					case 1: w = 8; h = 32; break;
					case 2: w = 16; h = 32; break;
					case 3: w = 32; h = 64; break;
					default: w = h = 8; break;
				}
				break;
			default:
				w = h = 8;
				break;
		}
	}
}
