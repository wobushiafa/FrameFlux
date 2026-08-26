using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
#if ANDROID
using AndroidSurfaceTexture = Android.Graphics.SurfaceTexture;
#endif
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrameFlux.Avalonia;

public unsafe class DesktopOpenGlRtspVideoView : OpenGlControlBase
{
    private const uint GlArrayBuffer = 0x8892;
    private const uint GlColorBufferBit = 0x00004000;
    private const uint GlFloat = 0x1406;
    private const uint GlFragmentShader = 0x8B30;
    private const uint GlLinear = 0x2601;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlRed = 0x1903;
    private const int GlR8 = 0x8229;
    private const uint GlRgba = 0x1908;
    private const uint GlBgra = 0x80E1;
    private const uint GlRg = 0x8227;
    private const int GlRg8 = 0x822B;
    private const uint GlStaticDraw = 0x88E4;
    private const uint GlTexture0 = 0x84C0;
    private const uint GlTexture1 = 0x84C1;
    private const uint GlTexture2 = 0x84C2;
    private const uint GlTexture2D = 0x0DE1;
#if ANDROID
    private const uint GlTextureExternalOes = 0x8D65;
#endif
    private const uint GlTextureMagFilter = 0x2800;
    private const uint GlTextureMinFilter = 0x2801;
    private const uint GlTextureWrapS = 0x2802;
    private const uint GlTextureWrapT = 0x2803;
    private const uint GlUnpackAlignment = 0x0CF5;
    private const uint GlUnpackRowLength = 0x0CF2;
    private const int GlClampToEdge = 0x812F;
    private const uint GlTriangleStrip = 0x0005;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlVertexShader = 0x8B31;
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlFramebuffer = 0x8D40;

    private static readonly byte[] PositionName = Encoding.ASCII.GetBytes("aPosition\0");
    private static readonly byte[] TexCoordName = Encoding.ASCII.GetBytes("aTexCoord\0");
    private static readonly byte[] TextureSamplerName = Encoding.ASCII.GetBytes("uTexture\0");
    private static readonly byte[] TextureYSamplerName = Encoding.ASCII.GetBytes("uTextureY\0");
    private static readonly byte[] TextureUSamplerName = Encoding.ASCII.GetBytes("uTextureU\0");
    private static readonly byte[] TextureVSamplerName = Encoding.ASCII.GetBytes("uTextureV\0");
    private static readonly byte[] TextureUvSamplerName = Encoding.ASCII.GetBytes("uTextureUV\0");
#if ANDROID
    private static readonly byte[] OesTextureSamplerName = Encoding.ASCII.GetBytes("uOesTexture\0");
#endif

    protected RtspStreamClient? _streamClient;
#if ANDROID
    private AndroidSurfaceTextureRtspClient? _surfaceTextureClient;
    private AndroidSurfaceTexture? _surfaceTexture;
    private uint _oesTextureId;
    private uint _oesProgram;
    private uint _oesFragmentShader;
    private ProgramBindings _oesBindings;
    private SurfaceTextureFrameAvailableListener? _surfaceTextureFrameListener;
    private bool _hasLatchedSurfaceTextureFrame;
    private bool _surfaceTextureAttachedToGlContext;
    private bool _surfaceTextureContextInvalid;
    private bool _useSurfaceTextureRendering;
    private bool _surfaceTextureStartPending;
    private Task _surfaceTextureCleanup = Task.CompletedTask;
#endif
    private RtspConnectionState _connectionState = RtspConnectionState.Idle;
    private RtspStreamError? _lastError;
    private bool _isHardwareAccelerationActive;
    private readonly object _frameLock = new();
    private IntPtr _frameBuffer;
    private int _frameBufferSize;
    private RtspFrameLease? _pendingLease;
    private int _frameWidth;
    private int _frameHeight;
    private RtspNativePixelFormat _currentPixelFormat = RtspNativePixelFormat.Bgra32;
    private bool _hasNewFrame;
    private bool _isAttached;
    private volatile bool _isVisuallyAttached;
    private bool _restartPending;
    private CancellationTokenSource? _shutdownCancellation;
    private int _streamVersion;
    private string _performanceSummary = string.Empty;
    private long _copyTotalTicks;
    private int _copySamples;
    private long _uploadTotalTicks;
    private int _uploadSamples;
    private string _streamPerformanceSummary = string.Empty;
    private string _renderPerformanceSummary = string.Empty;
    private bool _isCoreProfile;
    private bool _isGles;

    private GlBindings? _gl;
    private uint _vertexShader;
    private uint _bgraProgram;
    private uint _bgraFragmentShader;
    private uint _yuv420Program;
    private uint _yuv420FragmentShader;
    private uint _nv12Program;
    private uint _nv12FragmentShader;
    private uint _nv21Program;
    private uint _nv21FragmentShader;
    private uint _textureId;
    private uint _textureId2;
    private uint _textureId3;
    private uint _vertexBufferId;
    private uint _vertexArrayId;
    private ProgramBindings _bgraBindings;
    private ProgramBindings _yuv420Bindings;
    private ProgramBindings _nv12Bindings;
    private ProgramBindings _nv21Bindings;
    private int _uploadedWidth;
    private int _uploadedHeight;
    private RtspNativePixelFormat? _uploadedPixelFormat;

    public static readonly StyledProperty<string> StreamUrlProperty =
        RtspVideoView.StreamUrlProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> IsPlaybackEnabledProperty =
        RtspVideoView.IsPlaybackEnabledProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> KeepPlaybackAliveWhenDetachedProperty =
        RtspVideoView.KeepPlaybackAliveWhenDetachedProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> UseHardwareAccelerationProperty =
        RtspVideoView.UseHardwareAccelerationProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<RtspHardwareAccelerationMode> HardwareAccelerationModeProperty =
        RtspVideoView.HardwareAccelerationModeProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> FallbackToSoftwareDecodingProperty =
        RtspVideoView.FallbackToSoftwareDecodingProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<double> MaxFramesPerSecondProperty =
        RtspVideoView.MaxFramesPerSecondProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<string> TransportProperty =
        RtspVideoView.TransportProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> OpenTimeoutMillisecondsProperty =
        RtspVideoView.OpenTimeoutMillisecondsProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> EndpointProbeTimeoutMillisecondsProperty =
        RtspVideoView.EndpointProbeTimeoutMillisecondsProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> ReadTimeoutMillisecondsProperty =
        RtspVideoView.ReadTimeoutMillisecondsProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> ReconnectDelayMillisecondsProperty =
        RtspVideoView.ReconnectDelayMillisecondsProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> MaxVideoWidthProperty =
        RtspVideoView.MaxVideoWidthProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> MaxVideoHeightProperty =
        RtspVideoView.MaxVideoHeightProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> LowLatencyProperty =
        RtspVideoView.LowLatencyProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> EnableAudioProperty =
        RtspVideoView.EnableAudioProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<double> VolumeProperty =
        RtspVideoView.VolumeProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<bool> IsMutedProperty =
        RtspVideoView.IsMutedProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<RtspScaleQuality> ScaleQualityProperty =
        RtspVideoView.ScaleQualityProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly StyledProperty<int> MaxConcurrentOpenStreamsProperty =
        AvaloniaProperty.Register<DesktopOpenGlRtspVideoView, int>(
            nameof(MaxConcurrentOpenStreams),
            RtspStreamOptions.DefaultMaxConcurrentOpenStreams);

    public static readonly StyledProperty<Stretch> StretchProperty =
        RtspVideoView.StretchProperty.AddOwner<DesktopOpenGlRtspVideoView>();

    public static readonly DirectProperty<DesktopOpenGlRtspVideoView, RtspConnectionState> ConnectionStateProperty =
        RtspVideoView.ConnectionStateProperty.AddOwner<DesktopOpenGlRtspVideoView>(
            view => view.ConnectionState);

    public static readonly DirectProperty<DesktopOpenGlRtspVideoView, RtspStreamError?> LastErrorProperty =
        RtspVideoView.LastErrorProperty.AddOwner<DesktopOpenGlRtspVideoView>(
            view => view.LastError);

    public static readonly DirectProperty<DesktopOpenGlRtspVideoView, bool> IsHardwareAccelerationActiveProperty =
        RtspVideoView.IsHardwareAccelerationActiveProperty.AddOwner<DesktopOpenGlRtspVideoView>(
            view => view.IsHardwareAccelerationActive);

    public static readonly DirectProperty<DesktopOpenGlRtspVideoView, RtspRenderMode> EffectiveRenderModeProperty =
        RtspVideoView.EffectiveRenderModeProperty.AddOwner<DesktopOpenGlRtspVideoView>(
            view => view.EffectiveRenderMode);

    public static readonly DirectProperty<DesktopOpenGlRtspVideoView, string> PerformanceSummaryProperty =
        RtspVideoView.PerformanceSummaryProperty.AddOwner<DesktopOpenGlRtspVideoView>(
            view => view.PerformanceSummary);

    static DesktopOpenGlRtspVideoView()
    {
        StretchProperty.Changed.AddClassHandler<DesktopOpenGlRtspVideoView>((x, e) => x.RequestNextFrameRendering());
    }

    public event EventHandler<RtspConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<RtspStreamErrorEventArgs>? StreamError;

    public string StreamUrl
    {
        get => GetValue(StreamUrlProperty);
        set => SetValue(StreamUrlProperty, value);
    }

    public bool IsPlaybackEnabled
    {
        get => GetValue(IsPlaybackEnabledProperty);
        set => SetValue(IsPlaybackEnabledProperty, value);
    }

    public bool KeepPlaybackAliveWhenDetached
    {
        get => GetValue(KeepPlaybackAliveWhenDetachedProperty);
        set => SetValue(KeepPlaybackAliveWhenDetachedProperty, value);
    }

    public bool UseHardwareAcceleration
    {
        get => GetValue(UseHardwareAccelerationProperty);
        set => SetValue(UseHardwareAccelerationProperty, value);
    }

    public RtspHardwareAccelerationMode HardwareAccelerationMode
    {
        get => GetValue(HardwareAccelerationModeProperty);
        set => SetValue(HardwareAccelerationModeProperty, value);
    }

    public bool FallbackToSoftwareDecoding
    {
        get => GetValue(FallbackToSoftwareDecodingProperty);
        set => SetValue(FallbackToSoftwareDecodingProperty, value);
    }

    public double MaxFramesPerSecond
    {
        get => GetValue(MaxFramesPerSecondProperty);
        set => SetValue(MaxFramesPerSecondProperty, value);
    }

    public string Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    public int OpenTimeoutMilliseconds
    {
        get => GetValue(OpenTimeoutMillisecondsProperty);
        set => SetValue(OpenTimeoutMillisecondsProperty, value);
    }

    public int EndpointProbeTimeoutMilliseconds
    {
        get => GetValue(EndpointProbeTimeoutMillisecondsProperty);
        set => SetValue(EndpointProbeTimeoutMillisecondsProperty, value);
    }

    public int ReadTimeoutMilliseconds
    {
        get => GetValue(ReadTimeoutMillisecondsProperty);
        set => SetValue(ReadTimeoutMillisecondsProperty, value);
    }

    public int ReconnectDelayMilliseconds
    {
        get => GetValue(ReconnectDelayMillisecondsProperty);
        set => SetValue(ReconnectDelayMillisecondsProperty, value);
    }

    public int MaxVideoWidth
    {
        get => GetValue(MaxVideoWidthProperty);
        set => SetValue(MaxVideoWidthProperty, value);
    }

    public int MaxVideoHeight
    {
        get => GetValue(MaxVideoHeightProperty);
        set => SetValue(MaxVideoHeightProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool LowLatency
    {
        get => GetValue(LowLatencyProperty);
        set => SetValue(LowLatencyProperty, value);
    }

    public bool EnableAudio
    {
        get => GetValue(EnableAudioProperty);
        set => SetValue(EnableAudioProperty, value);
    }

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, Math.Clamp(value, 0d, 1d));
    }

    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public RtspScaleQuality ScaleQuality
    {
        get => GetValue(ScaleQualityProperty);
        set => SetValue(ScaleQualityProperty, value);
    }



    public int MaxConcurrentOpenStreams
    {
        get => GetValue(MaxConcurrentOpenStreamsProperty);
        set => SetValue(MaxConcurrentOpenStreamsProperty, value);
    }

    public RtspConnectionState ConnectionState
    {
        get => _connectionState;
        private set => SetConnectionState(value);
    }

    public RtspStreamError? LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    public bool IsHardwareAccelerationActive
    {
        get => _isHardwareAccelerationActive;
        private set => SetAndRaise(IsHardwareAccelerationActiveProperty, ref _isHardwareAccelerationActive, value);
    }

    public RtspRenderMode EffectiveRenderMode => GetEffectiveRenderMode();

    public string PerformanceSummary
    {
        get => _performanceSummary;
        private set => SetAndRaise(PerformanceSummaryProperty, ref _performanceSummary, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _isVisuallyAttached = true;
        _streamClient?.SetFrameDeliveryEnabled(true);

        if (_shutdownCancellation != null)
        {
            _shutdownCancellation.Cancel();
            _shutdownCancellation.Dispose();
            _shutdownCancellation = null;
        }

        if (!HasActiveStream())
        {
            QueueRestartStream();
        }
        else
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isVisuallyAttached = false;
        _streamClient?.SetFrameDeliveryEnabled(false);

        _shutdownCancellation?.Cancel();
        _shutdownCancellation?.Dispose();
        _shutdownCancellation = null;

        if (KeepPlaybackAliveWhenDetached)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _shutdownCancellation = cancellation;
        var token = cancellation.Token;
        _ = Task.Delay(1500, token).ContinueWith(delay =>
        {
            if (delay.IsCanceled)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested ||
                    !ReferenceEquals(_shutdownCancellation, cancellation))
                {
                    return;
                }

                _shutdownCancellation = null;
                cancellation.Dispose();
                Shutdown();
            }, DispatcherPriority.Background);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StreamUrlProperty ||
            change.Property == IsPlaybackEnabledProperty ||
            change.Property == UseHardwareAccelerationProperty ||
            change.Property == HardwareAccelerationModeProperty ||
            change.Property == FallbackToSoftwareDecodingProperty ||
            change.Property == MaxFramesPerSecondProperty ||
            change.Property == TransportProperty ||
            change.Property == OpenTimeoutMillisecondsProperty ||
            change.Property == EndpointProbeTimeoutMillisecondsProperty ||
            change.Property == ReadTimeoutMillisecondsProperty ||
            change.Property == ReconnectDelayMillisecondsProperty ||
            change.Property == MaxVideoWidthProperty ||
            change.Property == MaxVideoHeightProperty ||
            change.Property == LowLatencyProperty ||
            change.Property == EnableAudioProperty ||
            change.Property == ScaleQualityProperty)
        {
            QueueRestartStream();
        }
        else if (change.Property == VolumeProperty)
        {
            _streamClient?.SetVolume(Volume);
#if ANDROID
            _surfaceTextureClient?.SetVolume(Volume);
#endif
        }
        else if (change.Property == IsMutedProperty)
        {
            _streamClient?.SetMuted(IsMuted);
#if ANDROID
            _surfaceTextureClient?.SetMuted(IsMuted);
#endif
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new GlBindings(gl);
            var versionString = _gl.GetString(0x1F02); // GL_VERSION
            _isGles = versionString.Contains("OpenGL ES");
            
            // For core profile detection, we can check the version string or use GetIntegerv
            // but for now, let's assume core if it's not GLES and version >= 3.0
            // Most modern Linux desktops will use a core profile if requested.
            _isCoreProfile = !_isGles && (versionString.StartsWith("3.") || versionString.StartsWith("4."));

            EnsureGlResourcesInitialized();
#if ANDROID
            TryAttachSurfaceTextureToGlContext();
#endif
        }
        catch (Exception ex)
        {
            ReportRenderError("Failed to initialize the OpenGL renderer.", ex);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
#if ANDROID
        DetachSurfaceTextureFromGlContext();
#endif
        DestroyGlResources();
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl == null)
        {
            return;
        }

        try
        {
            EnsureGlResourcesInitialized();
            _gl.BindFramebuffer(GlFramebuffer, (uint)fb);
            var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
            var scaling = topLevel?.RenderScaling ?? 1.0;

            _gl.Viewport(0, 0, Math.Max(1, (int)(Bounds.Width * scaling)), Math.Max(1, (int)(Bounds.Height * scaling)));
            _gl.ClearColor(0, 0, 0, 1);
            _gl.Clear(GlColorBufferBit);

#if ANDROID
            StartPendingSurfaceTextureStream();
            UpdateSurfaceTextureFrame();
#endif
            UploadPendingFrame();
            var programBindings = GetActiveProgramBindings();
#if ANDROID
            if (_currentPixelFormat == RtspNativePixelFormat.AndroidSurfaceTexture &&
                !_hasLatchedSurfaceTextureFrame)
            {
                return;
            }
#endif
            if (GetActiveTextureId() == 0 || _uploadedWidth <= 0 || _uploadedHeight <= 0 || programBindings.Program == 0 || _vertexBufferId == 0)
            {
                return;
            }

            var stretch = Stretch;
            var destRect = stretch == Stretch.Fill 
                ? new Rect(Bounds.Size) 
                : RtspVideoView.CalculateDestinationRect(new Size(_uploadedWidth, _uploadedHeight), Bounds.Size, stretch);

            _gl.Viewport(
                (int)(destRect.X * scaling), 
                (int)((Bounds.Height - destRect.Y - destRect.Height) * scaling), 
                Math.Max(1, (int)(destRect.Width * scaling)), 
                Math.Max(1, (int)(destRect.Height * scaling)));

            _gl.UseProgram(programBindings.Program);
            BindTextures(programBindings);
            if (RtspOpenGlFrameUtilities.UsesVertexArrayObject())
            {
                _gl.BindVertexArray(_vertexArrayId);
            }
            _gl.BindBuffer(GlArrayBuffer, _vertexBufferId);

            _gl.EnableVertexAttribArray((uint)programBindings.PositionLocation);
            _gl.EnableVertexAttribArray((uint)programBindings.TexCoordLocation);

            _gl.VertexAttribPointer((uint)programBindings.PositionLocation, 2, GlFloat, false, 4 * sizeof(float), (void*)0);
            _gl.VertexAttribPointer((uint)programBindings.TexCoordLocation, 2, GlFloat, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
            _gl.DrawArrays(GlTriangleStrip, 0, 4);

            _gl.DisableVertexAttribArray((uint)programBindings.PositionLocation);
            _gl.DisableVertexAttribArray((uint)programBindings.TexCoordLocation);
            _gl.BindBuffer(GlArrayBuffer, 0);
            if (RtspOpenGlFrameUtilities.UsesVertexArrayObject())
            {
                _gl.BindVertexArray(0);
            }
        }
        catch (Exception ex)
        {
            ReportRenderError("Failed to render the OpenGL video frame.", ex);
        }
    }

    private void QueueRestartStream()
    {
        if (!_isAttached || _restartPending)
        {
            return;
        }

        _restartPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _restartPending = false;
            RestartStream();
        }, DispatcherPriority.Background);
    }

    protected void RequestStreamRestart() => QueueRestartStream();

    private bool HasActiveStream()
    {
#if ANDROID
        return _surfaceTextureClient != null || _streamClient != null || _surfaceTextureStartPending;
#else
        return _streamClient != null;
#endif
    }

    private void RestartStream()
    {
        var cleanup = StopStream();
        LastError = null;
        var version = _streamVersion;

        if (!_isAttached)
        {
            ConnectionState = RtspConnectionState.Idle;
            return;
        }

        if (!IsPlaybackEnabled || string.IsNullOrWhiteSpace(StreamUrl))
        {
            ConnectionState = string.IsNullOrWhiteSpace(StreamUrl) ? RtspConnectionState.Idle : RtspConnectionState.Stopped;
            return;
        }

        ConnectionState = RtspConnectionState.Connecting;
        if (cleanup.IsCompletedSuccessfully)
        {
            StartStream(version);
            return;
        }

        StartStreamAfterCleanup(cleanup, version);
    }

    private void StartStreamAfterCleanup(Task cleanup, int version)
    {
        _ = cleanup.ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(
                () => StartStream(version),
                DispatcherPriority.Background);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StartStream(int version)
    {
        if (version != _streamVersion ||
            !_isAttached ||
            !IsPlaybackEnabled ||
            string.IsNullOrWhiteSpace(StreamUrl))
        {
            return;
        }

#if ANDROID
        if (!CreateStreamOptions().UseHardwareAcceleration)
        {
            var softwareClient = new RtspStreamClient(StreamUrl, CreateStreamOptions());
            _streamClient = softwareClient;
            AttachStreamClient(softwareClient, _streamVersion);
            softwareClient.Start();
            return;
        }

        if (_gl == null || _oesTextureId == 0)
        {
            _surfaceTextureStartPending = true;
            RequestNextFrameRendering();
            return;
        }

        if (QueueSurfaceTextureStreamStart())
        {
            return;
        }
#endif
        var client = new RtspStreamClient(StreamUrl, CreateStreamOptions());
        _streamClient = client;
        AttachStreamClient(client, _streamVersion);
        client.Start();
    }

    private Task StopStream()
    {
        _streamVersion++;
        var client = _streamClient;
        _streamClient = null;
        var streamCleanup = Task.CompletedTask;
        if (client != null)
        {
            client.Stop();
            streamCleanup = DisposeStreamClientAsync(client);
        }

#if ANDROID
        var surfaceCleanup = ReleaseSurfaceTextureClient();
        _useSurfaceTextureRendering = false;
        _hasLatchedSurfaceTextureFrame = false;
        _surfaceTextureStartPending = false;
#endif
        ReleasePendingLease();
        FreeFrameBuffer();
        IsHardwareAccelerationActive = false;
        _frameWidth = 0;
        _frameHeight = 0;
        _uploadedWidth = 0;
        _uploadedHeight = 0;
        _currentPixelFormat = RtspNativePixelFormat.Bgra32;
        _uploadedPixelFormat = null;
        _streamPerformanceSummary = string.Empty;
        _renderPerformanceSummary = string.Empty;
        PerformanceSummary = string.Empty;
        ConnectionState = RtspConnectionState.Stopped;
#if ANDROID
        return Task.WhenAll(streamCleanup, surfaceCleanup);
#else
        return streamCleanup;
#endif
    }

    private static Task DisposeStreamClientAsync(RtspStreamClient client)
    {
        return client.Completion.ContinueWith(_ =>
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to dispose OpenGL RTSP stream client: {ex}");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void Shutdown()
    {
        _isAttached = false;
        _ = StopStream();
    }

    internal void Suspend()
    {
        _ = StopStream();
        DestroyGlResources();
        _uploadedWidth = 0;
        _uploadedHeight = 0;
        _uploadedPixelFormat = null;
        //InvalidateVisual();
        RequestNextFrameRendering();
    }

    protected bool IsCurrentStreamVersion(int version) =>
        version == Volatile.Read(ref _streamVersion);


    protected virtual RtspRenderMode GetEffectiveRenderMode() => RtspRenderMode.NativeSurface;

#if ANDROID
    private void DetachSurfaceTextureFromGlContext()
    {
        if (_surfaceTexture == null || !_surfaceTextureAttachedToGlContext)
        {
            return;
        }

        try
        {
            _surfaceTexture.DetachFromGLContext();
            _surfaceTextureAttachedToGlContext = false;
            _surfaceTextureContextInvalid = false;
            _oesTextureId = 0;
        }
        catch (Exception ex)
        {
            _surfaceTextureAttachedToGlContext = false;
            _surfaceTextureContextInvalid = true;
            _oesTextureId = 0;
            _surfaceTextureFrameListener?.ClearPendingFrame();
            ResetSurfaceTextureLatch();
            ReportRecoverableRenderIssue(
                "Failed to detach Android SurfaceTexture from the previous GL context.",
                ex);
        }
    }

    private void TryAttachSurfaceTextureToGlContext()
    {
        if (_surfaceTexture == null || _surfaceTextureClient == null)
        {
            return;
        }

        if (_surfaceTextureContextInvalid || _oesTextureId == 0)
        {
            QueueRestartStream();
            return;
        }

        if (_surfaceTextureAttachedToGlContext)
        {
            return;
        }

        try
        {
            _surfaceTexture.AttachToGLContext((int)_oesTextureId);
            _surfaceTextureAttachedToGlContext = true;
            InitializeTextureParameters(_oesTextureId, GlTextureExternalOes);
            RequestNextFrameRendering();
        }
        catch (Exception ex)
        {
            _surfaceTextureAttachedToGlContext = false;
            _surfaceTextureContextInvalid = true;
            _oesTextureId = 0;
            _surfaceTextureFrameListener?.ClearPendingFrame();
            ResetSurfaceTextureLatch();
            ReportRecoverableRenderIssue(
                "Failed to attach Android SurfaceTexture to the new GL context.",
                ex);
            QueueRestartStream();
        }
    }

    private void ResetSurfaceTextureLatch()
    {
        lock (_frameLock)
        {
            _hasLatchedSurfaceTextureFrame = false;
            _uploadedWidth = 0;
            _uploadedHeight = 0;
            _uploadedPixelFormat = null;
        }
    }

    private bool QueueSurfaceTextureStreamStart()
    {
        if (_gl == null || _oesTextureId == 0)
        {
            return false;
        }

        _surfaceTextureStartPending = true;
        RequestNextFrameRendering();
        return true;
    }

    private void StartPendingSurfaceTextureStream()
    {
        if (!_surfaceTextureStartPending)
        {
            return;
        }

        _surfaceTextureStartPending = false;
        try
        {
            var version = _streamVersion;
            _useSurfaceTextureRendering = true;
            _currentPixelFormat = RtspNativePixelFormat.AndroidSurfaceTexture;
            _surfaceTextureContextInvalid = false;
            ResetSurfaceTextureLatch();
            InitializeTextureParameters(_oesTextureId, GlTextureExternalOes);
            _surfaceTexture = new AndroidSurfaceTexture((int)_oesTextureId);
            _surfaceTextureAttachedToGlContext = true;
            var frameListener = new SurfaceTextureFrameAvailableListener(this, version);
            _surfaceTextureFrameListener = frameListener;
            _surfaceTexture.SetOnFrameAvailableListener(frameListener);

            var client = new AndroidSurfaceTextureRtspClient(StreamUrl, _surfaceTexture, CreateStreamOptions());
            _surfaceTextureClient = client;
            AttachSurfaceTextureClient(client, version);
            client.Start();
        }
        catch (Exception ex)
        {
            _useSurfaceTextureRendering = false;
            if (FallbackToSoftwareDecoding)
            {
                ReportRecoverableRenderIssue("Failed to start Android SurfaceTexture RTSP path; falling back to FFmpeg upload path.", ex);
                StartFfmpegStreamFallback(_streamVersion);
            }
            else
            {
                ReportRenderError("Failed to start Android SurfaceTexture RTSP path.", ex);
            }
        }
    }

    private void AttachSurfaceTextureClient(AndroidSurfaceTextureRtspClient client, int version)
    {
        client.StreamError += (sender, e) =>
        {
            if (version != _streamVersion)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (version != _streamVersion)
                {
                    return;
                }

                LastError = e.Error;
                StreamError?.Invoke(this, e);
                if (e.Error.WillRetry)
                {
                    StartFfmpegStreamFallback(version);
                }
                else
                {
                    ConnectionState = RtspConnectionState.Failed;
                }
            }, DispatcherPriority.Background);
        };
        client.ConnectionStateChanged += (_, e) =>
        {
            if (version == _streamVersion)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version == _streamVersion)
                    {
                        ConnectionState = e.NewState;
                    }
                }, DispatcherPriority.Background);
            }
        };
        client.HardwareAccelerationChanged += (_, active) =>
        {
            if (version == _streamVersion)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version == _streamVersion)
                    {
                        IsHardwareAccelerationActive = active;
                        _streamPerformanceSummary = client.HardwareDiagnostics;
                        RefreshPerformanceSummary();
                    }
                }, DispatcherPriority.Background);
            }
        };
        client.PerformanceUpdated += (_, snapshot) =>
        {
            if (version != _streamVersion)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (version != _streamVersion)
                {
                    return;
                }

                _streamPerformanceSummary =
                    $"{client.HardwareDiagnostics} | wait {snapshot.ReadMilliseconds:F1}ms | cpu {snapshot.PipelineCpuMilliseconds:F1}ms | codec {snapshot.CodecMilliseconds:F1}ms | transfer 0.0ms | convert 0.0ms | dispatch 0.0ms";
                RefreshPerformanceSummary();
            }, DispatcherPriority.Background);
        };
        client.VideoSizeChanged += (_, e) =>
        {
            if (version != _streamVersion)
            {
                return;
            }

            lock (_frameLock)
            {
                _frameWidth = e.Width;
                _frameHeight = e.Height;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (version == _streamVersion)
                {
                    RequestNextFrameRendering();
                }
            }, DispatcherPriority.Render);
        };
    }

    private Task ReleaseSurfaceTextureClient()
    {
        var client = _surfaceTextureClient;
        var surfaceTexture = _surfaceTexture;
        _surfaceTextureClient = null;
        _surfaceTexture = null;
        _surfaceTextureFrameListener = null;
        _surfaceTextureAttachedToGlContext = false;
        _surfaceTextureContextInvalid = false;
        ResetSurfaceTextureLatch();

        if (client == null)
        {
            if (surfaceTexture != null)
            {
                ReleaseSurfaceTexture(surfaceTexture);
            }

            return _surfaceTextureCleanup;
        }

        client.Stop();
        _surfaceTextureCleanup = DisposeSurfaceTextureResourcesAsync(client, surfaceTexture);
        return _surfaceTextureCleanup;
    }

    private static Task DisposeSurfaceTextureResourcesAsync(
        AndroidSurfaceTextureRtspClient client,
        AndroidSurfaceTexture? surfaceTexture)
    {
        return client.Completion.ContinueWith(_ =>
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to dispose Android SurfaceTexture RTSP client: {ex}");
            }
            finally
            {
                if (surfaceTexture != null)
                {
                    ReleaseSurfaceTexture(surfaceTexture);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void ReleaseSurfaceTexture(AndroidSurfaceTexture surfaceTexture)
    {
        try
        {
            surfaceTexture.Release();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to release Android SurfaceTexture: {ex}");
        }
        finally
        {
            surfaceTexture.Dispose();
        }
    }

    private void StartFfmpegStreamFallback(int version)
    {
        if (version != _streamVersion || _streamClient != null)
        {
            return;
        }

        version = ++_streamVersion;
        var cleanup = ReleaseSurfaceTextureClient();
        _useSurfaceTextureRendering = false;
        _hasLatchedSurfaceTextureFrame = false;
        _currentPixelFormat = RtspNativePixelFormat.Bgra32;
        _uploadedWidth = 0;
        _uploadedHeight = 0;
        _uploadedPixelFormat = null;
        ConnectionState = RtspConnectionState.Reconnecting;

        if (cleanup.IsCompletedSuccessfully)
        {
            StartFfmpegFallbackClient(version);
            return;
        }

        StartFfmpegFallbackAfterCleanup(cleanup, version);
    }

    private void StartFfmpegFallbackAfterCleanup(Task cleanup, int version)
    {
        _ = cleanup.ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(
                () => StartFfmpegFallbackClient(version),
                DispatcherPriority.Background);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StartFfmpegFallbackClient(int version)
    {
        if (version != _streamVersion ||
            _streamClient != null ||
            !_isAttached ||
            !IsPlaybackEnabled)
        {
            return;
        }

        var client = new RtspStreamClient(StreamUrl, CreateStreamOptions());
        _streamClient = client;
        AttachStreamClient(client, version);
        client.Start();
    }

    private void UpdateSurfaceTextureFrame()
    {
        var frameListener = _surfaceTextureFrameListener;
        if (!_useSurfaceTextureRendering ||
            _surfaceTextureContextInvalid ||
            !_surfaceTextureAttachedToGlContext ||
            _surfaceTexture == null ||
            frameListener == null ||
            !frameListener.TryConsumePendingFrame())
        {
            return;
        }

        try
        {
            _surfaceTexture.UpdateTexImage();
            lock (_frameLock)
            {
                _hasLatchedSurfaceTextureFrame = true;
                _uploadedWidth = Math.Max(
                    1,
                    _frameWidth > 0 ? _frameWidth : (int)Bounds.Width);
                _uploadedHeight = Math.Max(
                    1,
                    _frameHeight > 0 ? _frameHeight : (int)Bounds.Height);
                _uploadedPixelFormat = RtspNativePixelFormat.AndroidSurfaceTexture;
            }
        }
        catch (Exception ex)
        {
            _surfaceTextureContextInvalid = true;
            ResetSurfaceTextureLatch();
            ReportRecoverableRenderIssue(
                "Failed to latch Android SurfaceTexture frame after a GL context change.",
                ex);
            QueueRestartStream();
        }
    }

    private sealed class SurfaceTextureFrameAvailableListener(
        DesktopOpenGlRtspVideoView owner,
        int version) : Java.Lang.Object, AndroidSurfaceTexture.IOnFrameAvailableListener
    {
        private int _framePending;

        public bool TryConsumePendingFrame() =>
            Interlocked.Exchange(ref _framePending, 0) != 0;

        public void ClearPendingFrame() =>
            Interlocked.Exchange(ref _framePending, 0);

        public void OnFrameAvailable(AndroidSurfaceTexture? surfaceTexture)
        {
            if (!owner.IsCurrentStreamVersion(version) ||
                !ReferenceEquals(owner._surfaceTextureFrameListener, this))
            {
                return;
            }

            Interlocked.Exchange(ref _framePending, 1);
            if (!owner._isVisuallyAttached)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (owner.IsCurrentStreamVersion(version) &&
                    ReferenceEquals(owner._surfaceTextureFrameListener, this) &&
                    owner._isVisuallyAttached)
                {
                    owner.RequestNextFrameRendering();
                }
            }, DispatcherPriority.Render);
        }
    }
#endif

    protected virtual RtspStreamOptions CreateStreamOptions()
    {
        var effectiveRenderMode = GetEffectiveRenderMode();

        // On Windows, hardware decoding (D3D11VA) is only beneficial when the D3D11 direct
        // render path is active (zero-copy shared texture). When using NativeSurface (OpenGL),
        // hardware decoding causes a wasteful GPU→CPU→GPU round-trip:
        //   D3D11VA decode → av_hwframe_transfer_data (GPU→CPU) → GL texture upload (CPU→GPU)
        // This doubles GPU memory (D3D11VA surface pool + GL textures) with no performance gain.
        // On Linux, NativeSurface + VAAPI is fine (DMA-BUF interop or efficient transfer).
        var resolvedHardwareAccelerationMode =
            RtspPlaybackConfiguration.ResolveHardwareAccelerationMode(HardwareAccelerationMode, UseHardwareAcceleration);
        var useHardwareAcceleration =
            RtspPlaybackConfiguration.ResolveUseHardwareAcceleration(
                resolvedHardwareAccelerationMode,
                UseHardwareAcceleration,
                effectiveRenderMode);

        return new RtspStreamOptions
        {
            UseHardwareAcceleration = useHardwareAcceleration,
            HardwareAccelerationMode = resolvedHardwareAccelerationMode,
            RenderMode = effectiveRenderMode,
            FallbackToSoftwareDecoding = FallbackToSoftwareDecoding,
            MaxFramesPerSecond = MaxFramesPerSecond,
            Transport = Transport,
            OpenTimeoutMilliseconds = OpenTimeoutMilliseconds,
            EndpointProbeTimeoutMilliseconds = EndpointProbeTimeoutMilliseconds,
            ReadTimeoutMilliseconds = ReadTimeoutMilliseconds,
            ReconnectDelayMilliseconds = ReconnectDelayMilliseconds,
            MaxConcurrentOpenStreams = MaxConcurrentOpenStreams,
            MaxVideoWidth = MaxVideoWidth,
            MaxVideoHeight = MaxVideoHeight,
            LowLatency = LowLatency,
            EnableAudio = EnableAudio,
            Volume = Volume,
            IsMuted = IsMuted,
            // The OpenGL fragment shaders already output alpha = 1.0 for all pixel formats,
            // so the per-pixel CPU loop in ForceOpaqueAlpha is redundant on this path.
            ForceOpaqueAlpha = false,
            ScaleQuality = ScaleQuality
        };
    }

    protected virtual void AttachStreamClient(RtspStreamClient client, int version)
    {
        client.SetFrameDeliveryEnabled(_isVisuallyAttached);
        client.OnFrameReceived += (buffer, width, height, stride) =>
        {
            OnFrameReceivedCore(version, buffer, width, height, stride);
        };
        client.OnFrameLeaseReceived += lease =>
        {
            OnFrameLeaseReceivedCore(version, lease);
        };
        client.StreamError += (sender, e) =>
        {
            if (version == _streamVersion)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version != _streamVersion)
                    {
                        return;
                    }

                    LastError = e.Error;
                    StreamError?.Invoke(this, e);
                    ConnectionState = e.Error.WillRetry ? RtspConnectionState.Reconnecting : RtspConnectionState.Failed;
                }, DispatcherPriority.Background);
            }
        };
        client.ConnectionStateChanged += (_, e) =>
        {
            if (version == _streamVersion)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version == _streamVersion)
                    {
                        ConnectionState = e.NewState;
                    }
                }, DispatcherPriority.Background);
            }
        };
        client.HardwareAccelerationChanged += (_, active) =>
        {
            if (version == _streamVersion)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version == _streamVersion)
                    {
                        IsHardwareAccelerationActive = active;
                    }
                }, DispatcherPriority.Background);
            }
        };
        client.PerformanceUpdated += (_, snapshot) =>
        {
            if (version == _streamVersion)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version != _streamVersion)
                    {
                        return;
                    }

                    _streamPerformanceSummary =
                        $"wait {snapshot.ReadMilliseconds:F1}ms | cpu {snapshot.PipelineCpuMilliseconds:F1}ms | codec {snapshot.CodecMilliseconds:F1}ms | transfer {snapshot.HardwareTransferMilliseconds:F1}ms | convert {snapshot.ConvertMilliseconds:F1}ms | dispatch {snapshot.DispatchMilliseconds:F1}ms";
                    RefreshPerformanceSummary();
                }, DispatcherPriority.Background);
            }
        };
    }

    protected virtual void OnFrameReceivedCore(int version, IntPtr buffer, int width, int height, int stride)
    {
        if (!IsCurrentStreamVersion(version) || !_isVisuallyAttached)
        {
            return;
        }

        lock (_frameLock)
        {
            if (!IsCurrentStreamVersion(version) || !_isVisuallyAttached)
            {
                return;
            }

            UpdateFrame(buffer, width, height, stride);
        }
    }

    protected virtual void OnFrameLeaseReceivedCore(int version, RtspFrameLease lease)
    {
        if (!IsCurrentStreamVersion(version) || !_isVisuallyAttached)
        {
            lease.Dispose();
            return;
        }

        lock (_frameLock)
        {
            if (!IsCurrentStreamVersion(version) || !_isVisuallyAttached)
            {
                lease.Dispose();
                return;
            }

            UpdateFrameLease(lease);
        }
    }

    protected void UpdateFrame(IntPtr buffer, int width, int height, int stride)
    {
        var copyStart = Stopwatch.GetTimestamp();
        var packedStride = width * 4;
        var requiredSize = packedStride * height;
        var canUploadBgraDirectly = RtspOpenGlFrameUtilities.CanUploadBgraDirectly();
        lock (_frameLock)
        {
            if (_frameBuffer == IntPtr.Zero || _frameBufferSize != requiredSize)
            {
                if (_frameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_frameBuffer);
                }

                _frameBuffer = Marshal.AllocHGlobal(requiredSize);
                _frameBufferSize = requiredSize;
            }

            if (canUploadBgraDirectly)
            {
                RtspOpenGlFrameUtilities.CopyFrameRows(buffer, _frameBuffer, height, stride, packedStride);
            }
            else
            {
                RtspOpenGlFrameUtilities.CopyBgraToRgba(buffer, _frameBuffer, width, height, stride, packedStride);
            }

            RtspOpenGlFrameUtilities.ApplyOpaqueAlpha(_frameBuffer, width, height, packedStride);

            _frameWidth = width;
            _frameHeight = height;
            _currentPixelFormat = RtspNativePixelFormat.Bgra32;
            _hasNewFrame = true;
        }

        RecordCopyTiming(Stopwatch.GetTimestamp() - copyStart);

        Dispatcher.UIThread.Post(() =>
        {
            if (_isVisuallyAttached)
            {
                RequestNextFrameRendering();
            }
        }, DispatcherPriority.Render);
    }

    protected void UpdateFrameLease(RtspFrameLease lease)
    {
        lock (_frameLock)
        {
            var previousLease = _pendingLease;
            _pendingLease = lease;
            _frameWidth = lease.Width;
            _frameHeight = lease.Height;
            _currentPixelFormat = lease.PixelFormat;
            _hasNewFrame = true;
            previousLease?.Dispose();
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isVisuallyAttached)
            {
                RequestNextFrameRendering();
            }
        }, DispatcherPriority.Render);
    }

    private void UploadPendingFrame()
    {
        if (_gl == null)
        {
            return;
        }

        lock (_frameLock)
        {
            var lease = _pendingLease;
            var buffer = lease?.Buffer ?? _frameBuffer;
            if (!_hasNewFrame || !HasValidUploadSource(lease, buffer) || _frameWidth <= 0 || _frameHeight <= 0)
            {
                return;
            }

            EnsureSoftwareGlResourcesInitialized();
            var uploadStart = Stopwatch.GetTimestamp();
            UploadFrameTextures(buffer, lease);

            _hasNewFrame = false;
            ReleasePendingLease();
            RecordUploadTiming(Stopwatch.GetTimestamp() - uploadStart);
        }
    }

    private static bool HasValidUploadSource(RtspFrameLease? lease, IntPtr fallbackBuffer)
    {
        if (lease == null)
        {
            return fallbackBuffer != IntPtr.Zero;
        }

        return lease.PixelFormat switch
        {
            RtspNativePixelFormat.Yuv420P => lease.Plane0Pointer != IntPtr.Zero &&
                                             lease.Plane1Pointer != IntPtr.Zero &&
                                             lease.Plane2Pointer != IntPtr.Zero,
            RtspNativePixelFormat.Nv12 => lease.Plane0Pointer != IntPtr.Zero &&
                                          lease.Plane1Pointer != IntPtr.Zero,
            _ => lease.Plane0Pointer != IntPtr.Zero || lease.Buffer != IntPtr.Zero || fallbackBuffer != IntPtr.Zero
        };
    }

    private void InitializePrograms(bool includeSoftwarePrograms)
    {
        if (_gl == null)
        {
            return;
        }

        string versionHeader = _isGles ? "#version 100\n" : (_isCoreProfile ? "#version 330 core\n" : "#version 120\n");
        string attributeKeyword = _isCoreProfile ? "in" : "attribute";
        string varyingKeywordVertex = _isCoreProfile ? "out" : "varying";
        string varyingKeywordFragment = _isCoreProfile ? "in" : "varying";
        string fragmentOutputDeclaration = _isCoreProfile ? "out vec4 fragColor;\n" : "";
        string fragmentOutputName = _isCoreProfile ? "fragColor" : "gl_FragColor";
        string textureFunction = _isCoreProfile ? "texture" : "texture2D";
        string precisionHeader = _isGles || _isCoreProfile ? "precision mediump float;\n" : "";
#if ANDROID
        string oesExtensionHeader = _isGles ? "#extension GL_OES_EGL_image_external : require\n" : "";
#endif

        string vertexShaderSource = $$"""
            {{versionHeader}}
            {{attributeKeyword}} vec2 aPosition;
            {{attributeKeyword}} vec2 aTexCoord;
            {{varyingKeywordVertex}} vec2 vTexCoord;
            void main()
            {
                gl_Position = vec4(aPosition, 0.0, 1.0);
                vTexCoord = aTexCoord;
            }
            """;

        string bgraFragmentShaderSource = $$"""
            {{versionHeader}}
            {{precisionHeader}}
            {{varyingKeywordFragment}} vec2 vTexCoord;
            uniform sampler2D uTexture;
            {{fragmentOutputDeclaration}}
            void main()
            {
                {{fragmentOutputName}} = vec4({{textureFunction}}(uTexture, vTexCoord).rgb, 1.0);
            }
            """;

        string yuv420FragmentShaderSource = $$"""
            {{versionHeader}}
            {{precisionHeader}}
            {{varyingKeywordFragment}} vec2 vTexCoord;
            uniform sampler2D uTextureY;
            uniform sampler2D uTextureU;
            uniform sampler2D uTextureV;
            {{fragmentOutputDeclaration}}
            vec3 yuvToRgb(float y, float u, float v)
            {
                float c = y - 0.0625;
                float d = u - 0.5;
                float e = v - 0.5;
                return vec3(
                    1.1643 * c + 1.5958 * e,
                    1.1643 * c - 0.39173 * d - 0.81290 * e,
                    1.1643 * c + 2.017 * d
                );
            }
            void main()
            {
                float y = {{textureFunction}}(uTextureY, vTexCoord).r;
                float u = {{textureFunction}}(uTextureU, vTexCoord).r;
                float v = {{textureFunction}}(uTextureV, vTexCoord).r;
                {{fragmentOutputName}} = vec4(yuvToRgb(y, u, v), 1.0);
            }
            """;

        string nv12FragmentShaderSource = $$"""
            {{versionHeader}}
            {{precisionHeader}}
            {{varyingKeywordFragment}} vec2 vTexCoord;
            uniform sampler2D uTextureY;
            uniform sampler2D uTextureUV;
            {{fragmentOutputDeclaration}}
            vec3 yuvToRgb(float y, float u, float v)
            {
                float c = y - 0.0625;
                float d = u - 0.5;
                float e = v - 0.5;
                return vec3(
                    1.1643 * c + 1.5958 * e,
                    1.1643 * c - 0.39173 * d - 0.81290 * e,
                    1.1643 * c + 2.017 * d
                );
            }
            void main()
            {
                float y = {{textureFunction}}(uTextureY, vTexCoord).r;
                vec2 uv = {{textureFunction}}(uTextureUV, vTexCoord).rg;
                {{fragmentOutputName}} = vec4(yuvToRgb(y, uv.x, uv.y), 1.0);
            }
            """;

        if (_vertexShader == 0)
        {
            _vertexShader = CompileShader(GlVertexShader, vertexShaderSource);
        }

        // NV21 shader: same as NV12 but with U and V (uv.x and uv.y) swapped,
        // because NV21 interleaves chroma as V-U instead of U-V.
        string nv21FragmentShaderSource = $$"""
            {{versionHeader}}
            {{precisionHeader}}
            {{varyingKeywordFragment}} vec2 vTexCoord;
            uniform sampler2D uTextureY;
            uniform sampler2D uTextureUV;
            {{fragmentOutputDeclaration}}
            vec3 yuvToRgb(float y, float u, float v)
            {
                float c = y - 0.0625;
                float d = u - 0.5;
                float e = v - 0.5;
                return vec3(
                    1.1643 * c + 1.5958 * e,
                    1.1643 * c - 0.39173 * d - 0.81290 * e,
                    1.1643 * c + 2.017 * d
                );
            }
            void main()
            {
                float y = {{textureFunction}}(uTextureY, vTexCoord).r;
                vec2 uv = {{textureFunction}}(uTextureUV, vTexCoord).rg;
                // NV21: first byte is V, second byte is U — swap relative to NV12
                {{fragmentOutputName}} = vec4(yuvToRgb(y, uv.y, uv.x), 1.0);
            }
            """;
#if ANDROID
        string oesFragmentShaderSource = $$"""
            {{versionHeader}}
            {{oesExtensionHeader}}
            {{precisionHeader}}
            {{varyingKeywordFragment}} vec2 vTexCoord;
            uniform samplerExternalOES uOesTexture;
            {{fragmentOutputDeclaration}}
            void main()
            {
                {{fragmentOutputName}} = vec4({{textureFunction}}(uOesTexture, vTexCoord).rgb, 1.0);
            }
            """;
        if (_oesFragmentShader == 0)
        {
            _oesFragmentShader = CompileShader(GlFragmentShader, oesFragmentShaderSource);
        }
#endif

        if (includeSoftwarePrograms)
        {
            _bgraFragmentShader = _bgraFragmentShader == 0
                ? CompileShader(GlFragmentShader, bgraFragmentShaderSource)
                : _bgraFragmentShader;
            _yuv420FragmentShader = _yuv420FragmentShader == 0
                ? CompileShader(GlFragmentShader, yuv420FragmentShaderSource)
                : _yuv420FragmentShader;
            _nv12FragmentShader = _nv12FragmentShader == 0
                ? CompileShader(GlFragmentShader, nv12FragmentShaderSource)
                : _nv12FragmentShader;
            _nv21FragmentShader = _nv21FragmentShader == 0
                ? CompileShader(GlFragmentShader, nv21FragmentShaderSource)
                : _nv21FragmentShader;

            _bgraProgram = _bgraProgram == 0
                ? CreateProgram(_vertexShader, _bgraFragmentShader)
                : _bgraProgram;
            _yuv420Program = _yuv420Program == 0
                ? CreateProgram(_vertexShader, _yuv420FragmentShader)
                : _yuv420Program;
            _nv12Program = _nv12Program == 0
                ? CreateProgram(_vertexShader, _nv12FragmentShader)
                : _nv12Program;
            _nv21Program = _nv21Program == 0
                ? CreateProgram(_vertexShader, _nv21FragmentShader)
                : _nv21Program;

            _bgraBindings = CreateProgramBindings(_bgraProgram, TextureSamplerName, null, null);
            _yuv420Bindings = CreateProgramBindings(_yuv420Program, TextureYSamplerName, TextureUSamplerName, TextureVSamplerName);
            _nv12Bindings = CreateProgramBindings(_nv12Program, TextureYSamplerName, TextureUvSamplerName, null);
            _nv21Bindings = CreateProgramBindings(_nv21Program, TextureYSamplerName, TextureUvSamplerName, null);
        }
#if ANDROID
        _oesProgram = _oesProgram == 0
            ? CreateProgram(_vertexShader, _oesFragmentShader)
            : _oesProgram;
        _oesBindings = CreateProgramBindings(_oesProgram, OesTextureSamplerName, null, null);
#endif
    }

    private uint CompileShader(uint type, string source)
    {
        if (_gl == null)
        {
            return 0;
        }

        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        if (_gl.GetShaderStatus(shader, GlCompileStatus) == 0)
        {
            throw new InvalidOperationException($"Failed to compile OpenGL shader: {_gl.GetShaderInfoLog(shader)}");
        }

        return shader;
    }

    private uint CreateProgram(uint vertexShader, uint fragmentShader)
    {
        if (_gl == null)
        {
            return 0;
        }

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vertexShader);
        _gl.AttachShader(program, fragmentShader);
        _gl.LinkProgram(program);

        if (_gl.GetProgramStatus(program, GlLinkStatus) == 0)
        {
            throw new InvalidOperationException($"Failed to link OpenGL program: {_gl.GetProgramInfoLog(program)}");
        }

        return program;
    }

    private ProgramBindings CreateProgramBindings(uint program, byte[] sampler0Name, byte[]? sampler1Name, byte[]? sampler2Name)
    {
        if (_gl == null)
        {
            return default;
        }

        var bindings = new ProgramBindings
        {
            Program = program,
            PositionLocation = _gl.GetAttribLocation(program, PositionName),
            TexCoordLocation = _gl.GetAttribLocation(program, TexCoordName),
            Sampler0Location = _gl.GetUniformLocation(program, sampler0Name),
            Sampler1Location = sampler1Name == null ? -1 : _gl.GetUniformLocation(program, sampler1Name),
            Sampler2Location = sampler2Name == null ? -1 : _gl.GetUniformLocation(program, sampler2Name)
        };

        if (bindings.PositionLocation < 0 || bindings.TexCoordLocation < 0 || bindings.Sampler0Location < 0)
        {
            throw new InvalidOperationException(
                $"OpenGL shader locations are invalid. Position={bindings.PositionLocation}, TexCoord={bindings.TexCoordLocation}, Sampler0={bindings.Sampler0Location}");
        }

        return bindings;
    }

    private void EnsureGlResourcesInitialized()
    {
        if (_gl == null)
        {
            return;
        }

#if ANDROID
        if (_oesProgram != 0 && _oesTextureId != 0 && _vertexBufferId != 0)
#else
        if (_bgraProgram != 0 && _textureId != 0 && _vertexBufferId != 0)
#endif
        {
            return;
        }

#if ANDROID
        InitializePrograms(includeSoftwarePrograms: true);
        InitializeTexture(includeSoftwareTextures: true);
#else
        InitializePrograms(includeSoftwarePrograms: true);
        InitializeTexture(includeSoftwareTextures: true);
#endif
        InitializeVertexBuffer();
    }

    private void EnsureSoftwareGlResourcesInitialized()
    {
        if (_gl == null || (_bgraProgram != 0 && _textureId != 0))
        {
            return;
        }

        InitializePrograms(includeSoftwarePrograms: true);
        InitializeTexture(includeSoftwareTextures: true);
    }

    private void InitializeTexture(bool includeSoftwareTextures)
    {
        if (_gl == null)
        {
            return;
        }

        if (includeSoftwareTextures)
        {
            if (_textureId == 0)
            {
                _textureId = _gl.GenTexture();
                InitializeTextureParameters(_textureId);
            }

            if (_textureId2 == 0)
            {
                _textureId2 = _gl.GenTexture();
                InitializeTextureParameters(_textureId2);
            }

            if (_textureId3 == 0)
            {
                _textureId3 = _gl.GenTexture();
                InitializeTextureParameters(_textureId3);
            }
        }
#if ANDROID
        if (_oesTextureId == 0)
        {
            _oesTextureId = _gl.GenTexture();
        }
#endif
    }

    private void InitializeTextureParameters(uint textureId, uint textureTarget = GlTexture2D)
    {
        if (_gl == null || textureId == 0)
        {
            return;
        }

        _gl.BindTexture(textureTarget, textureId);
        _gl.TexParameteri(textureTarget, GlTextureMinFilter, (int)GlLinear);
        _gl.TexParameteri(textureTarget, GlTextureMagFilter, (int)GlLinear);
        _gl.TexParameteri(textureTarget, GlTextureWrapS, GlClampToEdge);
        _gl.TexParameteri(textureTarget, GlTextureWrapT, GlClampToEdge);
    }

    private void InitializeVertexBuffer()
    {
        if (_gl == null || _vertexBufferId != 0)
        {
            return;
        }

        ReadOnlySpan<float> vertices =
        [
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
            -1f,  1f, 0f, 0f,
             1f,  1f, 1f, 0f
        ];

        if (RtspOpenGlFrameUtilities.UsesVertexArrayObject())
        {
            _vertexArrayId = _gl.GenVertexArray();
            _gl.BindVertexArray(_vertexArrayId);
        }

        _vertexBufferId = _gl.GenBuffer();
        _gl.BindBuffer(GlArrayBuffer, _vertexBufferId);
        fixed (float* vertexPtr = vertices)
        {
            _gl.BufferData(GlArrayBuffer, vertices.Length * sizeof(float), vertexPtr, GlStaticDraw);
        }

#if ANDROID
        var vertexBindings = _oesBindings;
#else
        var vertexBindings = _bgraBindings;
#endif
        _gl.EnableVertexAttribArray((uint)vertexBindings.PositionLocation);
        _gl.EnableVertexAttribArray((uint)vertexBindings.TexCoordLocation);
        _gl.VertexAttribPointer((uint)vertexBindings.PositionLocation, 2, GlFloat, false, 4 * sizeof(float), (void*)0);
        _gl.VertexAttribPointer((uint)vertexBindings.TexCoordLocation, 2, GlFloat, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        _gl.BindBuffer(GlArrayBuffer, 0);
        if (RtspOpenGlFrameUtilities.UsesVertexArrayObject())
        {
            _gl.BindVertexArray(0);
        }
    }

    private void DestroyGlResources()
    {
        if (_gl == null)
        {
            return;
        }

        if (_vertexBufferId != 0)
        {
            _gl.DeleteBuffer(_vertexBufferId);
            _vertexBufferId = 0;
        }

        if (_vertexArrayId != 0)
        {
            _gl.DeleteVertexArray(_vertexArrayId);
            _vertexArrayId = 0;
        }

        if (_textureId != 0)
        {
            _gl.DeleteTexture(_textureId);
            _textureId = 0;
        }

        if (_textureId2 != 0)
        {
            _gl.DeleteTexture(_textureId2);
            _textureId2 = 0;
        }

        if (_textureId3 != 0)
        {
            _gl.DeleteTexture(_textureId3);
            _textureId3 = 0;
        }

#if ANDROID
        if (_oesTextureId != 0)
        {
            _gl.DeleteTexture(_oesTextureId);
            _oesTextureId = 0;
        }
#endif

        if (_bgraProgram != 0)
        {
            _gl.DeleteProgram(_bgraProgram);
            _bgraProgram = 0;
        }

        if (_yuv420Program != 0)
        {
            _gl.DeleteProgram(_yuv420Program);
            _yuv420Program = 0;
        }

        if (_nv12Program != 0)
        {
            _gl.DeleteProgram(_nv12Program);
            _nv12Program = 0;
        }

        if (_nv21Program != 0)
        {
            _gl.DeleteProgram(_nv21Program);
            _nv21Program = 0;
        }

#if ANDROID
        if (_oesProgram != 0)
        {
            _gl.DeleteProgram(_oesProgram);
            _oesProgram = 0;
        }
#endif

        if (_vertexShader != 0)
        {
            _gl.DeleteShader(_vertexShader);
            _vertexShader = 0;
        }

        if (_bgraFragmentShader != 0)
        {
            _gl.DeleteShader(_bgraFragmentShader);
            _bgraFragmentShader = 0;
        }

        if (_yuv420FragmentShader != 0)
        {
            _gl.DeleteShader(_yuv420FragmentShader);
            _yuv420FragmentShader = 0;
        }

        if (_nv12FragmentShader != 0)
        {
            _gl.DeleteShader(_nv12FragmentShader);
            _nv12FragmentShader = 0;
        }

        if (_nv21FragmentShader != 0)
        {
            _gl.DeleteShader(_nv21FragmentShader);
            _nv21FragmentShader = 0;
        }

#if ANDROID
        if (_oesFragmentShader != 0)
        {
            _gl.DeleteShader(_oesFragmentShader);
            _oesFragmentShader = 0;
        }
#endif
    }

    private void FreeFrameBuffer()
    {
        lock (_frameLock)
        {
            if (_frameBuffer == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeHGlobal(_frameBuffer);
            _frameBuffer = IntPtr.Zero;
            _frameBufferSize = 0;
            _frameWidth = 0;
            _frameHeight = 0;
            _uploadedPixelFormat = null;
            _hasNewFrame = false;
        }
    }

    private void ReleasePendingLease()
    {
        lock (_frameLock)
        {
            _pendingLease?.Dispose();
            _pendingLease = null;
        }
    }

    private void RecordCopyTiming(long elapsedTicks)
    {
        _copyTotalTicks += elapsedTicks;
        _copySamples++;
        if (_copySamples < 30)
        {
            return;
        }

        var averageMs = _copyTotalTicks * 1000d / Stopwatch.Frequency / _copySamples;
        _copyTotalTicks = 0;
        _copySamples = 0;
        UpdateRenderPerformance(copyMs: averageMs, uploadMs: null);
    }

    private void RecordUploadTiming(long elapsedTicks)
    {
        _uploadTotalTicks += elapsedTicks;
        _uploadSamples++;
        if (_uploadSamples < 30)
        {
            return;
        }

        var averageMs = _uploadTotalTicks * 1000d / Stopwatch.Frequency / _uploadSamples;
        _uploadTotalTicks = 0;
        _uploadSamples = 0;
        UpdateRenderPerformance(copyMs: null, uploadMs: averageMs);
    }

    private double? _copyAverageMilliseconds;
    private double? _uploadAverageMilliseconds;

    private void UpdateRenderPerformance(double? copyMs, double? uploadMs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (copyMs.HasValue)
            {
                _copyAverageMilliseconds = copyMs.Value;
            }

            if (uploadMs.HasValue)
            {
                _uploadAverageMilliseconds = uploadMs.Value;
            }

            if (_copyAverageMilliseconds.HasValue || _uploadAverageMilliseconds.HasValue)
            {
                _renderPerformanceSummary =
                    $"copy {_copyAverageMilliseconds.GetValueOrDefault():F1}ms | upload {_uploadAverageMilliseconds.GetValueOrDefault():F1}ms";
            }
            else
            {
                _renderPerformanceSummary = string.Empty;
            }

            RefreshPerformanceSummary();
        }, DispatcherPriority.Background);
    }

    private void RefreshPerformanceSummary()
    {
        var renderPath = _uploadedPixelFormat switch
        {
            RtspNativePixelFormat.VaapiDmaBuf => "[zero-copy]",
            RtspNativePixelFormat.Nv12 => "[NV12]",
            RtspNativePixelFormat.Nv21 => "[NV21]",
            RtspNativePixelFormat.Yuv420P => "[YUV420P]",
            RtspNativePixelFormat.Bgra32 => "[BGRA]",
            _ => ""
        };

        var parts = renderPath;

        if (!string.IsNullOrEmpty(_streamPerformanceSummary))
        {
            parts = string.IsNullOrEmpty(parts)
                ? _streamPerformanceSummary
                : $"{parts} {_streamPerformanceSummary}";
        }

        if (!string.IsNullOrEmpty(_renderPerformanceSummary))
        {
            parts = string.IsNullOrEmpty(parts)
                ? _renderPerformanceSummary
                : $"{parts} | {_renderPerformanceSummary}";
        }

        PerformanceSummary = parts;
    }

    private void SetConnectionState(RtspConnectionState state)
    {
        var oldState = _connectionState;
        if (oldState == state)
        {
            return;
        }

        SetAndRaise(ConnectionStateProperty, ref _connectionState, state);
        if (state == RtspConnectionState.Connected)
        {
            LastError = null;
        }

        ConnectionStateChanged?.Invoke(this, new RtspConnectionStateChangedEventArgs(oldState, state));
    }

    protected void ReportRenderError(string message, Exception ex)
    {
        var detailedMessage = ex != null ? $"{message} ({ex.GetType().Name}: {ex.Message})" : message;
        Dispatcher.UIThread.Post(() =>
        {
            LastError = new RtspStreamError(RtspStreamErrorKind.Unknown, detailedMessage, ex, WillRetry: false);
            ConnectionState = RtspConnectionState.Failed;
            StreamError?.Invoke(this, new RtspStreamErrorEventArgs(LastError));
        }, DispatcherPriority.Background);
    }

    protected void ReportRecoverableRenderIssue(string message, Exception? ex = null)
    {
        var detailedMessage = ex != null ? $"{message} ({ex.GetType().Name}: {ex.Message})" : message;
        Dispatcher.UIThread.Post(() =>
        {
            LastError = new RtspStreamError(RtspStreamErrorKind.Unknown, detailedMessage, ex, WillRetry: true);
            StreamError?.Invoke(this, new RtspStreamErrorEventArgs(LastError));
        }, DispatcherPriority.Background);
    }

    private ProgramBindings GetActiveProgramBindings()
    {
        return _currentPixelFormat switch
        {
#if ANDROID
            RtspNativePixelFormat.AndroidSurfaceTexture => _oesBindings,
#endif
            RtspNativePixelFormat.Yuv420P => _yuv420Bindings,
            RtspNativePixelFormat.Nv12 => _nv12Bindings,
            RtspNativePixelFormat.Nv21 => _nv21Bindings,
            _ => _bgraBindings
        };
    }

    private void BindTextures(ProgramBindings bindings)
    {
        if (_gl == null)
        {
            return;
        }

#if ANDROID
        if (_currentPixelFormat == RtspNativePixelFormat.AndroidSurfaceTexture)
        {
            _gl.ActiveTexture(GlTexture0);
            _gl.BindTexture(GlTextureExternalOes, _oesTextureId);
            _gl.Uniform1i(bindings.Sampler0Location, 0);
            return;
        }
#endif

        _gl.ActiveTexture(GlTexture0);
        _gl.BindTexture(GlTexture2D, _textureId);
        _gl.Uniform1i(bindings.Sampler0Location, 0);

        if (bindings.Sampler1Location >= 0)
        {
            _gl.ActiveTexture(GlTexture1);
            _gl.BindTexture(GlTexture2D, _textureId2);
            _gl.Uniform1i(bindings.Sampler1Location, 1);
        }

        if (bindings.Sampler2Location >= 0)
        {
            _gl.ActiveTexture(GlTexture2);
            _gl.BindTexture(GlTexture2D, _textureId3);
            _gl.Uniform1i(bindings.Sampler2Location, 2);
        }
    }

    private uint GetActiveTextureId()
    {
#if ANDROID
        return _currentPixelFormat == RtspNativePixelFormat.AndroidSurfaceTexture ? _oesTextureId : _textureId;
#else
        return _textureId;
#endif
    }

    private void UploadFrameTextures(IntPtr buffer, RtspFrameLease? lease)
    {
        if (_gl == null)
        {
            return;
        }

        switch (lease?.PixelFormat ?? RtspNativePixelFormat.Bgra32)
        {
            case RtspNativePixelFormat.Yuv420P:
                UploadYuv420Textures(lease!);
                break;
            case RtspNativePixelFormat.Nv12:
                UploadNv12Textures(lease!);
                break;
            case RtspNativePixelFormat.Nv21:
                UploadNv21Textures(lease!);
                break;
            default:
                UploadBgraTexture(buffer, lease);
                break;
        }
    }

    private void UploadBgraTexture(IntPtr buffer, RtspFrameLease? lease)
    {
        if (_gl == null)
        {
            return;
        }

        _gl.BindTexture(GlTexture2D, _textureId);
        _gl.PixelStorei(GlUnpackAlignment, 1);
        var rowLengthPixels = lease?.Plane0Stride > 0 ? lease.Plane0Stride / 4 : 0;
        if (rowLengthPixels > 0)
        {
            _gl.PixelStorei(GlUnpackRowLength, rowLengthPixels);
        }

        // On Android, DestinationPixelFormat is AV_PIX_FMT_RGBA (not BGRA), so the bytes in
        // the buffer are RGBA. We must upload using GL_RGBA to avoid swapping Red and Blue.
        // On other platforms the bytes are BGRA, which GL_BGRA (0x80E1) uploads natively.
        var uploadFormat = RtspOpenGlFrameUtilities.CanUploadBgraDirectly() && !OperatingSystem.IsAndroid() ? GlBgra : GlRgba;
        if (_uploadedWidth != _frameWidth || _uploadedHeight != _frameHeight || _uploadedPixelFormat != RtspNativePixelFormat.Bgra32)
        {
            _gl.TexImage2D(GlTexture2D, 0, (int)GlRgba, _frameWidth, _frameHeight, 0, uploadFormat, GlUnsignedByte, buffer);
        }
        else
        {
            _gl.TexSubImage2D(GlTexture2D, 0, 0, 0, _frameWidth, _frameHeight, uploadFormat, GlUnsignedByte, buffer);
        }

        if (rowLengthPixels > 0)
        {
            _gl.PixelStorei(GlUnpackRowLength, 0);
        }

        _uploadedWidth = _frameWidth;
        _uploadedHeight = _frameHeight;
        _uploadedPixelFormat = RtspNativePixelFormat.Bgra32;
    }

    private void UploadYuv420Textures(RtspFrameLease lease)
    {
        if (_gl == null)
        {
            return;
        }

        var chromaWidth = (lease.Width + 1) / 2;
        var chromaHeight = (lease.Height + 1) / 2;
        UploadPlaneTexture(_textureId, lease.Width, lease.Height, GlRed, GlR8, (byte*)lease.Plane0Pointer.ToPointer(), lease.PixelFormat, lease.Plane0Stride);
        UploadPlaneTexture(_textureId2, chromaWidth, chromaHeight, GlRed, GlR8, (byte*)lease.Plane1Pointer.ToPointer(), lease.PixelFormat, lease.Plane1Stride);
        UploadPlaneTexture(_textureId3, chromaWidth, chromaHeight, GlRed, GlR8, (byte*)lease.Plane2Pointer.ToPointer(), lease.PixelFormat, lease.Plane2Stride);
        _uploadedWidth = lease.Width;
        _uploadedHeight = lease.Height;
        _uploadedPixelFormat = RtspNativePixelFormat.Yuv420P;
    }

    private void UploadNv12Textures(RtspFrameLease lease)
    {
        if (_gl == null)
        {
            return;
        }

        var chromaHeight = (lease.Height + 1) / 2;
        var chromaWidth = (lease.Width + 1) / 2;
        // Only pass rowLength when the stride differs from the texture width to avoid GLES driver issues.
        var yRowLength = lease.Plane0Stride > lease.Width ? lease.Plane0Stride : 0;
        var uvRowLength = (lease.Plane1Stride / 2) > chromaWidth ? (lease.Plane1Stride / 2) : 0;
        UploadPlaneTexture(_textureId, lease.Width, lease.Height, GlRed, GlR8, (byte*)lease.Plane0Pointer.ToPointer(), lease.PixelFormat, yRowLength);
        UploadPlaneTexture(_textureId2, chromaWidth, chromaHeight, GlRg, GlRg8, (byte*)lease.Plane1Pointer.ToPointer(), lease.PixelFormat, uvRowLength);
        _uploadedWidth = lease.Width;
        _uploadedHeight = lease.Height;
        _uploadedPixelFormat = RtspNativePixelFormat.Nv12;
    }

    private void UploadNv21Textures(RtspFrameLease lease)
    {
        if (_gl == null)
        {
            return;
        }

        // NV21 memory layout is identical to NV12 (semi-planar); only U/V order in the chroma
        // plane differs.  The UV-swap is handled in the NV21 fragment shader, not here.
        var chromaHeight = (lease.Height + 1) / 2;
        var chromaWidth = (lease.Width + 1) / 2;
        var yRowLength = lease.Plane0Stride > lease.Width ? lease.Plane0Stride : 0;
        var uvRowLength = (lease.Plane1Stride / 2) > chromaWidth ? (lease.Plane1Stride / 2) : 0;
        UploadPlaneTexture(_textureId, lease.Width, lease.Height, GlRed, GlR8, (byte*)lease.Plane0Pointer.ToPointer(), lease.PixelFormat, yRowLength);
        UploadPlaneTexture(_textureId2, chromaWidth, chromaHeight, GlRg, GlRg8, (byte*)lease.Plane1Pointer.ToPointer(), lease.PixelFormat, uvRowLength);
        _uploadedWidth = lease.Width;
        _uploadedHeight = lease.Height;
        _uploadedPixelFormat = RtspNativePixelFormat.Nv21;
    }

    private void UploadPlaneTexture(uint textureId, int width, int height, uint format, int internalFormat, byte* buffer, RtspNativePixelFormat pixelFormat, int rowLengthPixels = 0)
    {
        if (_gl == null)
        {
            return;
        }

        _gl.BindTexture(GlTexture2D, textureId);
        _gl.PixelStorei(GlUnpackAlignment, 1);
        if (rowLengthPixels > 0)
        {
            _gl.PixelStorei(GlUnpackRowLength, rowLengthPixels);
        }

        if (_uploadedWidth != _frameWidth || _uploadedHeight != _frameHeight || _uploadedPixelFormat != pixelFormat)
        {
            _gl.TexImage2D(GlTexture2D, 0, internalFormat, width, height, 0, format, GlUnsignedByte, (IntPtr)buffer);
        }
        else
        {
            _gl.TexSubImage2D(GlTexture2D, 0, 0, 0, width, height, format, GlUnsignedByte, (IntPtr)buffer);
        }

        if (rowLengthPixels > 0)
        {
            _gl.PixelStorei(GlUnpackRowLength, 0);
        }
    }

    private struct ProgramBindings
    {
        public uint Program;
        public int PositionLocation;
        public int TexCoordLocation;
        public int Sampler0Location;
        public int Sampler1Location;
        public int Sampler2Location;
    }

    private sealed class GlBindings
    {
        private readonly GlActiveTexture _activeTexture;
        private readonly GlAttachShader _attachShader;
        private readonly GlBindBuffer _bindBuffer;
        private readonly GlBindFramebuffer _bindFramebuffer;
        private readonly GlBindTexture _bindTexture;
        private readonly GlBindVertexArray _bindVertexArray;
        private readonly GlBufferData _bufferData;
        private readonly GlClear _clear;
        private readonly GlClearColor _clearColor;
        private readonly GlCreateProgram _createProgram;
        private readonly GlCreateShader _createShader;
        private readonly GlDeleteBuffers _deleteBuffers;
        private readonly GlDeleteTextures _deleteTextures;
        private readonly GlDeleteVertexArrays _deleteVertexArrays;
        private readonly GlDeleteProgram _deleteProgram;
        private readonly GlDeleteShader _deleteShader;
        private readonly GlGenBuffers _genBuffers;
        private readonly GlGenTextures _genTextures;
        private readonly GlGenVertexArrays _genVertexArrays;
        private readonly GlGetProgramInfoLog _getProgramInfoLog;
        private readonly GlGetAttribLocation _getAttribLocation;
        private readonly GlGetProgramiv _getProgramiv;
        private readonly GlGetShaderInfoLog _getShaderInfoLog;
        private readonly GlGetShaderiv _getShaderiv;
        private readonly GlGetUniformLocation _getUniformLocation;
        private readonly GlCompileShader _compileShader;
        private readonly GlEnableVertexAttribArray _enableVertexAttribArray;
        private readonly GlDisableVertexAttribArray _disableVertexAttribArray;
        private readonly GlLinkProgram _linkProgram;
        private readonly GlTexImage2D _texImage2D;
        private readonly GlPixelStorei _pixelStorei;
        private readonly GlTexSubImage2D _texSubImage2D;
        private readonly GlTexParameteri _texParameteri;
        private readonly GlUniform1i _uniform1i;
        private readonly GlUseProgram _useProgram;
        private readonly GlViewport _viewport;
        private readonly GlVertexAttribPointer _vertexAttribPointer;
        private readonly GlDrawArrays _drawArrays;
        private readonly GlShaderSource _shaderSource;
        private readonly GlGetString _getString;

        public GlBindings(GlInterface gl)
        {
            _activeTexture = Load<GlActiveTexture>(gl, "glActiveTexture");
            _attachShader = Load<GlAttachShader>(gl, "glAttachShader");
            _bindBuffer = Load<GlBindBuffer>(gl, "glBindBuffer");
            _bindFramebuffer = Load<GlBindFramebuffer>(gl, "glBindFramebuffer");
            _bindTexture = Load<GlBindTexture>(gl, "glBindTexture");
            _bindVertexArray = Load<GlBindVertexArray>(gl, "glBindVertexArray");
            _bufferData = Load<GlBufferData>(gl, "glBufferData");
            _clear = Load<GlClear>(gl, "glClear");
            _clearColor = Load<GlClearColor>(gl, "glClearColor");
            _createProgram = Load<GlCreateProgram>(gl, "glCreateProgram");
            _createShader = Load<GlCreateShader>(gl, "glCreateShader");
            _deleteBuffers = Load<GlDeleteBuffers>(gl, "glDeleteBuffers");
            _deleteTextures = Load<GlDeleteTextures>(gl, "glDeleteTextures");
            _deleteVertexArrays = Load<GlDeleteVertexArrays>(gl, "glDeleteVertexArrays");
            _deleteProgram = Load<GlDeleteProgram>(gl, "glDeleteProgram");
            _deleteShader = Load<GlDeleteShader>(gl, "glDeleteShader");
            _genBuffers = Load<GlGenBuffers>(gl, "glGenBuffers");
            _genTextures = Load<GlGenTextures>(gl, "glGenTextures");
            _genVertexArrays = Load<GlGenVertexArrays>(gl, "glGenVertexArrays");
            _getAttribLocation = Load<GlGetAttribLocation>(gl, "glGetAttribLocation");
            _getProgramInfoLog = Load<GlGetProgramInfoLog>(gl, "glGetProgramInfoLog");
            _getProgramiv = Load<GlGetProgramiv>(gl, "glGetProgramiv");
            _getShaderInfoLog = Load<GlGetShaderInfoLog>(gl, "glGetShaderInfoLog");
            _getShaderiv = Load<GlGetShaderiv>(gl, "glGetShaderiv");
            _getUniformLocation = Load<GlGetUniformLocation>(gl, "glGetUniformLocation");
            _compileShader = Load<GlCompileShader>(gl, "glCompileShader");
            _enableVertexAttribArray = Load<GlEnableVertexAttribArray>(gl, "glEnableVertexAttribArray");
            _disableVertexAttribArray = Load<GlDisableVertexAttribArray>(gl, "glDisableVertexAttribArray");
            _linkProgram = Load<GlLinkProgram>(gl, "glLinkProgram");
            _pixelStorei = Load<GlPixelStorei>(gl, "glPixelStorei");
            _texImage2D = Load<GlTexImage2D>(gl, "glTexImage2D");
            _texSubImage2D = Load<GlTexSubImage2D>(gl, "glTexSubImage2D");
            _texParameteri = Load<GlTexParameteri>(gl, "glTexParameteri");
            _uniform1i = Load<GlUniform1i>(gl, "glUniform1i");
            _useProgram = Load<GlUseProgram>(gl, "glUseProgram");
            _viewport = Load<GlViewport>(gl, "glViewport");
            _vertexAttribPointer = Load<GlVertexAttribPointer>(gl, "glVertexAttribPointer");
            _drawArrays = Load<GlDrawArrays>(gl, "glDrawArrays");
            _shaderSource = Load<GlShaderSource>(gl, "glShaderSource");
            _getString = Load<GlGetString>(gl, "glGetString");
        }

        public void ActiveTexture(uint texture) => _activeTexture(texture);
        public void AttachShader(uint program, uint shader) => _attachShader(program, shader);
        public void BindBuffer(uint target, uint buffer) => _bindBuffer(target, buffer);
        public void BindFramebuffer(uint target, uint framebuffer) => _bindFramebuffer(target, framebuffer);
        public void BindTexture(uint target, uint texture) => _bindTexture(target, texture);
        public void BindVertexArray(uint array) => _bindVertexArray(array);
        public void BufferData(uint target, int size, void* data, uint usage) => _bufferData(target, (nint)size, data, usage);
        public void Clear(uint mask) => _clear(mask);
        public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
        public uint CreateProgram() => _createProgram();
        public uint CreateShader(uint type) => _createShader(type);
        public void DeleteBuffer(uint buffer)
        {
            _deleteBuffers(1, &buffer);
        }

        public void DeleteVertexArray(uint array)
        {
            _deleteVertexArrays(1, &array);
        }

        public void DeleteProgram(uint program) => _deleteProgram(program);
        public void DeleteShader(uint shader) => _deleteShader(shader);
        public void DeleteTexture(uint texture)
        {
            _deleteTextures(1, &texture);
        }

        public uint GenTexture()
        {
            uint texture = 0;
            _genTextures(1, &texture);
            return texture;
        }

        public uint GenBuffer()
        {
            uint buffer = 0;
            _genBuffers(1, &buffer);
            return buffer;
        }

        public uint GenVertexArray()
        {
            uint array = 0;
            _genVertexArrays(1, &array);
            return array;
        }

        public int GetAttribLocation(uint program, byte[] name)
        {
            fixed (byte* ptr = name)
            {
                return _getAttribLocation(program, ptr);
            }
        }

        public string GetProgramInfoLog(uint program) => GetInfoLog(program, _getProgramInfoLog);
        public int GetProgramStatus(uint program, uint pname)
        {
            int value = 0;
            _getProgramiv(program, pname, &value);
            return value;
        }

        public string GetShaderInfoLog(uint shader) => GetInfoLog(shader, _getShaderInfoLog);
        public int GetShaderStatus(uint shader, uint pname)
        {
            int value = 0;
            _getShaderiv(shader, pname, &value);
            return value;
        }

        public int GetUniformLocation(uint program, byte[] name)
        {
            fixed (byte* ptr = name)
            {
                return _getUniformLocation(program, ptr);
            }
        }

        public void CompileShader(uint shader) => _compileShader(shader);
        public void DisableVertexAttribArray(uint index) => _disableVertexAttribArray(index);
        public void DrawArrays(uint mode, int first, int count) => _drawArrays(mode, first, count);
        public void EnableVertexAttribArray(uint index) => _enableVertexAttribArray(index);
        public void LinkProgram(uint program) => _linkProgram(program);

        public void ShaderSource(uint shader, string source)
        {
            var bytes = Encoding.ASCII.GetBytes(source + "\0");
            fixed (byte* src = bytes)
            {
                byte*[] pointers = [src];
                fixed (byte** pPointers = pointers)
                {
                    _shaderSource(shader, 1, pPointers, null);
                }
            }
        }

        public void PixelStorei(uint pname, int param) => _pixelStorei(pname, param);
        public void TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr data)
            => _texImage2D(target, level, internalFormat, width, height, border, format, type, data.ToPointer());

        public void TexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, IntPtr data)
            => _texSubImage2D(target, level, xoffset, yoffset, width, height, format, type, data.ToPointer());

        public void TexParameteri(uint target, uint pname, int value) => _texParameteri(target, pname, value);
        public void Uniform1i(int location, int value) => _uniform1i(location, value);
        public void UseProgram(uint program) => _useProgram(program);
        public void VertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, void* pointer)
            => _vertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, pointer);
        public void Viewport(int x, int y, int width, int height) => _viewport(x, y, width, height);

        public string GetString(uint name)
        {
            var ptr = _getString(name);
            return ptr == null ? string.Empty : Marshal.PtrToStringAnsi((IntPtr)ptr) ?? string.Empty;
        }

        private static T Load<T>(GlInterface gl, string name) where T : Delegate
        {
            var proc = gl.GetProcAddress(name);
            if (proc == IntPtr.Zero)
            {
                throw new InvalidOperationException($"OpenGL function '{name}' is not available.");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(proc);
        }

        private static string GetInfoLog(uint handle, GlGetProgramInfoLog callback)
        {
            byte[] buffer = new byte[1024];
            int written = 0;
            fixed (byte* ptr = buffer)
            {
                callback(handle, buffer.Length, &written, ptr);
            }

            var text = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, written));
            var terminator = text.IndexOf('\0');
            return terminator >= 0 ? text[..terminator] : text;
        }

        private static string GetInfoLog(uint handle, GlGetShaderInfoLog callback)
        {
            byte[] buffer = new byte[1024];
            int written = 0;
            fixed (byte* ptr = buffer)
            {
                callback(handle, buffer.Length, &written, ptr);
            }

            var text = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, written));
            var terminator = text.IndexOf('\0');
            return terminator >= 0 ? text[..terminator] : text;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlActiveTexture(uint texture);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlAttachShader(uint program, uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBindBuffer(uint target, uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBindFramebuffer(uint target, uint framebuffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBindTexture(uint target, uint texture);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBindVertexArray(uint array);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBufferData(uint target, nint size, void* data, uint usage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlClear(uint mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlClearColor(float r, float g, float b, float a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GlCreateProgram();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GlCreateShader(uint type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteBuffers(int count, uint* buffers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteTextures(int count, uint* textures);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteVertexArrays(int count, uint* arrays);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteProgram(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteShader(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGenBuffers(int count, uint* buffers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGenTextures(int count, uint* textures);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGenVertexArrays(int count, uint* arrays);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GlGetAttribLocation(uint program, byte* name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetProgramiv(uint program, uint pname, int* value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetProgramInfoLog(uint program, int maxLength, int* length, byte* infoLog);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetShaderiv(uint shader, uint pname, int* value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetShaderInfoLog(uint shader, int maxLength, int* length, byte* infoLog);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GlGetUniformLocation(uint program, byte* name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlCompileShader(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlEnableVertexAttribArray(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDisableVertexAttribArray(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlLinkProgram(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlPixelStorei(uint pname, int param);
        private delegate void GlTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, void* data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, void* data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlTexParameteri(uint target, uint pname, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlUniform1i(int location, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlUseProgram(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlViewport(int x, int y, int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, void* pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDrawArrays(uint mode, int first, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlShaderSource(uint shader, int count, byte** source, int* lengths);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte* GlGetString(uint name);
    }
}

