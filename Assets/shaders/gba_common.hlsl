#ifndef GBA_COMMON_HLSL
#define GBA_COMMON_HLSL

struct ScanlineState
{
	uint DispCntMosaic;
	uint BgCnt01;
	uint BgCnt23;
	uint BgOffset0;
	uint BgOffset1;
	uint BgOffset2;
	uint BgOffset3;
	int Bg2PA;
	int Bg2PC;
	int Bg2X;
	int Bg2Y;
	int Bg3PA;
	int Bg3PC;
	int Bg3X;
	int Bg3Y;
	uint BldCntAlpha;
	uint BldYWin0H;
	uint Win0VWin1H;
	uint Win1VWinIn;
	uint WinOutPad;
	int FirstAffine;
	uint EnabledAtYMask;
};

struct GpuSprite
{
	int X;
	int Y;
	int Width;
	int Height;
	int RenderWidth;
	int RenderHeight;
	uint CharBase;
	uint Tile;
	uint Stride;
	uint Palette;
	uint Flags;
	int PA;
	int PB;
	int PC;
	int PD;
	int Cycles;
};

uint UnpackLow16( uint packed )  { return packed & 0xFFFFu; }
uint UnpackHigh16( uint packed ) { return ( packed >> 16 ) & 0xFFFFu; }

int UnpackS16Low( uint packed )  { return ( (int)packed << 16 ) >> 16; }
int UnpackS16High( uint packed ) { return (int)packed >> 16; }

uint GetDispCnt( ScanlineState s ) { return s.DispCntMosaic & 0xFFFFu; }
uint GetMosaic( ScanlineState s )  { return ( s.DispCntMosaic >> 16 ) & 0xFFFFu; }

uint GetBgCnt( ScanlineState s, uint bg )
{
	uint pair = bg < 2u ? s.BgCnt01 : s.BgCnt23;
	return ( bg & 1u ) != 0u ? UnpackHigh16( pair ) : UnpackLow16( pair );
}

uint GetBgOffset( ScanlineState s, uint bg )
{
	if      ( bg == 0u ) return s.BgOffset0;
	else if ( bg == 1u ) return s.BgOffset1;
	else if ( bg == 2u ) return s.BgOffset2;
	else                 return s.BgOffset3;
}

float4 Rgb555ToFloat4( uint rgb555 )
{
	return float4(
		float( rgb555 & 0x1Fu ) / 31.0,
		float( ( rgb555 >> 5u ) & 0x1Fu ) / 31.0,
		float( ( rgb555 >> 10u ) & 0x1Fu ) / 31.0,
		1.0
	);
}

#define LoadVramByte(buf, byteAddr) \
	(((buf)[(byteAddr) >> 2u] >> (((byteAddr) & 3u) * 8u)) & 0xFFu)

#define LoadVramU16(buf, byteAddr) \
	(((buf)[(byteAddr) >> 2u] >> (((byteAddr) & 2u) * 8u)) & 0xFFFFu)

#define LoadPaletteColor(palBuf, entry, scanline) \
	Rgb555ToFloat4( LoadVramU16( palBuf, ((scanline) * 512u + (entry)) * 2u ) )

static const int mosaicTable[17] = {
	0, 4096, 2048, 1366, 1024, 820, 683, 586,
	512, 456, 410, 373, 342, 316, 293, 274, 256
};

int MosaicFloor( int val, int grid )
{
	return ( ( val * mosaicTable[grid] ) >> 12 ) * grid;
}

void ExtractAffineParams( ScanlineState s, uint bg23, out int2 mat, out int2 off )
{
	if ( bg23 == 0u )
	{
		mat = int2( s.Bg2PA, s.Bg2PC );
		off = int2( s.Bg2X, s.Bg2Y );
	}
	else
	{
		mat = int2( s.Bg3PA, s.Bg3PC );
		off = int2( s.Bg3X, s.Bg3Y );
	}
}

float2 CubicInterpolate( int2 arr0, int2 arr1, int2 arr2, int2 arr3, float x )
{
	float x1m = 1.0 - x;
	return x1m * x1m * x1m * float2( arr0 )
	     + 3.0 * x1m * x1m * x   * float2( arr1 )
	     + 3.0 * x1m * x   * x   * float2( arr2 )
	     +        x   * x   * x   * float2( arr3 );
}

int2 AffineInterpolate(
	int2 mat0, int2 mat1, int2 mat2, int2 mat3,
	int2 off0, int2 off1, int2 off2, int2 off3,
	float inX, float inY, int firstAffine
)
{
	float y = frac( inY );
	float start = 2.0 / 3.0;
	if ( (int)inY - firstAffine < 4 )
	{
		y = inY - (float)firstAffine;
		start -= 1.0;
	}
	float lin = start + y / 3.0;

	float2 mixedTransform = CubicInterpolate( mat0, mat1, mat2, mat3, lin );
	float2 mixedOffset    = CubicInterpolate( off0, off1, off2, off3, lin );

	return int2( mixedTransform * inX + mixedOffset );
}

#endif
