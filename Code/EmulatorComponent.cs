using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Sandbox.Rendering;

namespace sGBA;

public sealed partial class EmulatorComponent : Component
{
	[Property, Title( "ROM Path" ), FilePath( Extension = "gba" )]
	public string RomPath { get; set; }

	public static EmulatorComponent Current { get; private set; }
	public Gba Core { get; private set; }
	public Texture ScreenTexture { get; private set; }
	public bool IsReady { get; private set; }
	public string ErrorMessage { get; private set; }

	private SoundStream _audioStream;
	private SoundHandle _soundHandle;
	private string _savePath;
	private CameraComponent _camera;

	private CancellationTokenSource _cts;
	private Channel<FramePacket> _frameChannel;
	private SemaphoreSlim _frameSemaphore;
	private int _inputKeys = 0x03FF;
	private short[][] _audBufs;
	private int _workerBufIdx;
	private double _frameDebt;

	private bool _paused;
	private int _inputCooldown;
	private string _stateBasePath;

	private readonly struct FramePacket( short[] a, int ac, byte[] s )
	{
		public readonly short[] Audio = a;
		public readonly int AudioSamples = ac;
		public readonly byte[] SaveData = s;
	}

	protected override void OnStart()
	{
		Current = this;
		GbaLog.SetBackend( LogBackend );
		_camera = Scene.Camera;
		if ( !string.IsNullOrEmpty( RomPath ) )
			InitCore();
	}

	public void Restart( string romPath )
	{
		TearDownCore();
		RomPath = romPath;
		IsReady = false;
		ErrorMessage = null;
		InitCore();
	}

	public void Unload()
	{
		TearDownCore();
		RomPath = null;
	}

	private void TearDownCore()
	{
		_cts?.Cancel();
		_cts = null;

		if ( Core?.Savedata != null && Core.Savedata.Data.Length > 0 && _savePath != null )
			FileSystem.Data.WriteAllBytes( _savePath, Core.Savedata.Data );

		if ( _soundHandle is { IsValid: true } )
			_soundHandle.Volume = 0;
		_audioStream?.Dispose();
		_audioStream = null;

		if ( _camera.IsValid() && Core?.Video?.RenderCommandList != null )
			_camera.RemoveCommandList( Core.Video.RenderCommandList );

		Core?.Video?.DisposeGpu();
		Core = null;
		ScreenTexture = null;
		_frameChannel = null;
		_frameSemaphore?.Dispose();
		_frameSemaphore = null;
		_frameDebt = 0;
		_workerBufIdx = 0;
		_inputCooldown = 0;
		_paused = false;
		_inputKeys = 0x03FF;
		IsReady = false;
	}

	private void InitCore()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( RomPath ) && !FileSystem.Data.FileExists( RomPath ) )
			{
				ErrorMessage = $"ROM not found: {RomPath}";
				GbaLog.Write( LogCategory.GBA, LogLevel.Error, ErrorMessage );
				return;
			}

			var romFs = FileSystem.Mounted.FileExists( RomPath ) ? FileSystem.Mounted : FileSystem.Data;
			var romData = romFs.ReadAllBytes( RomPath ).ToArray();
			if ( romData.Length < 192 )
			{
				ErrorMessage = "ROM file is too small to be a valid GBA ROM.";
				GbaLog.Write( LogCategory.GBA, LogLevel.Error, ErrorMessage );
				return;
			}

			Core = new Gba();
			Core.LoadRom( romData );

			_savePath = "saves/" + System.IO.Path.GetFileNameWithoutExtension( RomPath ) + ".sav";
			if ( FileSystem.Data.FileExists( _savePath ) )
			{
				var saveData = FileSystem.Data.ReadAllBytes( _savePath ).ToArray();
				Core.Savedata.Load( saveData );
			}

			Core.Reset();
			IsReady = true;

			Core.Video.InitGpu( scale: ComputeAutoScale() );
			ScreenTexture = Core.Video.OutputTexture;

			if ( _camera.IsValid() && Core.Video.RenderCommandList != null )
				_camera.AddCommandList( Core.Video.RenderCommandList, Stage.AfterOpaque, 0 );

			_stateBasePath = "states/" + System.IO.Path.GetFileNameWithoutExtension( RomPath );

			try { InitAudioStream(); }
			catch ( Exception audioEx ) { GbaLog.Write( LogCategory.GBAAudio, LogLevel.Warn, $"Audio init failed: {audioEx.Message}" ); }

			int audSize = GbaAudio.SamplesPerFrame * 2;
			_audBufs = new short[4][];
			for ( int i = 0; i < 4; i++ )
				_audBufs[i] = new short[audSize];

			_frameChannel = Channel.CreateBounded<FramePacket>( 2 );
			_frameSemaphore = new SemaphoreSlim( 0, 4 );
			_cts = new CancellationTokenSource();
			GameTask.RunInThreadAsync( EmulationLoop );
		}
		catch ( Exception ex )
		{
			ErrorMessage = $"Failed to load ROM: {ex.Message}";
			GbaLog.Write( LogCategory.GBA, LogLevel.Fatal, ErrorMessage );
		}
	}

	private const int AudioHighWatermark = 6600;
	private const int AudioPrefillFrames = 8;

	private void InitAudioStream()
	{
		if ( _audioStream != null )
		{
			if ( _soundHandle is { IsValid: true } )
				_soundHandle.Volume = 0;
			_audioStream.Dispose();
			_audioStream = null;
		}

		_audioStream = new SoundStream( GbaAudio.SampleRate, 2 );
		_audioStream.WriteData( new short[GbaAudio.SamplesPerFrame * 2 * AudioPrefillFrames] );
		_soundHandle = _audioStream.Play( volume: 1.0f );
		_soundHandle.SpacialBlend = 0f;
		_soundHandle.Occlusion = false;
		_soundHandle.DistanceAttenuation = false;
		_soundHandle.AirAbsorption = false;
		_soundHandle.Transmission = false;
		_soundHandle.Stop( float.MaxValue );
	}

	private static int ComputeAutoScale()
	{
		int sw = Screen.Width > 0 ? (int)Screen.Width : 1920;
		int sh = Screen.Height > 0 ? (int)Screen.Height : 1080;
		return Math.Clamp( Math.Min( sw / 240, sh / 160 ), 1, 8 );
	}

	private async Task EmulationLoop()
	{
		var token = _cts.Token;
		try
		{
			while ( !token.IsCancellationRequested )
			{
				await _frameSemaphore.WaitAsync( token );

				var core = Core;
				if ( core == null ) break;

				core.Io.KeyInput = (ushort)Interlocked.CompareExchange( ref _inputKeys, 0, 0 );

				core.RunFrame();

				if ( token.IsCancellationRequested ) break;

				int idx = _workerBufIdx;
				_workerBufIdx = (idx + 1) & 3;
				var aud = _audBufs[idx];

				int sampleCount = core.Audio.SamplesWritten;
				if ( sampleCount > 0 )
					Buffer.BlockCopy( core.Audio.OutputBuffer, 0, aud, 0, sampleCount * 2 * sizeof( short ) );

				byte[] saveData = null;
				if ( core.Savedata.Clean() && core.Savedata.Data.Length > 0 )
					saveData = core.Savedata.Data.ToArray();

				await _frameChannel.Writer.WriteAsync(
					new FramePacket( aud, sampleCount, saveData ), token );
			}
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception ex )
		{
			GbaLog.Write( LogCategory.GBA, LogLevel.Fatal, $"Emulation worker error: {ex.Message}\n{ex.StackTrace}" );
			_frameChannel.Writer.TryComplete( ex );
		}
	}

	private const double GbaFrameTime = 1.0 / 59.7275;

	protected override void OnUpdate()
	{
		if ( !IsReady || Core == null ) return;

		PollInput();

		if ( _audioStream != null && _soundHandle is not { IsValid: true } )
		{
			try { InitAudioStream(); }
			catch { _audioStream = null; }
		}

		if ( !_paused )
		{
			_frameDebt += RealTime.Delta;
			if ( _frameDebt > GbaFrameTime * 3 )
				_frameDebt = GbaFrameTime * 3;

			while ( _frameDebt >= GbaFrameTime )
			{
				_frameDebt -= GbaFrameTime;
				if ( _frameSemaphore.CurrentCount < 4 )
					_frameSemaphore.Release();
			}
		}

		bool hasFrame = false;

		while ( _frameChannel != null && _frameChannel.Reader.TryRead( out var frame ) )
		{
			if ( _audioStream != null && frame.AudioSamples > 0
				&& _audioStream.QueuedSampleCount < AudioHighWatermark )
			{
				_audioStream.WriteData( frame.Audio.AsSpan( 0, frame.AudioSamples * 2 ) );
			}

			if ( frame.SaveData != null )
				FileSystem.Data.WriteAllBytes( _savePath, frame.SaveData );

			hasFrame = true;
		}

		if ( hasFrame )
			Core?.Video?.UploadAndBuildCommandList();
		else
			Core?.Video?.RenderCommandList?.Reset();
	}

	private const float StickDeadzone = 0.3f;

	private void PollInput()
	{
		if ( _paused ) return;

		if ( _inputCooldown > 0 )
		{
			bool anyHeld = Input.Down( "GBA_A" ) || Input.Down( "GBA_B" ) ||
				Input.Down( "GBA_Start" ) || Input.Down( "GBA_Select" ) ||
				Input.Down( "GBA_L" ) || Input.Down( "GBA_R" ) ||
				Input.Down( "GBA_Up" ) || Input.Down( "GBA_Down" ) ||
				Input.Down( "GBA_Left" ) || Input.Down( "GBA_Right" ) ||
				MathF.Abs( Input.GetAnalog( InputAnalog.LeftStickX ) ) > StickDeadzone ||
				MathF.Abs( Input.GetAnalog( InputAnalog.LeftStickY ) ) > StickDeadzone;

			if ( anyHeld )
				return;

			_inputCooldown = 0;
		}

		int keys = 0x03FF;
		if ( Input.Down( "GBA_A" ) ) keys &= ~(int)GbaKey.A;
		if ( Input.Down( "GBA_B" ) ) keys &= ~(int)GbaKey.B;
		if ( Input.Down( "GBA_Start" ) ) keys &= ~(int)GbaKey.Start;
		if ( Input.Down( "GBA_Select" ) ) keys &= ~(int)GbaKey.Select;
		if ( Input.Down( "GBA_L" ) ) keys &= ~(int)GbaKey.L;
		if ( Input.Down( "GBA_R" ) ) keys &= ~(int)GbaKey.R;

		float stickX = Input.GetAnalog( InputAnalog.LeftStickX );
		float stickY = Input.GetAnalog( InputAnalog.LeftStickY );
		if ( Input.Down( "GBA_Up" ) || stickY < -StickDeadzone ) keys &= ~(int)GbaKey.Up;
		if ( Input.Down( "GBA_Down" ) || stickY > StickDeadzone ) keys &= ~(int)GbaKey.Down;
		if ( Input.Down( "GBA_Left" ) || stickX < -StickDeadzone ) keys &= ~(int)GbaKey.Left;
		if ( Input.Down( "GBA_Right" ) || stickX > StickDeadzone ) keys &= ~(int)GbaKey.Right;

		Interlocked.Exchange( ref _inputKeys, keys );
	}

	public void SetPaused( bool paused )
	{
		_paused = paused;
		if ( paused )
		{
			_frameDebt = 0;
			if ( _soundHandle is { IsValid: true } )
				_soundHandle.Volume = 0;
		}
		else
		{
			_inputCooldown = 2;
			if ( _soundHandle is { IsValid: true } )
				_soundHandle.Volume = 1.0f;
		}
	}

	public string GetStatePath( int slot ) => $"{_stateBasePath}.ss{slot}";

	public void CreateSuspendPoint( int slot )
	{
		if ( Core == null ) return;
		try
		{
			var screenshot = Core.Video.CaptureScreenshot();
			var data = GbaSerialize.Save( Core, screenshot );
			var path = GetStatePath( slot );
			FileSystem.Data.WriteAllBytes( path, data );
			GbaLog.Write( LogCategory.GBAState, LogLevel.Info, $"Suspend point created in slot {slot}" );
		}
		catch ( Exception ex )
		{
			GbaLog.Write( LogCategory.GBAState, LogLevel.Error, $"Failed to create suspend point {slot}: {ex.Message}" );
		}
	}

	public void LoadSuspendPoint( int slot )
	{
		if ( Core == null ) return;
		try
		{
			var path = GetStatePath( slot );
			if ( !FileSystem.Data.FileExists( path ) )
			{
				GbaLog.Write( LogCategory.GBAState, LogLevel.Warn, $"No suspend point in slot {slot}" );
				return;
			}

			var data = FileSystem.Data.ReadAllBytes( path ).ToArray();
			GbaSerialize.Load( Core, data );
			GbaLog.Write( LogCategory.GBAState, LogLevel.Info, $"Suspend point loaded from slot {slot}" );
		}
		catch ( Exception ex )
		{
			GbaLog.Write( LogCategory.GBAState, LogLevel.Error, $"Failed to load suspend point {slot}: {ex.Message}" );
		}
	}

	public void ResetEmulator()
	{
		Core?.Reset();
		GbaLog.Write( LogCategory.GBA, LogLevel.Info, "Emulator reset" );
	}

	protected override void OnDestroy()
	{
		TearDownCore();
		_camera = null;
	}

	private static void LogBackend( LogCategory category, LogLevel level, string message )
	{
		string formatted = $"{GbaLog.GetCategoryName( category )}: {message}";

		if ( (level & (LogLevel.Fatal | LogLevel.Error)) != 0 )
			Log.Error( formatted );
		else if ( (level & (LogLevel.Warn | LogLevel.GameError)) != 0 )
			Log.Warning( formatted );
		else
			Log.Info( formatted );
	}
}
