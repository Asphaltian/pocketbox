namespace sGBA;

public partial class Ppu
{
	public GbaSystem Gba { get; }
	public byte[] FrameBuffer { get; set; }
	private byte[] _backBuffer;
	public bool FrameReady { get; set; }

	public int VCount;
	public int Dot;

	public ushort DispCnt;
	public ushort DispStat;
	public ushort[] BgCnt = new ushort[4];
	public short[] BgHOfs = new short[4];
	public short[] BgVOfs = new short[4];
	public short[] BgPA = new short[2];
	public short[] BgPB = new short[2];
	public short[] BgPC = new short[2];
	public short[] BgPD = new short[2];
	public int[] BgX = new int[2];
	public int[] BgY = new int[2];
	public int[] BgRefX = new int[2];
	public int[] BgRefY = new int[2];

	public ushort BldCnt;
	public ushort BldAlpha;
	public ushort BldY;

	public ushort Win0H, Win0V, Win1H, Win1V;
	public ushort WinIn, WinOut;
	public ushort Mosaic;

	private ushort[] _scanlineBuffer = new ushort[GbaConstants.ScreenWidth];
	private byte[] _priorityBuffer = new byte[GbaConstants.ScreenWidth];
	private byte[] _layerBuffer = new byte[GbaConstants.ScreenWidth];

	private ushort[] _secondBuffer = new ushort[GbaConstants.ScreenWidth];
	private byte[] _secondLayerBuffer = new byte[GbaConstants.ScreenWidth];

	private ushort[] _spriteBuffer = new ushort[GbaConstants.ScreenWidth];
	private byte[] _spritePrioBuffer = new byte[GbaConstants.ScreenWidth];
	private bool[] _spriteDrawn = new bool[GbaConstants.ScreenWidth];
	private bool[] _spriteSemiTransparent = new bool[GbaConstants.ScreenWidth];

	private byte[] _windowMask = new byte[GbaConstants.ScreenWidth];
	private bool[] _objWindowMask = new bool[GbaConstants.ScreenWidth];

	public Ppu( GbaSystem gba )
	{
		Gba = gba;
		FrameBuffer = new byte[GbaConstants.ScreenWidth * GbaConstants.ScreenHeight * 4];
		_backBuffer = new byte[GbaConstants.ScreenWidth * GbaConstants.ScreenHeight * 4];
	}

	public void Reset()
	{
		VCount = 0;
		Dot = 0;
		DispCnt = 0;
		DispStat = 0;
		FrameReady = false;
		Array.Clear( BgCnt );
		Array.Clear( BgHOfs );
		Array.Clear( BgVOfs );
		BgPA[0] = 0x100; BgPA[1] = 0x100;
		Array.Clear( BgPB );
		Array.Clear( BgPC );
		BgPD[0] = 0x100; BgPD[1] = 0x100;
		Array.Clear( BgX );
		Array.Clear( BgY );
		Array.Clear( BgRefX );
		Array.Clear( BgRefY );
	}

	public void StartHBlank()
	{
		if ( VCount < GbaConstants.VisibleLines )
		{
			RenderScanline( VCount );
		}

		DispStat |= 0x0002;

		if ( (DispStat & 0x0010) != 0 )
			Gba.Io.RaiseIrq( IrqFlag.HBlank );

		if ( VCount < GbaConstants.VisibleLines )
			Gba.Dma.OnHBlank();

		if ( VCount >= 2 && VCount < GbaConstants.VisibleLines + 2 )
			Gba.Dma.OnDisplayStart();
	}

	public void StartHDraw()
	{
		DispStat &= unchecked((ushort)~0x0002);
		VCount++;

		if ( VCount == GbaConstants.VisibleLines )
		{
			DispStat |= 0x0001;
			if ( (DispStat & 0x0008) != 0 )
				Gba.Io.RaiseIrq( IrqFlag.VBlank );
			Gba.Dma.OnVBlank();
			FrameReady = true;

			BgX[0] = BgRefX[0];
			BgY[0] = BgRefY[0];
			BgX[1] = BgRefX[1];
			BgY[1] = BgRefY[1];
		}
		else if ( VCount == GbaConstants.TotalLines - 1 )
		{
			DispStat &= unchecked((ushort)~0x0001);
			(FrameBuffer, _backBuffer) = (_backBuffer, FrameBuffer);
		}
		else if ( VCount == GbaConstants.TotalLines )
		{
			VCount = 0;
		}

		int lyc = (DispStat >> 8) & 0xFF;
		if ( VCount == lyc )
		{
			DispStat |= 0x0004;
			if ( (DispStat & 0x0020) != 0 )
				Gba.Io.RaiseIrq( IrqFlag.VCountMatch );
		}
		else
		{
			DispStat &= unchecked((ushort)~0x0004);
		}
	}

	private void ComputeWindowMask( int y )
	{
		bool win0Enable = (DispCnt & 0x2000) != 0;
		bool win1Enable = (DispCnt & 0x4000) != 0;
		bool objWinEnable = (DispCnt & 0x8000) != 0;

		if ( !win0Enable && !win1Enable && !objWinEnable )
		{
			for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
				_windowMask[x] = 0x3F;
			return;
		}

		byte outsideMask = (byte)(WinOut & 0x3F);
		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
			_windowMask[x] = outsideMask;

		if ( objWinEnable )
		{
			byte objWinMask = (byte)((WinOut >> 8) & 0x3F);
			for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
			{
				if ( _objWindowMask[x] )
					_windowMask[x] = objWinMask;
			}
		}

		if ( win1Enable )
		{
			int y1 = (Win1V >> 8) & 0xFF;
			int y2 = Win1V & 0xFF;
			bool inY = (y1 <= y2) ? (y >= y1 && y < y2) : (y >= y1 || y < y2);

			if ( inY )
			{
				int x1 = (Win1H >> 8) & 0xFF;
				int x2 = Win1H & 0xFF;
				byte win1Mask = (byte)((WinIn >> 8) & 0x3F);
				if ( x1 <= x2 )
				{
					for ( int x = x1; x < x2 && x < GbaConstants.ScreenWidth; x++ )
						_windowMask[x] = win1Mask;
				}
				else
				{
					for ( int x = 0; x < x2 && x < GbaConstants.ScreenWidth; x++ )
						_windowMask[x] = win1Mask;
					for ( int x = x1; x < GbaConstants.ScreenWidth; x++ )
						_windowMask[x] = win1Mask;
				}
			}
		}

		if ( win0Enable )
		{
			int y1 = (Win0V >> 8) & 0xFF;
			int y2 = Win0V & 0xFF;
			bool inY = (y1 <= y2) ? (y >= y1 && y < y2) : (y >= y1 || y < y2);

			if ( inY )
			{
				int x1 = (Win0H >> 8) & 0xFF;
				int x2 = Win0H & 0xFF;
				byte win0Mask = (byte)(WinIn & 0x3F);
				if ( x1 <= x2 )
				{
					for ( int x = x1; x < x2 && x < GbaConstants.ScreenWidth; x++ )
						_windowMask[x] = win0Mask;
				}
				else
				{
					for ( int x = 0; x < x2 && x < GbaConstants.ScreenWidth; x++ )
						_windowMask[x] = win0Mask;
					for ( int x = x1; x < GbaConstants.ScreenWidth; x++ )
						_windowMask[x] = win0Mask;
				}
			}
		}
	}

	private void RenderScanline( int y )
	{
		ushort backdrop = (ushort)(Gba.Bus.PaletteRam[0] | (Gba.Bus.PaletteRam[1] << 8));
		Array.Fill( _scanlineBuffer, backdrop );
		Array.Fill( _priorityBuffer, (byte)4 );
		Array.Fill( _layerBuffer, (byte)5 );
		Array.Fill( _secondBuffer, backdrop );
		Array.Fill( _secondLayerBuffer, (byte)5 );
		Array.Fill( _spriteDrawn, false );
		Array.Fill( _spriteSemiTransparent, false );
		Array.Fill( _objWindowMask, false );

		bool forcedBlank = (DispCnt & 0x80) != 0;
		if ( forcedBlank )
		{
			int fbOff = y * GbaConstants.ScreenWidth * 4;
			for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
			{
				_backBuffer[fbOff++] = 255;
				_backBuffer[fbOff++] = 255;
				_backBuffer[fbOff++] = 255;
				_backBuffer[fbOff++] = 255;
			}
			return;
		}

		int bgMode = DispCnt & 7;

		bool objEnabled = (DispCnt & 0x1000) != 0;
		if ( objEnabled )
			RenderSprites( y );

		ComputeWindowMask( y );

		for ( int prio = 3; prio >= 0; prio-- )
		{
			switch ( bgMode )
			{
				case 0:
					for ( int bg = 3; bg >= 0; bg-- )
						if ( (DispCnt & (1 << (8 + bg))) != 0 && (BgCnt[bg] & 3) == prio )
							RenderTextBg( y, bg );
					break;
				case 1:
					if ( (DispCnt & 0x0400) != 0 && (BgCnt[2] & 3) == prio )
						RenderAffineBg( y, 2 );
					for ( int bg = 1; bg >= 0; bg-- )
						if ( (DispCnt & (1 << (8 + bg))) != 0 && (BgCnt[bg] & 3) == prio )
							RenderTextBg( y, bg );
					break;
				case 2:
					for ( int bg = 3; bg >= 2; bg-- )
						if ( (DispCnt & (1 << (8 + bg))) != 0 && (BgCnt[bg] & 3) == prio )
							RenderAffineBg( y, bg );
					break;
				case 3:
					if ( prio == (BgCnt[2] & 3) && (DispCnt & 0x0400) != 0 )
						RenderMode3( y );
					break;
				case 4:
					if ( prio == (BgCnt[2] & 3) && (DispCnt & 0x0400) != 0 )
						RenderMode4( y );
					break;
				case 5:
					if ( prio == (BgCnt[2] & 3) && (DispCnt & 0x0400) != 0 )
						RenderMode5( y );
					break;
			}

			if ( objEnabled )
			{
				for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
				{
					if ( !_spriteDrawn[x] || _spritePrioBuffer[x] != prio ) continue;
					if ( (_windowMask[x] & 0x10) == 0 ) continue;

					if ( prio <= _priorityBuffer[x] )
					{
						_secondBuffer[x] = _scanlineBuffer[x];
						_secondLayerBuffer[x] = _layerBuffer[x];
						_scanlineBuffer[x] = _spriteBuffer[x];
						_priorityBuffer[x] = (byte)prio;
						_layerBuffer[x] = 4;
					}
					else
					{
						_secondBuffer[x] = _spriteBuffer[x];
						_secondLayerBuffer[x] = 4;
					}
				}
			}
		}

		OutputScanline( y );

		if ( bgMode >= 1 )
		{
			BgX[0] += BgPB[0];
			BgY[0] += BgPD[0];
			if ( bgMode == 2 )
			{
				BgX[1] += BgPB[1];
				BgY[1] += BgPD[1];
			}
		}
	}

	private void OutputScanline( int y )
	{
		int fbOff = y * GbaConstants.ScreenWidth * 4;
		var fb = _backBuffer;

		int bldMode = (BldCnt >> 6) & 3;
		int firstTarget = BldCnt & 0x3F;
		int secondTarget = (BldCnt >> 8) & 0x3F;

		int eva = BldAlpha & 0x1F;
		if ( eva > 16 ) eva = 16;
		int evb = (BldAlpha >> 8) & 0x1F;
		if ( evb > 16 ) evb = 16;

		int evy = BldY & 0x1F;
		if ( evy > 16 ) evy = 16;

		for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
		{
			ushort color = _scanlineBuffer[x];
			int topLayer = _layerBuffer[x];
			int topBit = LayerToBit( topLayer );

			bool isSemiTransparentObj = (topLayer == 4) && _spriteDrawn[x] && _spriteSemiTransparent[x];

			bool blendEnabled = (_windowMask[x] & 0x20) != 0;

			if ( isSemiTransparentObj && (secondTarget & LayerToBit( _secondLayerBuffer[x] )) != 0 )
			{
				color = AlphaBlend( color, _secondBuffer[x], eva, evb );
			}
			else if ( blendEnabled && bldMode == 1 && (firstTarget & topBit) != 0 )
			{
				int secondLayer = _secondLayerBuffer[x];
				if ( (secondTarget & LayerToBit( secondLayer )) != 0 )
				{
					color = AlphaBlend( color, _secondBuffer[x], eva, evb );
				}
			}
			else if ( blendEnabled && bldMode == 2 && (firstTarget & topBit) != 0 )
			{
				color = BrightnessIncrease( color, evy );
			}
			else if ( blendEnabled && bldMode == 3 && (firstTarget & topBit) != 0 )
			{
				color = BrightnessDecrease( color, evy );
			}

			int r = (color & 0x1F) << 3;
			int g = ((color >> 5) & 0x1F) << 3;
			int b = ((color >> 10) & 0x1F) << 3;

			fb[fbOff++] = (byte)r;
			fb[fbOff++] = (byte)g;
			fb[fbOff++] = (byte)b;
			fb[fbOff++] = 255;
		}
	}

	private static int LayerToBit( int layer )
	{
		return layer switch
		{
			0 => 1,
			1 => 2,
			2 => 4,
			3 => 8,
			4 => 16,
			_ => 32,
		};
	}

	private static ushort AlphaBlend( ushort top, ushort bottom, int eva, int evb )
	{
		int r1 = top & 0x1F, g1 = (top >> 5) & 0x1F, b1 = (top >> 10) & 0x1F;
		int r2 = bottom & 0x1F, g2 = (bottom >> 5) & 0x1F, b2 = (bottom >> 10) & 0x1F;
		int r = Math.Min( 31, (r1 * eva + r2 * evb) >> 4 );
		int g = Math.Min( 31, (g1 * eva + g2 * evb) >> 4 );
		int b = Math.Min( 31, (b1 * eva + b2 * evb) >> 4 );

		return (ushort)(r | (g << 5) | (b << 10));
	}

	private static ushort BrightnessIncrease( ushort color, int evy )
	{
		int r = color & 0x1F, g = (color >> 5) & 0x1F, b = (color >> 10) & 0x1F;
		r += ((31 - r) * evy) >> 4;
		g += ((31 - g) * evy) >> 4;
		b += ((31 - b) * evy) >> 4;
		return (ushort)(r | (g << 5) | (b << 10));
	}

	private static ushort BrightnessDecrease( ushort color, int evy )
	{
		int r = color & 0x1F, g = (color >> 5) & 0x1F, b = (color >> 10) & 0x1F;
		r -= (r * evy) >> 4;
		g -= (g * evy) >> 4;
		b -= (b * evy) >> 4;
		return (ushort)(r | (g << 5) | (b << 10));
	}

	private byte ReadVram( uint offset )
	{
		offset &= 0x1FFFF;
		if ( offset >= 0x18000 ) offset -= 0x8000;
		return Gba.Bus.Vram[offset];
	}

	private ushort ReadVramHalf( uint offset )
	{
		offset &= 0x1FFFF;
		if ( offset >= 0x18000 ) offset -= 0x8000;
		return (ushort)(Gba.Bus.Vram[offset] | (Gba.Bus.Vram[offset + 1] << 8));
	}

	private ushort ReadPalette( int index )
	{
		int off = index * 2;
		return (ushort)(Gba.Bus.PaletteRam[off] | (Gba.Bus.PaletteRam[off + 1] << 8));
	}
}
