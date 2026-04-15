HEADER
{
	DevShader = true;
	Description = "GBA Finalize Layer Compositing and Blending";
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
	StructuredBuffer<uint> Palette < Attribute( "Palette" ); >;

	Texture2D<float4> Bg0Tex < Attribute( "Bg0Tex" ); >;
	Texture2D<float4> Bg1Tex < Attribute( "Bg1Tex" ); >;
	Texture2D<float4> Bg2Tex < Attribute( "Bg2Tex" ); >;
	Texture2D<float4> Bg3Tex < Attribute( "Bg3Tex" ); >;
	Texture2D<float4> ObjColorTex < Attribute( "ObjColorTex" ); >;
	Texture2D<uint>   ObjFlagsTex < Attribute( "ObjFlagsTex" ); >;
	Texture2D<uint>   WindowTex < Attribute( "WindowTex" ); >;

	RWTexture2D<float4> OutputTex < Attribute( "OutputTex" ); >;
	int Scale < Attribute( "Scale" ); >;

	void Composite(
		float4 pixel, int4 flags,
		inout float4 topPixel, inout int4 topFlags,
		inout float4 bottomPixel, inout int4 bottomFlags )
	{
		if ( flags.x >= topFlags.x )
		{
			if ( flags.x >= bottomFlags.x )
				return;
			bottomFlags = flags;
			bottomPixel = pixel;
		}
		else
		{
			bottomFlags = topFlags;
			topFlags = flags;
			bottomPixel = topPixel;
			topPixel = pixel;
		}
	}

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint nativeY = id.y / (uint)Scale;
		uint nativeX = id.x / (uint)Scale;

		if ( nativeY >= 160 || nativeX >= 240 )
		{
			OutputTex[id.xy] = float4( 0, 0, 0, 1 );
			return;
		}

		ScanlineState state = States[nativeY];
		uint dispCnt = GetDispCnt( state );

		if ( ( dispCnt & 0x80u ) != 0u )
		{
			OutputTex[id.xy] = float4( 1, 1, 1, 1 );
			return;
		}

		uint bldCnt = UnpackLow16( state.BldCntAlpha );
		uint bldAlpha = UnpackHigh16( state.BldCntAlpha );
		uint bldY = UnpackLow16( state.BldYWin0H );

		int iBlendEffect = (int)( ( bldCnt >> 6 ) & 3u );
		int eva = (int)min( bldAlpha & 0x1Fu, 16u );
		int evb = (int)min( ( bldAlpha >> 8 ) & 0x1Fu, 16u );
		int evy = (int)min( bldY & 0x1Fu, 16u );

		int bdT1 = (int)( ( bldCnt >> 5 ) & 1u );
		int bdT2 = (int)( ( bldCnt >> 13 ) & 1u );
		int4 backdropFlags = int4( 32, bdT1 | ( bdT2 << 1 ) | ( iBlendEffect << 2 ), eva, 0 );

		float4 backdrop = LoadPaletteColor( Palette, 0, nativeY );
		float4 topPixel = backdrop;
		int4 topFlags = backdropFlags;
		float4 bottomPixel = backdrop;
		int4 bottomFlags = backdropFlags;

		uint windowMask = WindowTex.Load( int3( id.xy, 0 ) );
		int3 loadCoord = int3( id.xy, 0 );

		if ( ( windowMask & 16u ) != 0 )
		{
			float4 objPix = ObjColorTex.Load( loadCoord );
			if ( objPix.a > 0.0 )
			{
				uint objRaw = ObjFlagsTex.Load( loadCoord );
				int objPrio = (int)( objRaw & 3u );
				int semiTrans = (int)( ( objRaw >> 2 ) & 1u );
				int objT1 = (int)( ( bldCnt >> 4 ) & 1u ) | semiTrans;
				int objT2 = (int)( ( bldCnt >> 12 ) & 1u );
				int4 objFlags = int4( objPrio, objT1 | ( objT2 << 1 ) | ( iBlendEffect << 2 ), eva, semiTrans );
				Composite( objPix, objFlags, topPixel, topFlags, bottomPixel, bottomFlags );
			}
		}

		// BG0
		if ( ( windowMask & 1u ) != 0 && ( dispCnt & 0x100u ) != 0 )
		{
			float4 pix = Bg0Tex.Load( loadCoord );
			if ( pix.a > 0.0 )
			{
				int prio = (int)( UnpackLow16( state.BgCnt01 ) & 3u );
				int t1 = (int)( bldCnt & 1u );
				int t2 = (int)( ( bldCnt >> 8 ) & 1u );
				Composite( pix, int4( prio, t1 | ( t2 << 1 ) | ( iBlendEffect << 2 ), eva, 0 ), topPixel, topFlags, bottomPixel, bottomFlags );
			}
		}

		// BG1
		if ( ( windowMask & 2u ) != 0 && ( dispCnt & 0x200u ) != 0 )
		{
			float4 pix = Bg1Tex.Load( loadCoord );
			if ( pix.a > 0.0 )
			{
				int prio = (int)( UnpackHigh16( state.BgCnt01 ) & 3u );
				int t1 = (int)( ( bldCnt >> 1 ) & 1u );
				int t2 = (int)( ( bldCnt >> 9 ) & 1u );
				Composite( pix, int4( prio, t1 | ( t2 << 1 ) | ( iBlendEffect << 2 ), eva, 0 ), topPixel, topFlags, bottomPixel, bottomFlags );
			}
		}

		// BG2
		if ( ( windowMask & 4u ) != 0 && ( dispCnt & 0x400u ) != 0 )
		{
			float4 pix = Bg2Tex.Load( loadCoord );
			if ( pix.a > 0.0 )
			{
				int prio = (int)( UnpackLow16( state.BgCnt23 ) & 3u );
				int t1 = (int)( ( bldCnt >> 2 ) & 1u );
				int t2 = (int)( ( bldCnt >> 10 ) & 1u );
				Composite( pix, int4( prio, t1 | ( t2 << 1 ) | ( iBlendEffect << 2 ), eva, 0 ), topPixel, topFlags, bottomPixel, bottomFlags );
			}
		}

		// BG3
		if ( ( windowMask & 8u ) != 0 && ( dispCnt & 0x800u ) != 0 )
		{
			float4 pix = Bg3Tex.Load( loadCoord );
			if ( pix.a > 0.0 )
			{
				int prio = (int)( UnpackHigh16( state.BgCnt23 ) & 3u );
				int t1 = (int)( ( bldCnt >> 3 ) & 1u );
				int t2 = (int)( ( bldCnt >> 11 ) & 1u );
				Composite( pix, int4( prio, t1 | ( t2 << 1 ) | ( iBlendEffect << 2 ), eva, 0 ), topPixel, topFlags, bottomPixel, bottomFlags );
			}
		}

		if ( ( windowMask & 32u ) == 0 )
			topFlags.y &= ~1;

		if ( ( ( topFlags.y & 13 ) == 5 || topFlags.w > 0 ) && ( bottomFlags.y & 2 ) == 2 )
		{
			float a = (float)topFlags.z / 16.0;
			float b = (float)evb / 16.0;
			topPixel.rgb = min( topPixel.rgb * a + bottomPixel.rgb * b, float3( 1, 1, 1 ) );
		}
		else if ( topFlags.w == 0 )
		{
			if ( ( topFlags.y & 13 ) == 9 )
			{
				float y = (float)evy / 16.0;
				topPixel.rgb += ( 1.0 - topPixel.rgb ) * y;
			}
			else if ( ( topFlags.y & 13 ) == 13 )
			{
				float y = (float)evy / 16.0;
				topPixel.rgb -= topPixel.rgb * y;
			}
		}

		OutputTex[id.xy] = float4( topPixel.rgb, 1.0 );
	}
}
