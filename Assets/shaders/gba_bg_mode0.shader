HEADER
{
	DevShader = true;
	Description = "GBA BG Mode 0 Text Background Renderer";
}

MODES
{
	Default();
}

FEATURES
{
}

COMMON
{
	#include "system.fxc"
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
		bool validMode = ( mode <= 1u ) && !( mode == 1u && (uint)BgIndex > 1u );
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
		bool is8bpp = ( bgCnt & 0x80u ) != 0u;
		uint size = ( bgCnt >> 14u ) & 3u;

		int2 coord = int2( (int)nativeX, (int)nativeY );

		uint mosaicReg = GetMosaic( state );
		if ( ( bgCnt & 0x40u ) != 0u )
		{
			uint mosaicH = ( mosaicReg & 0xFu ) + 1u;
			uint mosaicV = ( ( mosaicReg >> 4u ) & 0xFu ) + 1u;
			if ( mosaicH > 1u ) coord.x = MosaicFloor( coord.x, (int)mosaicH );
			if ( mosaicV > 1u ) coord.y = MosaicFloor( coord.y, (int)mosaicV );
		}

		uint bgOffset = GetBgOffset( state, (uint)BgIndex );
		coord += int2( (int)( bgOffset & 0x1FFu ), (int)( ( bgOffset >> 16u ) & 0x1FFu ) );

		int2 wrapMask = int2( 255, 255 );
		int doty = 0;
		if ( ( size & 1u ) == 1u ) { wrapMask.x = 511; doty++; }
		if ( ( size & 2u ) == 2u ) { wrapMask.y = 511; doty++; }
		coord &= wrapMask;
		int2 wrapBits = coord & 256;
		coord &= 255;
		coord.y += wrapBits.x + wrapBits.y * doty;

		uint mapAddr = screenBase + (uint)( ( coord.x >> 3 ) + ( coord.y >> 3 ) * 32 ) * 2u;
		uint mapEntry = LoadVramU16( Vram, mapAddr );

		int2 local = coord & 7;
		if ( ( mapEntry & 1024u ) != 0u ) local.x ^= 7;
		if ( ( mapEntry & 2048u ) != 0u ) local.y ^= 7;

		int tile = (int)( mapEntry & 1023u );
		int paletteId = (int)( ( mapEntry >> 12u ) & 15u );

		uint palIdx;
		if ( is8bpp )
		{
			uint addr = charBase + (uint)tile * 64u + (uint)local.y * 8u + (uint)local.x;
			if ( addr >= 0x10000u ) { OutputTex[id.xy] = float4( 0, 0, 0, 0 ); return; }
			palIdx = LoadVramByte( Vram, addr );
			if ( palIdx == 0u ) { OutputTex[id.xy] = float4( 0, 0, 0, 0 ); return; }
		}
		else
		{
			uint addr = charBase + (uint)tile * 32u + (uint)local.y * 4u + (uint)local.x / 2u;
			if ( addr >= 0x10000u ) { OutputTex[id.xy] = float4( 0, 0, 0, 0 ); return; }
			uint data = LoadVramByte( Vram, addr );
			uint entry = ( data >> ( 4u * ( (uint)local.x & 1u ) ) ) & 15u;
			if ( entry == 0u ) { OutputTex[id.xy] = float4( 0, 0, 0, 0 ); return; }
			palIdx = (uint)paletteId * 16u + entry;
		}

		OutputTex[id.xy] = LoadPaletteColor( Palette, palIdx, nativeY );
	}
}
