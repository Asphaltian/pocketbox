HEADER
{
	DevShader = true;
	Description = "GBA BG Mode 2 Affine Background Renderer";
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
	RWTexture2D<float4> OutputTex < Attribute( "OutputTex" ); >;
	int BgIndex < Attribute( "BgIndex" ); >;
	int Scale < Attribute( "Scale" ); >;
	int IsBasePass < Attribute( "IsBasePass" ); >;
	float2 OldCharBase2 < Attribute( "OldCharBase2" ); >;
	float2 OldCharBase3 < Attribute( "OldCharBase3" ); >;

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint nativeY = id.y / (uint)Scale;
		uint nativeX = id.x / (uint)Scale;

		if ( nativeY >= 160u || nativeX >= 240u )
		{
			if ( IsBasePass != 0 ) OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		ScanlineState state = States[nativeY];
		uint dispCnt = GetDispCnt( state );

		uint mode = dispCnt & 7u;
		bool validMode = ( mode == 1u && (uint)BgIndex == 2u ) || ( mode == 2u && (uint)BgIndex >= 2u );
		if ( !validMode )
		{
			if ( IsBasePass != 0 ) OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		if ( ( state.EnabledAtYMask & ( 1u << (uint)BgIndex ) ) == 0u )
		{
			if ( IsBasePass != 0 ) OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		uint bgCnt = GetBgCnt( state, (uint)BgIndex );
		uint charBase = ( ( bgCnt >> 2u ) & 3u ) * 0x4000u;
		uint screenBase = ( ( bgCnt >> 8u ) & 0x1Fu ) * 0x800u;
		uint size = ( bgCnt >> 14u ) & 3u;
		bool overflow = ( bgCnt & 0x2000u ) != 0u;

		float inX = ( (float)id.x + 0.5 ) / (float)Scale;
		float inY = ( (float)id.y + 0.5 ) / (float)Scale;

		uint mosaicReg = GetMosaic( state );
		if ( ( bgCnt & 0x40u ) != 0u )
		{
			uint mosaicH = ( mosaicReg & 0xFu ) + 1u;
			uint mosaicV = ( ( mosaicReg >> 4u ) & 0xFu ) + 1u;
			if ( mosaicH > 1u ) inX = (float)MosaicFloor( (int)inX, (int)mosaicH );
			if ( mosaicV > 1u ) inY = (float)MosaicFloor( (int)inY, (int)mosaicV );
		}

		uint bg23 = (uint)BgIndex - 2u;
		int firstAffine = state.FirstAffine;
		if ( firstAffine < 0 ) firstAffine = (int)nativeY;

		int aStartY = max( firstAffine, (int)inY - 3 );
		int aY0 = max( aStartY, 0 );
		int aY1 = min( aY0 + 1, 159 );
		int aY2 = min( aY0 + 2, 159 );
		int aY3 = min( aY0 + 3, 159 );
		int2 am0, ao0, am1, ao1, am2, ao2, am3, ao3;
		ExtractAffineParams( States[aY0], bg23, am0, ao0 );
		ExtractAffineParams( States[aY1], bg23, am1, ao1 );
		ExtractAffineParams( States[aY2], bg23, am2, ao2 );
		ExtractAffineParams( States[aY3], bg23, am3, ao3 );

		int2 coord = AffineInterpolate( am0, am1, am2, am3, ao0, ao1, ao2, ao3, inX, inY, firstAffine );

		int sizeAdjusted = (int)( ( 0x8000u << size ) - 1u );
		if ( overflow )
		{
			coord &= sizeAdjusted;
		}
		else
		{
			int2 outerCoord = coord & ~sizeAdjusted;
			if ( ( outerCoord.x | outerCoord.y ) != 0 )
			{
				OutputTex[id.xy] = float4( 0, 0, 0, 0 );
				return;
			}
		}

		int map = ( coord.x >> 11 ) + ( ( ( coord.y >> 7 ) & 0x7F0 ) << (int)size );
		uint mapAddr = screenBase + (uint)map;
		uint tile = LoadVramByte( Vram, mapAddr );

		uint newCharBase = charBase;
		float2 oldCB = bg23 == 0u ? OldCharBase2 : OldCharBase3;
		if ( newCharBase != (uint)oldCB.x )
		{
			int sy = (int)nativeY;
			int ocbFirstY = (int)oldCB.y;
			if ( sy == ocbFirstY && nativeY > 0u )
			{
				ScanlineState prevState = States[nativeY - 1u];
				int prevBgY = bg23 == 0u ? prevState.Bg2Y : prevState.Bg3Y;
				if ( ( prevBgY >> 11 ) == ( coord.y >> 11 ) )
					newCharBase = (uint)oldCB.x;
			}
		}

		uint pixX = (uint)( ( coord.x >> 8 ) & 7 );
		uint pixY = (uint)( ( coord.y >> 8 ) & 7 );
		uint pixAddr = newCharBase + tile * 64u + pixY * 8u + pixX;

		if ( pixAddr >= 0x10000u )
		{
			OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		uint entry = LoadVramByte( Vram, pixAddr );
		if ( entry == 0u )
		{
			OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		OutputTex[id.xy] = LoadPaletteColor( Palette, entry, nativeY );
	}
}
