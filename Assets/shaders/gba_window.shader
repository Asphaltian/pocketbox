HEADER
{
	DevShader = true;
	Description = "GBA Window Mask Shader";
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
	Texture2D<uint> ObjWindowMask < Attribute( "ObjWindowMask" ); >;
	RWTexture2D<uint> OutputMask < Attribute( "OutputMask" ); >;
	int Scale < Attribute( "Scale" ); >;
	float3 Circle0 < Attribute( "Circle0" ); Default3( 0, 0, 0 ); >;
	float3 Circle1 < Attribute( "Circle1" ); Default3( 0, 0, 0 ); >;

	static float nativeYFloatGlobal;
	static float nativeXFloatGlobal;

	float4 WindowInterpolate( float4 top, float4 bottom )
	{
		if ( distance( top, bottom ) > 40.0 )
			return top;

		float f = frac( nativeYFloatGlobal );
		float2 xy = lerp( bottom.xy, top.xy, f );
		return float4( xy, top.zw );
	}

	bool WindowCrop( float4 windowParams )
	{		bool4 compare = bool4(
			nativeXFloatGlobal < windowParams.x,
			nativeXFloatGlobal < windowParams.y,
			nativeYFloatGlobal < windowParams.z,
			nativeYFloatGlobal < windowParams.w
		);
		bool4 outside = bool4( compare.x, !compare.y, compare.z, !compare.w );
		if ( any( outside ) )
		{
			float2 h = windowParams.xy;
			float2 v = windowParams.zw;
			if ( v.x > v.y )
			{
				if ( outside.z && outside.w ) return false;
			}
			else if ( outside.z || outside.w )
			{
				return false;
			}
			if ( h.x > h.y )
			{
				if ( outside.x && outside.y ) return false;
			}
			else if ( outside.x || outside.y )
			{
				return false;
			}
		}
		return true;
	}

	bool WindowTest( float3 circle, float4 top, float4 bottom )
	{
		if ( circle.z > 0.0 )
			return distance( circle.xy, float2( nativeXFloatGlobal, nativeYFloatGlobal ) ) <= circle.z;
		if ( Scale < 2 )
			return WindowCrop( top );
		return WindowCrop( WindowInterpolate( top, bottom ) );
	}

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		float nativeYFloat = ( (float)id.y + 0.5 ) / (float)Scale;
		float nativeXFloat = ( (float)id.x + 0.5 ) / (float)Scale;
		nativeYFloatGlobal = nativeYFloat;
		nativeXFloatGlobal = nativeXFloat;
		uint nativeY = (uint)floor( nativeYFloat );
		uint nativeX = (uint)floor( nativeXFloat );

		if ( nativeY >= 160 || nativeX >= 240 )
		{
			OutputMask[id.xy] = 0x3Fu;
			return;
		}

		ScanlineState state = States[nativeY];
		uint dispCnt = GetDispCnt( state );

		bool win0Enable = ( dispCnt & 0x2000u ) != 0;
		bool win1Enable = ( dispCnt & 0x4000u ) != 0;
		bool objWinEnable = ( dispCnt & 0x8000u ) != 0;
		bool anyWindowEnabled = win0Enable || win1Enable || objWinEnable;

		if ( !anyWindowEnabled )
		{
			OutputMask[id.xy] = 0x3Fu;
			return;
		}

		uint win0H = UnpackHigh16( state.BldYWin0H );
		uint win0V = UnpackLow16( state.Win0VWin1H );
		uint win1H = UnpackHigh16( state.Win0VWin1H );
		uint win1V = UnpackLow16( state.Win1VWinIn );
		uint winIn = UnpackHigh16( state.Win1VWinIn );
		uint winOut = UnpackLow16( state.WinOutPad );

		float4 win0Top = float4(
			(float)( ( win0H >> 8 ) & 0xFFu ), (float)( win0H & 0xFFu ),
			(float)( ( win0V >> 8 ) & 0xFFu ), (float)( win0V & 0xFFu )
		);
		float4 win1Top = float4(
			(float)( ( win1H >> 8 ) & 0xFFu ), (float)( win1H & 0xFFu ),
			(float)( ( win1V >> 8 ) & 0xFFu ), (float)( win1V & 0xFFu )
		);

		float4 win0Bottom = win0Top;
		float4 win1Bottom = win1Top;
		if ( nativeY > 0u )
		{
			ScanlineState prevState = States[nativeY - 1u];
			uint pWin0H = UnpackHigh16( prevState.BldYWin0H );
			uint pWin0V = UnpackLow16( prevState.Win0VWin1H );
			uint pWin1H = UnpackHigh16( prevState.Win0VWin1H );
			uint pWin1V = UnpackLow16( prevState.Win1VWinIn );
			win0Bottom = float4(
				(float)( ( pWin0H >> 8 ) & 0xFFu ), (float)( pWin0H & 0xFFu ),
				(float)( ( pWin0V >> 8 ) & 0xFFu ), (float)( pWin0V & 0xFFu )
			);
			win1Bottom = float4(
				(float)( ( pWin1H >> 8 ) & 0xFFu ), (float)( pWin1H & 0xFFu ),
				(float)( ( pWin1V >> 8 ) & 0xFFu ), (float)( pWin1V & 0xFFu )
			);
		}

		bool inWin0 = false;
		if ( win0Enable )
			inWin0 = WindowTest( Circle0, win0Top, win0Bottom );

		bool inWin1 = false;
		if ( win1Enable )
			inWin1 = WindowTest( Circle1, win1Top, win1Bottom );

		uint mask;
		if ( inWin0 )
		{
			mask = winIn & 0x3Fu;
		}
		else if ( inWin1 )
		{
			mask = ( winIn >> 8 ) & 0x3Fu;
		}
		else if ( objWinEnable && ObjWindowMask.Load( int3( id.xy, 0 ) ) != 0 )
		{
			mask = ( winOut >> 8 ) & 0x3Fu;
		}
		else
		{
			mask = winOut & 0x3Fu;
		}

		OutputMask[id.xy] = mask;
	}
}
