namespace sGBA;

public static class GbaConstants
{
	public const int Arm7Clock = 16777216;

	public const int ScreenWidth = 240;
	public const int ScreenHeight = 160;

	public const int HDrawCycles = 1008;
	public const int HBlankCycles = 224;
	public const int ScanlineCycles = 1232;
	public const int VisibleLines = 160;
	public const int VBlankLines = 68;
	public const int TotalLines = 228;
	public const int FrameCycles = 280896;

	public const int BiosSize = 0x4000;
	public const int EwramSize = 0x40000;
	public const int IwramSize = 0x8000;
	public const int IoSize = 0x400;
	public const int PaletteSize = 0x400;
	public const int VramSize = 0x18000;
	public const int OamSize = 0x400;
	public const int RomMaxSize = 0x2000000;
	public const int SramSize = 0x8000;
	public const int FlashSize = 0x10000;
	public const int FlashSizeLarge = 0x20000;

	public const uint BiosBase = 0x00000000;
	public const uint EwramBase = 0x02000000;
	public const uint IwramBase = 0x03000000;
	public const uint IoBase = 0x04000000;
	public const uint PaletteBase = 0x05000000;
	public const uint VramBase = 0x06000000;
	public const uint OamBase = 0x07000000;
	public const uint RomBase = 0x08000000;
	public const uint RomWs1Base = 0x0A000000;
	public const uint RomWs2Base = 0x0C000000;
	public const uint SramBase = 0x0E000000;

	public const uint SpSys = 0x03007F00;
	public const uint SpIrq = 0x03007FA0;
	public const uint SpSvc = 0x03007FE0;

	public const uint IrqHandlerAddr = 0x03FFFFFC;
	public const uint IrqHandlerAddrAlt = 0x03007FFC;

	public const uint VectorReset = 0x00000000;
	public const uint VectorUndef = 0x00000004;
	public const uint VectorSwi = 0x00000008;
	public const uint VectorPAbort = 0x0000000C;
	public const uint VectorDAbort = 0x00000010;
	public const uint VectorIrq = 0x00000018;
	public const uint VectorFiq = 0x0000001C;

	public const double Fps = (double)Arm7Clock / FrameCycles;
}
