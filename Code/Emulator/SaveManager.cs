using System.IO;
using System.Text;

namespace sGBA;

public class SaveManager
{
	public GbaSystem Gba { get; }
	public SaveType Type { get; private set; }
	public byte[] Data { get; set; }

	private FlashState _flashState;
	private int _flashBank;
	private bool _flashIdMode;

	private byte FlashManufacturerId;
	private byte FlashDeviceId;

	private EepromCommand _eepromCommand;
	private int _eepromBitsRemaining;
	private uint _eepromReadAddress;
	private uint _eepromWriteAddress;
	private bool _eepromSettling;
	private int _eepromSettleCycles;
	private const int EepromSettleCycles = 115000;

	public bool IsDirty { get; private set; }
	private int _dirtyFrameCount;
	private const int SaveDelayFrames = 30;

	public SaveManager( GbaSystem gba )
	{
		Gba = gba;
		Type = SaveType.None;
		Data = Array.Empty<byte>();
	}

	public void Reset()
	{
		_flashState = FlashState.Ready;
		_flashBank = 0;
		_flashIdMode = false;
		_eepromCommand = EepromCommand.Null;
		_eepromBitsRemaining = 0;
		_eepromReadAddress = 0;
		_eepromWriteAddress = 0;
		_eepromSettling = false;
		_eepromSettleCycles = 0;
		IsDirty = false;
		_dirtyFrameCount = 0;
	}

	public void DetectSaveType( byte[] rom )
	{
		string romText = Encoding.ASCII.GetString( rom );

		if ( romText.Contains( "FLASH1M_V" ) )
		{
			Type = SaveType.Flash128K;
			Data = new byte[GbaConstants.FlashSizeLarge];
			FlashManufacturerId = 0x62;
			FlashDeviceId = 0x13;
		}
		else if ( romText.Contains( "FLASH_V" ) || romText.Contains( "FLASH512_V" ) )
		{
			Type = SaveType.Flash64K;
			Data = new byte[GbaConstants.FlashSize];
			FlashManufacturerId = 0x32;
			FlashDeviceId = 0x1B;
		}
		else if ( romText.Contains( "SRAM_V" ) || romText.Contains( "SRAM_F_V" ) )
		{
			Type = SaveType.Sram;
			Data = new byte[GbaConstants.SramSize];
		}
		else if ( romText.Contains( "EEPROM_V" ) )
		{
			Type = SaveType.Eeprom;
			Data = new byte[8192];
		}
		else
		{
			Type = SaveType.None;
			Data = [];
		}

		if ( Type == SaveType.Flash64K || Type == SaveType.Flash128K || Type == SaveType.Eeprom )
		{
			Array.Fill( Data, (byte)0xFF );
		}
	}

	public void LoadSaveData( byte[] saveData )
	{
		if ( saveData == null || saveData.Length == 0 || Data.Length == 0 )
			return;

		int copyLen = Math.Min( saveData.Length, Data.Length );
		Array.Copy( saveData, Data, copyLen );
	}

	public byte Read8( uint address )
	{
		uint offset = address & 0xFFFF;

		switch ( Type )
		{
			case SaveType.Sram:
				return Data[offset & (uint)(Data.Length - 1)];

			case SaveType.Flash64K:
			case SaveType.Flash128K:
				return ReadFlash( offset );

			case SaveType.Eeprom:
				return 0;
		}

		return 0;
	}

	public void Write8( uint address, byte value )
	{
		uint offset = address & 0xFFFF;

		switch ( Type )
		{
			case SaveType.Sram:
				Data[offset & (uint)(Data.Length - 1)] = value;
				MarkDirty();
				break;

			case SaveType.Flash64K:
			case SaveType.Flash128K:
				WriteFlash( offset, value );
				break;
		}
	}

	public void TickEepromSettle( int cycles )
	{
		if ( !_eepromSettling ) return;
		_eepromSettleCycles -= cycles;
		if ( _eepromSettleCycles <= 0 )
			_eepromSettling = false;
	}

	public ushort ReadEeprom()
	{
		if ( _eepromCommand != EepromCommand.Read )
			return (ushort)(_eepromSettling ? 0 : 1);

		_eepromBitsRemaining--;
		if ( _eepromBitsRemaining < 64 )
		{
			int step = 63 - _eepromBitsRemaining;
			uint address = (_eepromReadAddress + (uint)step) >> 3;
			if ( address < (uint)Data.Length )
			{
				byte data = Data[address];
				int bit = (data >> (0x7 - (step & 0x7))) & 1;
				if ( _eepromBitsRemaining == 0 )
					_eepromCommand = EepromCommand.Null;
				return (ushort)bit;
			}
			if ( _eepromBitsRemaining == 0 )
				_eepromCommand = EepromCommand.Null;
			return 1;
		}
		return 0;
	}

	public void WriteEeprom( ushort value, int dmaCount )
	{
		switch ( _eepromCommand )
		{
			case EepromCommand.Null:
			default:
				_eepromCommand = (EepromCommand)(value & 1);
				break;

			case EepromCommand.Pending:
				_eepromCommand = (EepromCommand)(((int)_eepromCommand << 1) | (value & 1));
				if ( _eepromCommand == EepromCommand.Write )
					_eepromWriteAddress = 0;
				else
					_eepromReadAddress = 0;
				break;

			case EepromCommand.Write:
				if ( dmaCount > 65 )
				{
					_eepromWriteAddress <<= 1;
					_eepromWriteAddress |= (uint)(value & 1) << 6;
				}
				else if ( dmaCount == 1 )
				{
					_eepromCommand = EepromCommand.Null;
				}
				else if ( (_eepromWriteAddress >> 3) < (uint)Data.Length )
				{
					uint addr = _eepromWriteAddress >> 3;
					byte current = Data[addr];
					int bitPos = 0x7 - (int)(_eepromWriteAddress & 0x7);
					current &= (byte)~(1 << bitPos);
					current |= (byte)((value & 1) << bitPos);
					Data[addr] = current;
					_eepromSettling = true;
					_eepromSettleCycles = EepromSettleCycles;
					_eepromWriteAddress++;
					MarkDirty();
				}
				break;

			case EepromCommand.ReadPending:
				if ( dmaCount > 1 )
				{
					_eepromReadAddress <<= 1;
					if ( (value & 1) != 0 )
						_eepromReadAddress |= 0x40;
				}
				else
				{
					_eepromBitsRemaining = 68;
					_eepromCommand = EepromCommand.Read;
				}
				break;
		}
	}

	public bool TickFrame()
	{
		if ( !IsDirty ) return false;

		_dirtyFrameCount++;
		if ( _dirtyFrameCount >= SaveDelayFrames )
		{
			IsDirty = false;
			_dirtyFrameCount = 0;
			return true;
		}

		return false;
	}

	private void MarkDirty()
	{
		IsDirty = true;
		_dirtyFrameCount = 0;
	}

	private byte ReadFlash( uint offset )
	{
		if ( _flashIdMode )
		{
			if ( offset == 0 ) return FlashManufacturerId;
			if ( offset == 1 ) return FlashDeviceId;
			return 0;
		}

		uint realOffset = (uint)(_flashBank * 0x10000) + offset;
		if ( realOffset < (uint)Data.Length )
			return Data[realOffset];

		return 0xFF;
	}

	private void WriteFlash( uint offset, byte value )
	{
		switch ( _flashState )
		{
			case FlashState.Ready:
				if ( offset == 0x5555 && value == 0xAA )
					_flashState = FlashState.Cmd1;
				break;

			case FlashState.Cmd1:
				if ( offset == 0x2AAA && value == 0x55 )
					_flashState = FlashState.Cmd2;
				else
					_flashState = FlashState.Ready;
				break;

			case FlashState.Cmd2:
				if ( offset == 0x5555 )
				{
					ExecuteFlashCommand( value );
				}
				else
				{
					_flashState = FlashState.Ready;
				}
				break;

			case FlashState.PrepareWrite:
				{
					uint realOffset = (uint)(_flashBank * 0x10000) + offset;
					if ( realOffset < (uint)Data.Length )
					{
						Data[realOffset] &= value;
						MarkDirty();
					}
				}
				_flashState = FlashState.Ready;
				break;

			case FlashState.PrepareErase:
				if ( offset == 0x5555 && value == 0xAA )
					_flashState = FlashState.Erase1;
				else
					_flashState = FlashState.Ready;
				break;

			case FlashState.Erase1:
				if ( offset == 0x2AAA && value == 0x55 )
					_flashState = FlashState.Erase2;
				else
					_flashState = FlashState.Ready;
				break;

			case FlashState.Erase2:
				if ( offset == 0x5555 && value == 0x10 )
				{
					Array.Fill( Data, (byte)0xFF );
					MarkDirty();
				}
				else if ( value == 0x30 )
				{
					uint sectorBase = (uint)(_flashBank * 0x10000) + (offset & 0xF000);
					if ( sectorBase + 0x1000 <= (uint)Data.Length )
					{
						Array.Fill( Data, (byte)0xFF, (int)sectorBase, 0x1000 );
						MarkDirty();
					}
				}
				_flashState = FlashState.Ready;
				break;

			case FlashState.BankSwitch:
				if ( offset == 0 )
				{
					_flashBank = value & 1;
				}
				_flashState = FlashState.Ready;
				break;
		}
	}

	private void ExecuteFlashCommand( byte cmd )
	{
		switch ( cmd )
		{
			case 0x90:
				_flashIdMode = true;
				_flashState = FlashState.Ready;
				break;

			case 0xF0:
				_flashIdMode = false;
				_flashState = FlashState.Ready;
				break;

			case 0xA0:
				_flashState = FlashState.PrepareWrite;
				break;

			case 0x80:
				_flashState = FlashState.PrepareErase;
				break;

			case 0xB0:
				if ( Type == SaveType.Flash128K )
					_flashState = FlashState.BankSwitch;
				else
					_flashState = FlashState.Ready;
				break;

			default:
				_flashState = FlashState.Ready;
				break;
		}
	}

	public void SerializeState( BinaryWriter w )
	{
		w.Write( (int)_flashState );
		w.Write( _flashBank );
		w.Write( _flashIdMode );
		w.Write( FlashManufacturerId );
		w.Write( FlashDeviceId );
		w.Write( (int)_eepromCommand );
		w.Write( _eepromBitsRemaining );
		w.Write( _eepromReadAddress );
		w.Write( _eepromWriteAddress );
		w.Write( _eepromSettling );
		w.Write( _eepromSettleCycles );
		w.Write( IsDirty );
		w.Write( _dirtyFrameCount );
	}

	public void DeserializeState( BinaryReader r )
	{
		_flashState = (FlashState)r.ReadInt32();
		_flashBank = r.ReadInt32();
		_flashIdMode = r.ReadBoolean();
		FlashManufacturerId = r.ReadByte();
		FlashDeviceId = r.ReadByte();
		_eepromCommand = (EepromCommand)r.ReadInt32();
		_eepromBitsRemaining = r.ReadInt32();
		_eepromReadAddress = r.ReadUInt32();
		_eepromWriteAddress = r.ReadUInt32();
		_eepromSettling = r.ReadBoolean();
		_eepromSettleCycles = r.ReadInt32();
		IsDirty = r.ReadBoolean();
		_dirtyFrameCount = r.ReadInt32();
	}
}

public enum SaveType
{
	None,
	Sram,
	Flash64K,
	Flash128K,
	Eeprom,
}

internal enum EepromCommand
{
	Null = 0,
	Pending = 1,
	Write = 2,
	ReadPending = 3,
	Read = 4,
}

internal enum FlashState
{
	Ready,
	Cmd1,
	Cmd2,
	PrepareWrite,
	PrepareErase,
	Erase1,
	Erase2,
	BankSwitch,
}
