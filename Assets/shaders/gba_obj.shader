HEADER
{
	DevShader = true;
	Description = "GBA OBJ Sprite Renderer";
}

MODES
{
	Default();
}

FEATURES
{
}

CS
{
	#include "gba_common.hlsl"

	StructuredBuffer<ScanlineState> States < Attribute( "ScanlineStates" ); >;
	StructuredBuffer<uint> Vram < Attribute( "Vram" ); >;
	StructuredBuffer<uint> Palette < Attribute( "Palette" ); >;
	StructuredBuffer<GpuSprite> Sprites < Attribute( "Sprites" ); >;
	int Scale < Attribute( "Scale" ); >;

	RWTexture2D<float4> OutputColor < Attribute( "OutputColor" ); >;
	RWTexture2D<uint>   OutputFlags < Attribute( "OutputFlags" ); >;
	RWTexture2D<uint>   WindowTex < Attribute( "WindowTex" ); >;

	#define FLAG_8BPP          1u
	#define FLAG_FLIPH         2u
	#define FLAG_FLIPV         4u
	#define FLAG_AFFINE        8u
	#define FLAG_DOUBLESIZE   16u
	#define FLAG_MOSAIC       64u
	#define FLAG_SEMITRANS   128u
	#define FLAG_OBJWINDOW   256u

	#define OBJ_LENGTH            1210
	#define OBJ_HBLANK_FREE_LENGTH 954

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint nativeY = id.y / (uint)Scale;
		uint nativeX = id.x / (uint)Scale;

		OutputColor[id.xy] = float4( 0, 0, 0, 0 );
		OutputFlags[id.xy] = 0xFFFFFFFFu;

		if ( nativeY >= 160u || nativeX >= 240u )
			return;

		ScanlineState state = States[nativeY];
		uint dispCnt = GetDispCnt( state );
		uint winOut = UnpackLow16( state.WinOutPad );

		if ( ( dispCnt & 0x1000u ) == 0u )
			return;

		uint oamState = state.OamState;
		int oamOffset = (int)( oamState & 0xFFFFu );
		int oamMax = (int)( ( oamState >> 16u ) & 0xFFFFu );

		int screenX = (int)nativeX;
		int screenY = (int)nativeY;
		float screenXf = ( (float)id.x + 0.5 ) / (float)Scale;
		float screenYf = ( (float)id.y + 0.5 ) / (float)Scale;

		int cycleBudget = ( dispCnt & 0x20u ) != 0u ? OBJ_HBLANK_FREE_LENGTH : OBJ_LENGTH;
		int cyclesUsed = 0;

		int bestPriority = 5;
		float4 bestColor = float4( 0, 0, 0, 0 );
		uint bestFlags = 0;

		uint mosaicReg = GetMosaic( state );
		int objMosH = (int)( ( mosaicReg >> 8u ) & 0xFu ) + 1;
		int objMosV = (int)( ( mosaicReg >> 12u ) & 0xFu ) + 1;

		for ( int i = 0; i < oamMax; i++ )
		{
			GpuSprite spr = Sprites[oamOffset + i];
			uint flags = spr.Flags;
			int priority = (int)( ( flags >> 10u ) & 3u );

			int rw = spr.RenderWidth;
			int rh = spr.RenderHeight;
			int relX = screenX - spr.X;
			int relY = screenY - spr.Y;
			float relXf = screenXf - (float)spr.X;
			float relYf = screenYf - (float)spr.Y;

			if ( relX < 0 && spr.X > 240 - rw ) { relX += 512; relXf += 512.0; }
			if ( relY < 0 && spr.Y > 160 - rh ) { relY += 256; relYf += 256.0; }

			if ( relY < 0 || relY >= rh )
				continue;

			if ( cyclesUsed >= cycleBudget )
				break;
			cyclesUsed += spr.Cycles;

			if ( relX < 0 || relX >= rw )
				continue;

			float inX = relXf;
			float inY = relYf;

			if ( ( flags & FLAG_MOSAIC ) != 0u )
			{
				bool isHFlip = ( ( flags & FLAG_FLIPH ) != 0u ) && ( ( flags & FLAG_AFFINE ) == 0u );

				if ( objMosH > 1 )
				{
					if ( !isHFlip )
					{
						int mx = MosaicFloor( spr.X + relX, objMosH ) - spr.X;
						inX = (float)clamp( mx, 0, rw - 1 );
					}
					else
					{
						int mx = rw - relX - 1;
						mx = rw - ( MosaicFloor( spr.X + mx, objMosH ) - spr.X ) - 1;
						inX = (float)clamp( mx, 0, rw - 1 );
					}
				}
				else
				{
					inX = relXf;
				}

				if ( objMosV > 1 )
				{
					int my = MosaicFloor( spr.Y + relY, objMosV ) - spr.Y;
					inY = (float)clamp( my, 0, rh - 1 );
				}
				else
				{
					inY = relYf;
				}
			}

			float fdx = inX - (float)rw * 0.5;
			float fdy = inY - (float)rh * 0.5;

			int localX = (int)( (float)spr.PA / 256.0 * fdx + (float)spr.PB / 256.0 * fdy + (float)spr.Width * 0.5 );
			int localY = (int)( (float)spr.PC / 256.0 * fdx + (float)spr.PD / 256.0 * fdy + (float)spr.Height * 0.5 );

			if ( ( localX & ~( spr.Width - 1 ) ) != 0 || ( localY & ~( spr.Height - 1 ) ) != 0 )
				continue;

			bool is8bpp = ( flags & FLAG_8BPP ) != 0u;

			uint palIdx;
			if ( is8bpp )
			{
				uint tileIdx = (uint)( ( ( localX >> 3 ) + (int)spr.Tile ) & 15 )
				             + (uint)( localY >> 3 ) * spr.Stride;
				uint addr = spr.CharBase * 2u + tileIdx * 64u
				          + (uint)( localY & 7 ) * 8u + (uint)( localX & 7 );
				if ( addr >= 0x18000u ) continue;
				palIdx = LoadVramByte( Vram, addr );
				if ( palIdx == 0u ) continue;
			}
			else
			{
				uint tileIdx = (uint)( ( ( localX >> 3 ) + (int)spr.Tile ) & 31 )
				             + (uint)( localY >> 3 ) * spr.Stride;
				uint addr = spr.CharBase * 2u + tileIdx * 32u
				          + (uint)( localY & 7 ) * 4u + (uint)( localX & 7 ) / 2u;
				if ( addr >= 0x18000u ) continue;
				uint data = LoadVramByte( Vram, addr );
				uint entry = ( data >> ( 4u * ( (uint)( localX & 7 ) & 1u ) ) ) & 0xFu;
				if ( entry == 0u ) continue;
				palIdx = spr.Palette * 16u + entry;
			}

			if ( ( flags & FLAG_OBJWINDOW ) != 0u )
			{
				if ( ( dispCnt & 0x8000u ) != 0u )
					WindowTex[id.xy] = ( winOut >> 8 ) & 0x3Fu;
				continue;
			}

			float4 color = LoadPaletteColor( Palette, 256u + palIdx, nativeY );

			if ( priority < bestPriority || ( priority == bestPriority && bestColor.a == 0.0 ) )
			{
				bestPriority = priority;
				bestColor = color;
				bestFlags = (uint)priority | ( ( flags & FLAG_SEMITRANS ) != 0u ? 4u : 0u );
			}
		}

		if ( bestColor.a > 0.0 )
		{
			OutputColor[id.xy] = bestColor;
			OutputFlags[id.xy] = bestFlags;
		}
	}
}
