using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Capture.GameHook;

internal readonly record struct OpenGlGameFrame(
    ID3D11Texture2D Texture,
    long Timestamp100ns,
    long RouteEpoch,
    long TargetRevision,
    long Sequence,
    CaptureCursorUpdate Cursor);

/// <summary>Владеет IPC, инъекцией, reader thread и переиспользуемой upload texture.</summary>
internal sealed unsafe class OpenGlGameFrameBridge : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly GameCaptureTarget _target;
    private readonly WindowCursorSampler _cursorSampler;
    private readonly bool _captureCursor;
    private readonly long _generation;
    private readonly object _identitySync = new();
    private readonly GameHookFrameReader _reader = new();
    private readonly MinecraftHookInjectionQuarantine _quarantine = new();
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly Thread _readerThread;
    private readonly WaitHandle[] _waitHandles;
    private readonly byte[] _scratch;
    private readonly GameHookSessionNames _names;
    private readonly long _mappingSize;
    private readonly long _slotStride;
    private MemoryMappedFile? _bootstrapMapping;
    private MemoryMappedViewAccessor? _bootstrapView;
    private MemoryMappedFile? _frameMapping;
    private MemoryMappedViewAccessor? _frameView;
    private EventWaitHandle? _frameReadyEvent;
    private EventWaitHandle? _controlEvent;
    private Timer? _heartbeatTimer;
    private byte* _bootstrapPointer;
    private byte* _framePointer;
    private IGameHookMemoryView? _memoryView;
    private GameHookFrameExpectedIdentity _expected;
    private ID3D11Texture2D? _uploadTexture;
    private ID3D11Texture2D? _frameTexture;
    private int _textureWidth;
    private int _textureHeight;
    private bool _started;
    private bool _disposed;
    private long _framesUploaded;
    private long _framesRejected;

    public OpenGlGameFrameBridge(
        ID3D11Device device,
        ID3D11DeviceContext context,
        in GameCaptureTarget target,
        int maximumWidth,
        int maximumHeight,
        int targetFps,
        bool captureCursor,
        long generation,
        long initialRouteEpoch)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        if (targetFps is <= 0 or > 240) throw new ArgumentOutOfRangeException(nameof(targetFps));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (initialRouteEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(initialRouteEpoch));

        _target = target;
        _cursorSampler = new WindowCursorSampler(target);
        _captureCursor = captureCursor;
        _generation = generation;
        _slotStride = GameHookProtocol.CalculateSlotStride(maximumWidth, maximumHeight);
        _mappingSize = checked(GameHookProtocol.HeaderSize + GameHookProtocol.SlotCount * _slotStride);
        _scratch = new byte[GameHookProtocol.CalculateFrameByteCount(maximumWidth, maximumHeight)];
        _names = GameHookSessionNames.Create(target.ProcessId, Environment.ProcessId);
        _expected = new GameHookFrameExpectedIdentity(
            target.ProcessId,
            target.ProcessStartTicks,
            unchecked((ulong)(nuint)target.Hwnd),
            target.Revision,
            generation,
            initialRouteEpoch);

        CreateSharedObjects(maximumWidth, maximumHeight, targetFps, initialRouteEpoch);
        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Aura OpenGL game frame reader"
        };
        _waitHandles = [_frameReadyEvent!, _stopEvent];
    }

    public event Action<OpenGlGameFrame>? FrameArrived;
    public event Action<Exception>? Failed;

    public long FramesUploaded => Interlocked.Read(ref _framesUploaded);
    public long FramesRejected => Interlocked.Read(ref _framesRejected);
    public long InvalidCursorShapes => _cursorSampler.InvalidShapes;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("OpenGL bridge уже запущен");
        _started = true;
        _readerThread.Start();
        _heartbeatTimer = new Timer(
            _ => WriteControllerHeartbeat(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250));

        try
        {
            MinecraftHookEligibilitySnapshot snapshot = MinecraftProcessInspector.Inspect(_target);
            MinecraftHookEligibilityResult eligibility = MinecraftHookEligibility.Evaluate(_target, snapshot);
            var injector = new MinecraftHookInjector(_quarantine);
            string hookPath = Path.Combine(AppContext.BaseDirectory, "Aura.GameCaptureHook64.dll");
            MinecraftHookInjectionAttempt attempt = injector.InjectUsingEmbeddedHash(
                eligibility,
                hookPath,
                TimeSpan.FromSeconds(3));
            if (attempt.Result != MinecraftHookInjectionResult.Success)
                throw new InvalidOperationException(
                    $"Minecraft hook не запущен: {attempt.Result}, eligibility={attempt.EligibilityReason}, " +
                    $"Win32={attempt.Win32Error}");
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    public void SetRoute(long routeEpoch, bool captureEnabled)
    {
        if (routeEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(routeEpoch));
        if (_framePointer is null) return;

        lock (_identitySync)
        {
            _expected = _expected with { RouteEpoch = routeEpoch };
            GameHookHeader* header = (GameHookHeader*)_framePointer;
            Volatile.Write(ref header->RouteEpoch, routeEpoch);
            Volatile.Write(
                ref Unsafe.As<GameHookCommand, int>(ref header->Command),
                captureEnabled
                    ? (int)GameHookCommand.Capture
                    : (int)GameHookCommand.Idle);
        }
        _controlEvent?.Set();
    }

    private void CreateSharedObjects(
        int maximumWidth,
        int maximumHeight,
        int targetFps,
        long initialRouteEpoch)
    {
        _bootstrapMapping = MemoryMappedFile.CreateNew(
            _names.BootstrapMapping,
            GameHookProtocol.BootstrapHeaderSize,
            MemoryMappedFileAccess.ReadWrite);
        _bootstrapView = _bootstrapMapping.CreateViewAccessor(
            0,
            GameHookProtocol.BootstrapHeaderSize,
            MemoryMappedFileAccess.ReadWrite);
        _bootstrapView.SafeMemoryMappedViewHandle.AcquirePointer(ref _bootstrapPointer);
        _bootstrapPointer += _bootstrapView.PointerOffset;

        _frameMapping = MemoryMappedFile.CreateNew(
            _names.FrameMapping,
            _mappingSize,
            MemoryMappedFileAccess.ReadWrite);
        _frameView = _frameMapping.CreateViewAccessor(0, _mappingSize, MemoryMappedFileAccess.ReadWrite);
        _frameView.SafeMemoryMappedViewHandle.AcquirePointer(ref _framePointer);
        _framePointer += _frameView.PointerOffset;
        _memoryView = new UnmanagedGameHookMemoryView(_framePointer, _mappingSize);
        _frameReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _names.FrameReadyEvent);
        _controlEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _names.ControlEvent);

        Unsafe.InitBlockUnaligned(_bootstrapPointer, 0, GameHookProtocol.BootstrapHeaderSize);
        var bootstrap = new GameHookBootstrapHeader
        {
            Magic = GameHookProtocol.BootstrapMagic,
            Version = GameHookProtocol.Version,
            HeaderSize = GameHookProtocol.BootstrapHeaderSize,
            ControllerPid = Environment.ProcessId,
            TargetPid = _target.ProcessId,
            TargetProcessStartTicks = _target.ProcessStartTicks,
            TargetHwnd = unchecked((ulong)(nuint)_target.Hwnd),
            NonceByteCount = 32
        };
        for (int index = 0; index < 32; ++index)
            bootstrap.Nonce[index] = (byte)_names.Nonce[index];
        Unsafe.WriteUnaligned(ref *_bootstrapPointer, bootstrap);

        Unsafe.InitBlockUnaligned(_framePointer, 0, checked((uint)_mappingSize));
        var header = new GameHookHeader
        {
            Magic = GameHookProtocol.Magic,
            Version = GameHookProtocol.Version,
            HeaderSize = GameHookProtocol.HeaderSize,
            MappingSize = _mappingSize,
            ControllerPid = Environment.ProcessId,
            TargetPid = _target.ProcessId,
            TargetProcessStartTicks = _target.ProcessStartTicks,
            TargetHwnd = unchecked((ulong)(nuint)_target.Hwnd),
            TargetRevision = _target.Revision,
            CaptureGeneration = _generation,
            RouteEpoch = initialRouteEpoch,
            Command = GameHookCommand.Idle,
            State = GameHookState.Empty,
            Error = GameHookError.None,
            TargetFps = targetFps,
            Width = maximumWidth,
            Height = maximumHeight,
            Stride = checked(maximumWidth * GameHookProtocol.BytesPerPixel),
            PixelFormat = GameHookPixelFormat.Bgra8,
            SlotCount = GameHookProtocol.SlotCount,
            SlotHeaderSize = GameHookProtocol.SlotHeaderSize,
            SlotStride = _slotStride
        };
        Unsafe.WriteUnaligned(ref *_framePointer, header);
    }

    private void ReadLoop()
    {
        while (!_disposed)
        {
            int signaled = WaitHandle.WaitAny(_waitHandles, TimeSpan.FromSeconds(1));
            if (signaled == 1) return;
            if (signaled != 0 || _memoryView is null) continue;

            try
            {
                GameHookFrameExpectedIdentity expected;
                lock (_identitySync) expected = _expected;
                GameHookHeader peek = _memoryView.ReadHeader();
                if (peek.Width <= 0 || peek.Height <= 0 ||
                    peek.Width > GameHookProtocol.MaxWidth ||
                    peek.Height > GameHookProtocol.MaxHeight)
                    continue;
                EnsureTextures(peek.Width, peek.Height);
                MappedSubresource mapped = _context.Map(
                    _uploadTexture!,
                    0,
                    MapMode.WriteDiscard,
                    Vortice.Direct3D11.MapFlags.None);
                GameHookFrameSnapshot frame;
                bool accepted;
                try
                {
                    int peekByteCount = checked(peek.Stride * peek.Height);
                    if (peek.Stride > 0 && mapped.RowPitch == peek.Stride &&
                        peekByteCount <= _scratch.Length)
                    {
                        var directDestination = new Span<byte>(
                            (void*)mapped.DataPointer,
                            peekByteCount);
                        accepted = _reader.TryRead(
                            _memoryView,
                            expected,
                            directDestination,
                            out frame,
                            out _);
                    }
                    else
                    {
                        accepted = _reader.TryRead(
                            _memoryView,
                            expected,
                            _scratch,
                            out frame,
                            out _);
                        if (accepted)
                        {
                            fixed (byte* source = _scratch)
                            {
                                byte* destination = (byte*)mapped.DataPointer;
                                for (int row = 0; row < frame.Height; ++row)
                                {
                                    Buffer.MemoryCopy(
                                        source + row * frame.Stride,
                                        destination + row * mapped.RowPitch,
                                        mapped.RowPitch,
                                        frame.Stride);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_uploadTexture!, 0);
                }

                if (!accepted)
                {
                    Interlocked.Increment(ref _framesRejected);
                    continue;
                }

                _context.CopyResource(_frameTexture!, _uploadTexture!);
                Interlocked.Increment(ref _framesUploaded);
                FrameArrived?.Invoke(new OpenGlGameFrame(
                    _frameTexture!,
                    frame.Timestamp100ns,
                    frame.RouteEpoch,
                    _target.Revision,
                    frame.Sequence,
                    _cursorSampler.Sample(_captureCursor)));
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    private void EnsureTextures(int width, int height)
    {
        if (_uploadTexture is not null && _textureWidth == width && _textureHeight == height)
            return;

        _frameTexture?.Dispose();
        _uploadTexture?.Dispose();
        _textureWidth = width;
        _textureHeight = height;
        var uploadDescription = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            BindFlags = BindFlags.ShaderResource
        };
        _uploadTexture = _device.CreateTexture2D(uploadDescription);
        _frameTexture = _device.CreateTexture2D(uploadDescription with
        {
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        });
    }

    private void WriteControllerHeartbeat()
    {
        if (_framePointer is null || _disposed) return;
        GameHookHeader* header = (GameHookHeader*)_framePointer;
        Volatile.Write(ref header->ControllerHeartbeat100ns, Environment.TickCount64 * 10_000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        if (_framePointer is not null)
        {
            GameHookHeader* header = (GameHookHeader*)_framePointer;
            Volatile.Write(
                ref Unsafe.As<GameHookCommand, int>(ref header->Command),
                (int)GameHookCommand.Stop);
            _controlEvent?.Set();
        }
        _stopEvent.Set();
        if (_readerThread.IsAlive && !_readerThread.Join(TimeSpan.FromSeconds(2)))
            Failed?.Invoke(new TimeoutException("OpenGL reader thread не остановился за 2 секунды"));

        _frameTexture?.Dispose();
        _uploadTexture?.Dispose();
        if (_frameView is not null && _framePointer is not null)
        {
            _framePointer -= _frameView.PointerOffset;
            _frameView.SafeMemoryMappedViewHandle.ReleasePointer();
            _framePointer = null;
        }
        if (_bootstrapView is not null && _bootstrapPointer is not null)
        {
            _bootstrapPointer -= _bootstrapView.PointerOffset;
            _bootstrapView.SafeMemoryMappedViewHandle.ReleasePointer();
            _bootstrapPointer = null;
        }
        _controlEvent?.Dispose();
        _frameReadyEvent?.Dispose();
        _frameView?.Dispose();
        _frameMapping?.Dispose();
        _bootstrapView?.Dispose();
        _bootstrapMapping?.Dispose();
        _stopEvent.Dispose();
    }
}
