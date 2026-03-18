using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace sGBA;

public sealed partial class EmulatorComponent : Component
{
	[Property, Title( "ROM Path" ), FilePath( Extension = "gba" )]
	public string RomPath { get; set; }

	public static EmulatorComponent Current { get; private set; }
	public GbaSystem Core { get; private set; }
	public Texture ScreenTexture { get; private set; }
	public bool IsReady { get; private set; }
	public string ErrorMessage { get; private set; }

	private SoundStream _audioStream;
	private SoundHandle _soundHandle;
	private string _savePath;

	private CancellationTokenSource _cts;
	private Channel<FramePacket> _frameChannel;
	private SemaphoreSlim _frameSemaphore;
	private int _inputKeys = 0x03FF;
	private byte[][] _pixBufs;
	private short[][] _audBufs;
	private int _workerBufIdx;
	private double _frameDebt;

	private bool _paused;
	private int _inputCooldown;
	private byte[] _lastFramePixels;
	private string _stateBasePath;

	private readonly struct FramePacket( byte[] p, short[] a, int ac, byte[] s )
	{
		public readonly byte[] Pixels = p;
		public readonly short[] Audio = a;
		public readonly int AudioSamples = ac;
		public readonly byte[] SaveData = s;
	}

	protected override void OnStart()
	{
		Current = this;

		ScreenTexture = Texture.Create( GbaConstants.ScreenWidth, GbaConstants.ScreenHeight, ImageFormat.RGBA8888 )
			.WithDynamicUsage()
			.WithName( "gba-screen" )
			.Finish();

		try
		{
			if ( !FileSystem.Mounted.FileExists( RomPath ) )
			{
				ErrorMessage = $"ROM not found: {RomPath}";
				Log.Error( ErrorMessage );
				return;
			}

			var romData = FileSystem.Mounted.ReadAllBytes( RomPath ).ToArray();
			if ( romData.Length < 192 )
			{
				ErrorMessage = "ROM file is too small to be a valid GBA ROM.";
				Log.Error( ErrorMessage );
				return;
			}

			Core = new GbaSystem();
			Core.LoadRom( romData );

			_savePath = "saves/" + System.IO.Path.GetFileNameWithoutExtension( RomPath ) + ".sav";
			if ( FileSystem.Data.FileExists( _savePath ) )
			{
				var saveData = FileSystem.Data.ReadAllBytes( _savePath ).ToArray();
				Core.Save.LoadSaveData( saveData );
			}

			Core.Reset();
			IsReady = true;

			_stateBasePath = "states/" + System.IO.Path.GetFileNameWithoutExtension( RomPath );

			try { InitAudioStream(); }
			catch ( Exception audioEx ) { Log.Warning( $"Audio init failed: {audioEx.Message}" ); }

			int pixSize = GbaConstants.ScreenWidth * GbaConstants.ScreenHeight * 4;
			int audSize = Apu.SamplesPerFrame * 2;
			_pixBufs = new byte[4][];
			_audBufs = new short[4][];
			for ( int i = 0; i < 4; i++ )
			{
				_pixBufs[i] = new byte[pixSize];
				_audBufs[i] = new short[audSize];
			}

			_frameChannel = Channel.CreateBounded<FramePacket>( 2 );
			_frameSemaphore = new SemaphoreSlim( 0, 4 );
			_cts = new CancellationTokenSource();
			GameTask.RunInThreadAsync( EmulationLoop );
		}
		catch ( Exception ex )
		{
			ErrorMessage = $"Failed to load ROM: {ex.Message}";
			Log.Error( ErrorMessage );
		}
	}

	private const int AudioHighWatermark = 6600;
	private const int AudioPrefillFrames = 8;

	private void InitAudioStream()
	{
		if ( _audioStream != null )
		{
			_soundHandle.Volume = 0;
			_audioStream.Dispose();
			_audioStream = null;
		}

		_audioStream = new SoundStream( Apu.SampleRate, 2 );
		_audioStream.WriteData( new short[Apu.SamplesPerFrame * 2 * AudioPrefillFrames] );
		_soundHandle = _audioStream.Play( volume: 1.0f );
		_soundHandle.SpacialBlend = 0f;
		_soundHandle.Occlusion = false;
		_soundHandle.DistanceAttenuation = false;
		_soundHandle.AirAbsorption = false;
		_soundHandle.Transmission = false;
		_soundHandle.Stop( float.MaxValue );
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
				var pix = _pixBufs[idx];
				var aud = _audBufs[idx];

				Buffer.BlockCopy( core.Ppu.FrameBuffer, 0, pix, 0, pix.Length );
				int sampleCount = core.Apu.SamplesWritten;
				if ( sampleCount > 0 )
					Buffer.BlockCopy( core.Apu.OutputBuffer, 0, aud, 0, sampleCount * 2 * sizeof( short ) );

				byte[] saveData = null;
				if ( core.Save.TickFrame() && core.Save.Data.Length > 0 )
					saveData = core.Save.Data.ToArray();

				await _frameChannel.Writer.WriteAsync(
					new FramePacket( pix, aud, sampleCount, saveData ), token );
			}
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception ex )
		{
			Log.Error( $"Emulation worker error: {ex.Message}\n{ex.StackTrace}" );
			_frameChannel.Writer.TryComplete( ex );
		}
	}

	private const double GbaFrameTime = 1.0 / 59.7275;

	protected override void OnUpdate()
	{
		if ( !IsReady || Core == null ) return;

		PollInput();

		if ( _audioStream != null && !_soundHandle.IsValid )
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

		FramePacket lastFrame = default;
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

			lastFrame = frame;
			hasFrame = true;
		}

		if ( hasFrame )
		{
			_lastFramePixels ??= new byte[lastFrame.Pixels.Length];
			Buffer.BlockCopy( lastFrame.Pixels, 0, _lastFramePixels, 0, lastFrame.Pixels.Length );

			ScreenTexture.Update( new ReadOnlySpan<byte>( lastFrame.Pixels ),
				0, 0, GbaConstants.ScreenWidth, GbaConstants.ScreenHeight );
		}
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
			if ( _soundHandle.IsValid )
				_soundHandle.Volume = 0;
		}
		else
		{
			_inputCooldown = 2;
			if ( _soundHandle.IsValid )
				_soundHandle.Volume = 1.0f;
		}
	}

	public string GetStatePath( int slot ) => $"{_stateBasePath}.ss{slot}";

	public void CreateSuspendPoint( int slot )
	{
		if ( Core == null ) return;
		try
		{
			var data = SaveState.Save( Core, _lastFramePixels );
			var path = GetStatePath( slot );
			FileSystem.Data.WriteAllBytes( path, data );
			Log.Info( $"Suspend point created in slot {slot}" );
		}
		catch ( Exception ex )
		{
			Log.Error( $"Failed to create suspend point {slot}: {ex.Message}" );
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
				Log.Warning( $"No suspend point in slot {slot}" );
				return;
			}

			var data = FileSystem.Data.ReadAllBytes( path ).ToArray();
			SaveState.Load( Core, data );
			Log.Info( $"Suspend point loaded from slot {slot}" );
		}
		catch ( Exception ex )
		{
			Log.Error( $"Failed to load suspend point {slot}: {ex.Message}" );
		}
	}

	public void ResetEmulator()
	{
		Core?.Reset();
		Log.Info( "Emulator reset" );
	}

	protected override void OnDestroy()
	{
		_cts?.Cancel();

		if ( Core?.Save != null && Core.Save.Data.Length > 0 && _savePath != null )
		{
			FileSystem.Data.WriteAllBytes( _savePath, Core.Save.Data );
		}

		_soundHandle.Volume = 0;
		_audioStream?.Dispose();
		_audioStream = null;
		_frameSemaphore?.Dispose();
		Core = null;
		ScreenTexture = null;
	}
}
