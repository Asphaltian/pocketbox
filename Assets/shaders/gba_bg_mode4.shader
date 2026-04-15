HEADER
{
	DevShader = true;
	Description = "GBA BG Mode 4 Bitmap 8-bit Paletted";
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
	int Scale < Attribute( "Scale" ); >;
	int IsBasePass < Attribute( "IsBasePass" ); >;


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
		if ( mode != 4u )
		{
			if ( IsBasePass != 0 ) OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		if ( ( state.EnabledAtYMask & 4u ) == 0u )
		{
			if ( IsBasePass != 0 ) OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		uint charBase = ( dispCnt & 0x10u ) != 0u ? 0xA000u : 0u;

		float inX = ( (float)id.x + 0.5 ) / (float)Scale;
		float inY = ( (float)id.y + 0.5 ) / (float)Scale;

		uint bgCnt = GetBgCnt( state, 2u );
		uint mosaicReg = GetMosaic( state );
		if ( ( bgCnt & 0x40u ) != 0u )
		{
			uint mosaicH = ( mosaicReg & 0xFu ) + 1u;
			uint mosaicV = ( ( mosaicReg >> 4u ) & 0xFu ) + 1u;
			if ( mosaicH > 1u ) inX = (float)MosaicFloor( (int)inX, (int)mosaicH );
			if ( mosaicV > 1u ) inY = (float)MosaicFloor( (int)inY, (int)mosaicV );
		}

		int firstAffine = state.FirstAffine;
		if ( firstAffine < 0 ) firstAffine = (int)nativeY;

		int aStartY = max( firstAffine, (int)inY - 3 );
		int aY0 = max( aStartY, 0 );
		int aY1 = min( aY0 + 1, 159 );
		int aY2 = min( aY0 + 2, 159 );
		int aY3 = min( aY0 + 3, 159 );
		ScanlineState as0 = States[aY0];
		ScanlineState as1 = States[aY1];
		ScanlineState as2 = States[aY2];
		ScanlineState as3 = States[aY3];
		int2 coord = AffineInterpolate(
			int2( as0.Bg2PA, as0.Bg2PC ), int2( as1.Bg2PA, as1.Bg2PC ),
			int2( as2.Bg2PA, as2.Bg2PC ), int2( as3.Bg2PA, as3.Bg2PC ),
			int2( as0.Bg2X, as0.Bg2Y ), int2( as1.Bg2X, as1.Bg2Y ),
			int2( as2.Bg2X, as2.Bg2Y ), int2( as3.Bg2X, as3.Bg2Y ),
			inX, inY, firstAffine );

		if ( coord.x < 0 || coord.x >= ( 240 << 8 ) || coord.y < 0 || coord.y >= ( 160 << 8 ) )
		{
			OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		uint addr = charBase + (uint)( coord.x >> 8 ) + (uint)( coord.y >> 8 ) * 240u;
		uint entry = LoadVramByte( Vram, addr );

		if ( entry == 0u )
		{
			OutputTex[id.xy] = float4( 0, 0, 0, 0 );
			return;
		}

		OutputTex[id.xy] = LoadPaletteColor( Palette, entry, nativeY );
	}
}
