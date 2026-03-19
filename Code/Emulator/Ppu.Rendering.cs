using System.Runtime.InteropServices;
using System.Threading;
using Sandbox.Rendering;

namespace sGBA;

public partial class Ppu
{
	[StructLayout( LayoutKind.Sequential )]
	public struct ScanlineState
	{
		public uint DispCntMosaic;
		public uint BgCnt01;
		public uint BgCnt23;
		public uint BgOffset0, BgOffset1, BgOffset2, BgOffset3;
		public int Bg2PA, Bg2PC;
		public int Bg2X, Bg2Y;
		public int Bg3PA, Bg3PC;
		public int Bg3X, Bg3Y;
		public uint BldCntAlpha;
		public uint BldYWin0H;
		public uint Win0VWin1H;
		public uint Win1VWinIn;
		public uint WinOutPad;
		public int FirstAffine;
		public uint EnabledAtYMask;
	}

	[StructLayout( LayoutKind.Sequential )]
	public struct GpuSprite
	{
		public int X, Y;
		public int Width, Height;
		public int RenderWidth, RenderHeight;
		public uint CharBase;
		public uint Tile;
		public uint Stride;
		public uint Palette;
		public uint Flags;
		public int PA, PB, PC, PD;
		public int Cycles;
	}

	public int GpuScale { get; private set; }
	public Texture OutputTexture { get; private set; }
	public CommandList RenderCommandList { get; private set; }
	public bool GpuReady { get; private set; }

	private int _scaledWidth;
	private int _scaledHeight;

	private GpuBuffer<ScanlineState> _gpuScanlines;
	private GpuBuffer<GpuSprite> _gpuSprites;
	private GpuBuffer<uint> _gpuVram;
	private GpuBuffer<uint> _gpuPalette;

	private Texture _bg0Tex, _bg1Tex, _bg2Tex, _bg3Tex;
	private Texture _objColorTex, _objFlagsTex, _objWindowMaskTex, _windowTex;

	private ComputeShader _csBgMode0, _csBgMode2, _csBgMode3, _csBgMode4, _csBgMode5;
	private ComputeShader _csObj, _csWindow, _csFinalize;

	private ScanlineState[][] _scanlineFrames;
	private uint[][] _paletteFrames;
	private GpuSprite[][] _spriteFrames;
	private int[] _spriteCountFrames;
	private uint[][] _vramFrames;

	private int _writeSlot;
	private int _readSlot = -1;
	private int _frameSpriteCount;

	private uint[] _frameOldCharBase = new uint[2];
	private int[] _frameOldCharBaseFirstY = new int[2];

	public void InitGpu( int scale = 1 )
	{
		GpuScale = Math.Max( 1, scale );
		_scaledWidth = GbaConstants.ScreenWidth * GpuScale;
		_scaledHeight = GbaConstants.ScreenHeight * GpuScale;

		_scanlineFrames = new ScanlineState[2][];
		_paletteFrames = new uint[2][];
		_spriteFrames = new GpuSprite[2][];
		_spriteCountFrames = new int[2];
		_vramFrames = new uint[2][];
		for ( int i = 0; i < 2; i++ )
		{
			_scanlineFrames[i] = new ScanlineState[GbaConstants.VisibleLines];
			_paletteFrames[i] = new uint[256 * GbaConstants.VisibleLines];
			_spriteFrames[i] = new GpuSprite[128];
			_vramFrames[i] = new uint[96 * 1024 / 4];
		}

		_gpuScanlines = new GpuBuffer<ScanlineState>( GbaConstants.VisibleLines );
		_gpuSprites = new GpuBuffer<GpuSprite>( 128 );
		_gpuVram = new GpuBuffer<uint>( 96 * 1024 / 4 );
		_gpuPalette = new GpuBuffer<uint>( 256 * GbaConstants.VisibleLines );

		_bg0Tex = CreateLayerRT();
		_bg1Tex = CreateLayerRT();
		_bg2Tex = CreateLayerRT();
		_bg3Tex = CreateLayerRT();
		_objColorTex = CreateLayerRT();
		_objFlagsTex = CreateUintRT();
		_objWindowMaskTex = CreateUintRT();
		_windowTex = CreateUintRT();

		OutputTexture = Texture.CreateRenderTarget()
			.WithSize( _scaledWidth, _scaledHeight )
			.WithFormat( ImageFormat.RGBA8888 )
			.WithUAVBinding()
			.WithDynamicUsage()
			.Create();

		_csBgMode0 = new ComputeShader( "shaders/gba_bg_mode0.shader" );
		_csBgMode2 = new ComputeShader( "shaders/gba_bg_mode2.shader" );
		_csBgMode3 = new ComputeShader( "shaders/gba_bg_mode3.shader" );
		_csBgMode4 = new ComputeShader( "shaders/gba_bg_mode4.shader" );
		_csBgMode5 = new ComputeShader( "shaders/gba_bg_mode5.shader" );
		_csObj = new ComputeShader( "shaders/gba_obj.shader" );
		_csWindow = new ComputeShader( "shaders/gba_window.shader" );
		_csFinalize = new ComputeShader( "shaders/gba_finalize.shader" );

		RenderCommandList = new CommandList( "GBA PPU" );
		GpuReady = true;
	}

	public void DisposeGpu()
	{
		GpuReady = false;
		_gpuScanlines?.Dispose();
		_gpuSprites?.Dispose();
		_gpuVram?.Dispose();
		_gpuPalette?.Dispose();
		RenderCommandList = null;
		OutputTexture = null;
	}

	private Texture CreateLayerRT()
	{
		return Texture.CreateRenderTarget()
			.WithSize( _scaledWidth, _scaledHeight )
			.WithFormat( ImageFormat.RGBA8888 )
			.WithUAVBinding()
			.Create();
	}

	private Texture CreateUintRT()
	{
		return Texture.CreateRenderTarget()
			.WithSize( _scaledWidth, _scaledHeight )
			.WithFormat( ImageFormat.R32_UINT )
			.WithUAVBinding()
			.Create();
	}

	private void CaptureScanline( int y )
	{
		if ( !GpuReady ) return;

		var scanlines = _scanlineFrames[_writeSlot];
		ref var s = ref scanlines[y];

		s.DispCntMosaic = (uint)DispCnt | ((uint)Mosaic << 16);
		s.BgCnt01 = (uint)BgCnt[0] | ((uint)BgCnt[1] << 16);
		s.BgCnt23 = (uint)BgCnt[2] | ((uint)BgCnt[3] << 16);

		s.BgOffset0 = PackOffset( BgHOfs[0], BgVOfs[0] );
		s.BgOffset1 = PackOffset( BgHOfs[1], BgVOfs[1] );
		s.BgOffset2 = PackOffset( BgHOfs[2], BgVOfs[2] );
		s.BgOffset3 = PackOffset( BgHOfs[3], BgVOfs[3] );

		s.Bg2PA = BgPA[0];
		s.Bg2PC = BgPC[0];
		s.Bg2X = BgX[0];
		s.Bg2Y = BgY[0];
		s.Bg3PA = BgPA[1];
		s.Bg3PC = BgPC[1];
		s.Bg3X = BgX[1];
		s.Bg3Y = BgY[1];

		s.BldCntAlpha = (uint)BldCnt | ((uint)BldAlpha << 16);
		s.BldYWin0H = (uint)BldY | ((uint)Win0H << 16);
		s.Win0VWin1H = (uint)Win0V | ((uint)Win1H << 16);
		s.Win1VWinIn = (uint)Win1V | ((uint)WinIn << 16);
		s.WinOutPad = (uint)WinOut;

		s.FirstAffine = _firstAffine;

		uint enabledMask = 0;
		for ( int i = 0; i < 4; i++ )
		{
			if ( (DispCnt & (0x100 << i)) != 0 && y >= _enabledAtY[i] )
				enabledMask |= (1u << i);
		}
		s.EnabledAtYMask = enabledMask;

		var ram = Gba.Bus.PaletteRam;
		var palDst = _paletteFrames[_writeSlot];
		int baseIdx = y * 256;
		Buffer.BlockCopy( ram, 0, palDst, baseIdx * 4, 1024 );
	}

	private void PrepareSprites()
	{
		if ( !GpuReady ) return;

		var sprites = _spriteFrames[_writeSlot];
		byte[] oam = Gba.Bus.Oam;
		bool mapping1D = (DispCnt & 0x40) != 0;
		int bgMode = DispCnt & 7;
		int count = 0;

		for ( int i = 0; i < 128 && count < 128; i++ )
		{
			int off = i * 8;
			ushort attr0 = (ushort)(oam[off] | (oam[off + 1] << 8));
			ushort attr1 = (ushort)(oam[off + 2] | (oam[off + 3] << 8));
			ushort attr2 = (ushort)(oam[off + 4] | (oam[off + 5] << 8));

			int objMode = (attr0 >> 10) & 3;
			if ( objMode == 3 ) continue;

			bool isAffine = (attr0 & 0x100) != 0;
			bool doubleSize = isAffine && (attr0 & 0x200) != 0;
			if ( !isAffine && (attr0 & 0x200) != 0 ) continue;

			int shape = (attr0 >> 14) & 3;
			int sizeParam = (attr1 >> 14) & 3;
			GetSpriteSize( shape, sizeParam, out int w, out int h );

			int sprY = attr0 & 0xFF;
			if ( sprY >= 160 ) sprY -= 256;
			int sprX = attr1 & 0x1FF;
			if ( sprX >= 240 ) sprX -= 512;

			int tileNum = attr2 & 0x3FF;
			bool is8bpp = (attr0 & 0x2000) != 0;

			if ( bgMode >= 3 && tileNum < 512 ) continue;

			int align = is8bpp && !mapping1D ? 1 : 0;
			uint charBase = (uint)((0x10000 >> 1) + ((tileNum & ~align) * 0x10));
			uint tile = 0;
			if ( !mapping1D )
			{
				if ( is8bpp )
					tile = (charBase >> 5) & 0xFu;
				else
					tile = (charBase >> 4) & 0x1Fu;
				charBase &= ~0x1FFu;
			}
			uint stride = mapping1D ? (uint)(w >> 3) : (uint)(0x20 >> (is8bpp ? 1 : 0));

			ref var spr = ref sprites[count];
			spr.X = sprX;
			spr.Y = sprY;
			spr.Width = w;
			spr.Height = h;
			spr.RenderWidth = doubleSize ? w * 2 : w;
			spr.RenderHeight = doubleSize ? h * 2 : h;
			spr.CharBase = charBase;
			spr.Tile = tile;
			spr.Stride = stride;
			spr.Palette = (uint)((attr2 >> 12) & 0xF);

			bool flipH = !isAffine && (attr1 & 0x1000) != 0;
			bool flipV = !isAffine && (attr1 & 0x2000) != 0;
			bool isMosaic = (attr0 & 0x1000) != 0;
			int priority = (attr2 >> 10) & 3;

			spr.Flags = 0;
			if ( is8bpp ) spr.Flags |= 1u;
			if ( flipH ) spr.Flags |= 2u;
			if ( flipV ) spr.Flags |= 4u;
			if ( isAffine ) spr.Flags |= 8u;
			if ( doubleSize ) spr.Flags |= 16u;
			if ( isMosaic ) spr.Flags |= 64u;
			if ( objMode == 1 ) spr.Flags |= 128u;
			if ( objMode == 2 ) spr.Flags |= 256u;
			spr.Flags |= (uint)(priority << 10);

			int cycles;
			if ( isAffine )
			{
				int renderW = doubleSize ? w * 2 : w;
				cycles = 8 + renderW * 2;
				if ( sprX < 0 )
					cycles += sprX;
			}
			else
			{
				cycles = w - 2;
				if ( sprX < 0 )
				{
					if ( sprX + w < 0 ) continue;
					cycles += sprX >> 1;
				}
			}
			spr.Cycles = cycles;

			if ( isAffine )
			{
				int affIdx = (attr1 >> 9) & 0x1F;
				int paOff = affIdx * 32 + 6;
				int pbOff = affIdx * 32 + 14;
				int pcOff = affIdx * 32 + 22;
				int pdOff = affIdx * 32 + 30;
				spr.PA = (short)(oam[paOff] | (oam[paOff + 1] << 8));
				spr.PB = (short)(oam[pbOff] | (oam[pbOff + 1] << 8));
				spr.PC = (short)(oam[pcOff] | (oam[pcOff + 1] << 8));
				spr.PD = (short)(oam[pdOff] | (oam[pdOff + 1] << 8));
			}
			else
			{
				spr.PA = flipH ? -256 : 256;
				spr.PB = 0;
				spr.PC = 0;
				spr.PD = flipV ? -256 : 256;
			}

			count++;
		}

		_spriteCountFrames[_writeSlot] = count;
	}

	private void SnapshotVram()
	{
		if ( !GpuReady ) return;
		Buffer.BlockCopy( Gba.Bus.Vram, 0, _vramFrames[_writeSlot], 0, Gba.Bus.Vram.Length );
	}

	private void CommitFrame()
	{
		if ( !GpuReady ) return;
		Array.Copy( _oldCharBase, _frameOldCharBase, 2 );
		Array.Copy( _oldCharBaseFirstY, _frameOldCharBaseFirstY, 2 );
		Interlocked.Exchange( ref _readSlot, _writeSlot );
		_writeSlot ^= 1;
	}

	public bool UploadAndBuildCommandList()
	{
		int slot = Interlocked.CompareExchange( ref _readSlot, 0, 0 );
		if ( slot < 0 ) return false;

		var scanlines = _scanlineFrames[slot];

		_gpuScanlines.SetData( scanlines );
		int sprCount = _spriteCountFrames[slot];
		_gpuSprites.SetData( _spriteFrames[slot].AsSpan( 0, Math.Max( 1, sprCount ) ) );
		_gpuVram.SetData( _vramFrames[slot] );
		_gpuPalette.SetData( _paletteFrames[slot] );

		_frameSpriteCount = sprCount;

		uint modesMask = 0;
		for ( int y = 0; y < GbaConstants.VisibleLines; y++ )
			modesMask |= 1u << (int)(scanlines[y].DispCntMosaic & 7);

		DetectCircle( scanlines, 0, out var circle0 );
		DetectCircle( scanlines, 1, out var circle1 );

		var cmd = RenderCommandList;
		cmd.Reset();

		cmd.Attributes.Set( "ScanlineStates", _gpuScanlines );
		cmd.Attributes.Set( "Vram", _gpuVram );
		cmd.Attributes.Set( "Palette", _gpuPalette );
		cmd.Attributes.Set( "Scale", GpuScale );

		cmd.Attributes.Set( "Sprites", _gpuSprites );
		cmd.Attributes.Set( "SpriteCount", _frameSpriteCount );
		cmd.Attributes.Set( "OutputColor", _objColorTex );
		cmd.Attributes.Set( "OutputFlags", _objFlagsTex );
		cmd.Attributes.Set( "ObjWindowMask", _objWindowMaskTex );
		cmd.DispatchCompute( _csObj, _scaledWidth, _scaledHeight, 1 );

		cmd.UavBarrier( _objColorTex );
		cmd.UavBarrier( _objFlagsTex );
		cmd.UavBarrier( _objWindowMaskTex );

		cmd.Attributes.Set( "OutputMask", _windowTex );
		cmd.Attributes.Set( "Circle0", circle0 );
		cmd.Attributes.Set( "Circle1", circle1 );
		cmd.DispatchCompute( _csWindow, _scaledWidth, _scaledHeight, 1 );

		cmd.UavBarrier( _windowTex );

		cmd.Attributes.Set( "OldCharBase2", new Vector2( _frameOldCharBase[0], _frameOldCharBaseFirstY[0] ) );
		cmd.Attributes.Set( "OldCharBase3", new Vector2( _frameOldCharBase[1], _frameOldCharBaseFirstY[1] ) );

		Texture[] bgTex = [_bg0Tex, _bg1Tex, _bg2Tex, _bg3Tex];
		for ( int bg = 0; bg < 4; bg++ )
		{
			cmd.Attributes.Set( "BgIndex", bg );
			cmd.Attributes.Set( "OutputTex", bgTex[bg] );

			var shaders = GetBgShaders( bg, modesMask );
			bool first = true;
			foreach ( var shader in shaders )
			{
				cmd.Attributes.Set( "IsBasePass", first ? 1 : 0 );
				cmd.DispatchCompute( shader, _scaledWidth, _scaledHeight, 1 );
				if ( shaders.Count > 1 )
					cmd.UavBarrier( bgTex[bg] );
				first = false;
			}
		}

		cmd.UavBarrier( _bg0Tex );
		cmd.UavBarrier( _bg1Tex );
		cmd.UavBarrier( _bg2Tex );
		cmd.UavBarrier( _bg3Tex );

		cmd.Attributes.Set( "Bg0Tex", _bg0Tex );
		cmd.Attributes.Set( "Bg1Tex", _bg1Tex );
		cmd.Attributes.Set( "Bg2Tex", _bg2Tex );
		cmd.Attributes.Set( "Bg3Tex", _bg3Tex );
		cmd.Attributes.Set( "ObjColorTex", _objColorTex );
		cmd.Attributes.Set( "ObjFlagsTex", _objFlagsTex );
		cmd.Attributes.Set( "WindowTex", _windowTex );
		cmd.Attributes.Set( "OutputTex", OutputTexture );
		cmd.DispatchCompute( _csFinalize, _scaledWidth, _scaledHeight, 1 );

		return true;
	}

	private List<ComputeShader> GetBgShaders( int bg, uint modesMask )
	{
		var result = new List<ComputeShader>( 2 );

		switch ( bg )
		{
			case 0:
			case 1:
				if ( (modesMask & 0b11) != 0 )
					result.Add( _csBgMode0 );
				break;

			case 2:
				if ( (modesMask & 0b001) != 0 ) result.Add( _csBgMode0 );
				if ( (modesMask & 0b110) != 0 ) result.Add( _csBgMode2 );
				if ( (modesMask & 0b001000) != 0 ) result.Add( _csBgMode3 );
				if ( (modesMask & 0b010000) != 0 ) result.Add( _csBgMode4 );
				if ( (modesMask & 0b100000) != 0 ) result.Add( _csBgMode5 );
				break;

			case 3:
				if ( (modesMask & 0b001) != 0 ) result.Add( _csBgMode0 );
				if ( (modesMask & 0b100) != 0 ) result.Add( _csBgMode2 );
				break;
		}

		return result;
	}

	private void DetectCircle( ScanlineState[] states, int window, out Vector3 result )
	{
		result = Vector3.Zero;
		if ( GpuScale < 2 ) return;

		int firstY = 0;

		float centerX = -1, centerY = -1, radius = 0;
		bool invalid = false;
		int circleFirstY = -1;
		int lastStartX = 0, lastEndX = 0;
		int startX = 0, endX = 0;

		for ( int y = firstY; y < GbaConstants.VisibleLines; y++ )
		{
			ref var s = ref states[y];

			uint winH, winV;
			if ( window == 0 )
			{
				winH = (s.BldYWin0H >> 16) & 0xFFFFu;
				winV = s.Win0VWin1H & 0xFFFFu;
			}
			else
			{
				winH = (s.Win0VWin1H >> 16) & 0xFFFFu;
				winV = s.Win1VWinIn & 0xFFFFu;
			}

			lastStartX = startX;
			lastEndX = endX;
			startX = (int)((winH >> 8) & 0xFF);
			endX = (int)(winH & 0xFF);
			int startY = (int)((winV >> 8) & 0xFF);
			int endY = (int)(winV & 0xFF);

			if ( startX == endX || y < startY || y >= endY )
			{
				if ( circleFirstY >= 0 )
				{
					centerY = (circleFirstY + y) / 2.0f;
					circleFirstY = -1;
				}
				continue;
			}
			if ( lastEndX - lastStartX <= 0 ) continue;

			if ( startX >= 240 ) { invalid = true; break; }

			int startDiff = lastStartX - startX;
			int endDiff = endX - lastEndX;
			if ( startDiff - endDiff < -1 || startDiff - endDiff > 1 )
			{
				invalid = true; break;
			}

			if ( startX < lastStartX )
			{
				centerX = (startX + endX) / 2.0f;
				if ( radius > 0 ) { invalid = true; break; }
			}
			else if ( startX > lastStartX && radius <= 0 )
			{
				radius = (lastEndX - lastStartX) / 2.0f;
			}

			if ( circleFirstY < 0 )
			{
				int prevStartY = (int)((window == 0 ? states[Math.Max( 0, y - 1 )].Win0VWin1H & 0xFFFFu : states[Math.Max( 0, y - 1 )].Win1VWinIn & 0xFFFFu) >> 8 & 0xFF);
				int prevEndY = (int)((window == 0 ? states[Math.Max( 0, y - 1 )].Win0VWin1H & 0xFFFFu : states[Math.Max( 0, y - 1 )].Win1VWinIn & 0xFFFFu) & 0xFF);
				if ( y - 1 >= prevStartY && y - 1 < prevEndY )
					circleFirstY = y - 1;
			}
		}

		if ( radius <= 0 || centerX < 0 || centerY < 0 ) return;

		for ( int y = firstY; y < GbaConstants.VisibleLines && !invalid; y++ )
		{
			ref var s = ref states[y];
			uint winH, winV;
			if ( window == 0 )
			{
				winH = (s.BldYWin0H >> 16) & 0xFFFFu;
				winV = s.Win0VWin1H & 0xFFFFu;
			}
			else
			{
				winH = (s.Win0VWin1H >> 16) & 0xFFFFu;
				winV = s.Win1VWinIn & 0xFFFFu;
			}

			int sx = (int)((winH >> 8) & 0xFF);
			int ex = (int)(winH & 0xFF);
			int sy = (int)((winV >> 8) & 0xFF);
			int ey = (int)(winV & 0xFF);
			bool xActive = sx < ex;
			bool yActive = y >= sy && y < ey;

			if ( xActive && yActive )
			{
				float dy = Math.Abs( centerY - y );
				if ( dy > radius ) { invalid = true; break; }
				float sine = MathF.Sqrt( radius * radius - dy * dy );
				if ( MathF.Abs( centerX - sine - sx ) <= 1 && MathF.Abs( centerX + sine - ex ) <= 1 )
					continue;
				if ( radius >= dy + 1 )
				{
					float sine2 = MathF.Sqrt( radius * radius - (dy + 1) * (dy + 1) );
					if ( MathF.Abs( centerX - sine2 - sx ) <= 1 && MathF.Abs( centerX + sine2 - ex ) <= 1 )
						continue;
				}
				invalid = true;
			}
			else if ( Math.Abs( centerY - y ) < radius )
			{
				invalid = true;
			}
		}

		if ( !invalid )
			result = new Vector3( centerX, centerY, radius - 0.499f );
	}

	public byte[] CaptureScreenshot()
	{
		if ( OutputTexture == null ) return null;

		var pixels = OutputTexture.GetPixels();
		var result = new byte[GbaConstants.ScreenWidth * GbaConstants.ScreenHeight * 4];

		if ( GpuScale == 1 )
		{
			for ( int i = 0; i < pixels.Length && i * 4 + 3 < result.Length; i++ )
			{
				result[i * 4] = pixels[i].r;
				result[i * 4 + 1] = pixels[i].g;
				result[i * 4 + 2] = pixels[i].b;
				result[i * 4 + 3] = pixels[i].a;
			}
		}
		else
		{
			for ( int y = 0; y < GbaConstants.ScreenHeight; y++ )
			{
				int srcY = y * GpuScale;
				for ( int x = 0; x < GbaConstants.ScreenWidth; x++ )
				{
					int srcIdx = srcY * _scaledWidth + x * GpuScale;
					int dstIdx = (y * GbaConstants.ScreenWidth + x) * 4;
					result[dstIdx] = pixels[srcIdx].r;
					result[dstIdx + 1] = pixels[srcIdx].g;
					result[dstIdx + 2] = pixels[srcIdx].b;
					result[dstIdx + 3] = pixels[srcIdx].a;
				}
			}
		}

		return result;
	}

	private static uint PackOffset( short h, short v )
	{
		return (uint)(h & 0x1FF) | (uint)((v & 0x1FF) << 16);
	}

	private static uint PackS16( short a, short b )
	{
		return (uint)(ushort)a | ((uint)(ushort)b << 16);
	}

	private static void GetSpriteSize( int shape, int size, out int w, out int h )
	{
		switch ( shape )
		{
			case 0:
				w = h = 8 << size;
				break;
			case 1:
				(w, h) = size switch
				{
					0 => (16, 8),
					1 => (32, 8),
					2 => (32, 16),
					_ => (64, 32),
				};
				break;
			case 2:
				(w, h) = size switch
				{
					0 => (8, 16),
					1 => (8, 32),
					2 => (16, 32),
					_ => (32, 64),
				};
				break;
			default:
				w = h = 8;
				break;
		}
	}
}
