using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Java.Nio;

namespace FrameFlux.FFmpeg.Android;

internal sealed class AndroidSharedSurfaceMediaOutput :
    ISharedPlatformVideoOutput,
    IAndroidSharedSurfacePresenter
{
    private const int FrameWaitTimeoutMilliseconds = 50;

    private readonly object _targetsSync = new();
    private readonly object _frameSync = new();
    private readonly object _cpuSync = new();
    private readonly List<TargetEntry> _targets = [];
    private readonly List<TargetEntry> _retiredTargets = [];
    private readonly float[] _transformMatrix = new float[16];
    private readonly FrameAvailableListener _frameListener;

    private EGLDisplay? _eglDisplay;
    private EGLConfig? _eglConfig;
    private EGLContext? _eglContext;
    private EGLSurface? _pbufferSurface;
    private HandlerThread? _callbackThread;
    private Handler? _callbackHandler;
    private SurfaceTexture? _decoderSurfaceTexture;
    private Surface? _decoderSurface;
    private int _oesTexture;
    private int _windowProgram;
    private int _windowPosLoc;
    private int _windowTexLoc;
    private int _windowMatrixLoc;
    private int _windowSamplerLoc;
    private int _fboProgram;
    private int _fboPosLoc;
    private int _fboTexLoc;
    private int _fboMatrixLoc;
    private int _fboSamplerLoc;
    private FloatBuffer? _positionBuffer;
    private FloatBuffer? _windowTexBuffer;
    private FloatBuffer? _fboTexBuffer;
    private int _fbo;
    private int _fboTexture;
    private int _fboWidth;
    private int _fboHeight;
    private ByteBuffer? _readbackBuffer;
    private IntPtr _readbackAddress;
    private int _readbackCapacity;

    private Func<int, FfmpegMediaFrameLease>? _rentCpuLease;
    private FrameLeaseReceivedHandler? _onCpuFrameLeaseReceived;
    private bool _cpuDeliveryRequired;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _frameAvailable;
    private bool _disposed;

    internal AndroidSharedSurfaceMediaOutput()
    {
        _frameListener = new FrameAvailableListener(this);
        _transformMatrix[0] = 1f;
        _transformMatrix[5] = 1f;
        _transformMatrix[10] = 1f;
        _transformMatrix[15] = 1f;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => false;

    public bool TryPresent(IMediaFrameLease frame) => false;

    public bool HandlesOutputNatively(IMediaVideoOutput output) =>
        AndroidMediaCodecDecoderFactory.ResolveSurfaceOutput(output) is not null;

    public void AttachOutput(IMediaVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var surfaceOutput = AndroidMediaCodecDecoderFactory.ResolveSurfaceOutput(output);
        if (surfaceOutput is null)
        {
            return;
        }

        TargetEntry entry;
        int width;
        int height;
        lock (_targetsSync)
        {
            if (_disposed || _targets.Any(existing => ReferenceEquals(existing.Output, output)))
            {
                return;
            }

            entry = new TargetEntry(output, surfaceOutput);
            _targets.Add(entry);
            width = _sourceWidth;
            height = _sourceHeight;
        }

        if (width > 0 && height > 0)
        {
            surfaceOutput.SetDecodedVideoSize(width, height);
        }

        var token = entry.AcquireCts.Token;
        _ = Task.Run(() => AcquireChildSurfaceAsync(entry, token));
    }

    public void DetachOutput(IMediaVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        TargetEntry? removed = null;
        lock (_targetsSync)
        {
            for (var index = 0; index < _targets.Count; index++)
            {
                if (ReferenceEquals(_targets[index].Output, output))
                {
                    removed = _targets[index];
                    _targets.RemoveAt(index);
                    removed.Detached = true;
                    _retiredTargets.Add(removed);
                    break;
                }
            }
        }

        removed?.CancelAcquisition();
    }

    public void ConfigureCpuFrameDelivery(
        Func<int, FfmpegMediaFrameLease>? rentLease,
        FrameLeaseReceivedHandler? onCpuFrameLeaseReceived,
        bool required)
    {
        lock (_cpuSync)
        {
            _rentCpuLease = rentLease;
            _onCpuFrameLeaseReceived = onCpuFrameLeaseReceived;
            _cpuDeliveryRequired = required;
        }
    }

    public Surface AcquireDecoderSurface(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_decoderSurface is not null)
        {
            return _decoderSurface;
        }

        try
        {
            InitializeEglAndSurface();
            return _decoderSurface!;
        }
        catch
        {
            ReleaseEglResources();
            throw;
        }
    }

    public void SetDecodedVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        TargetEntry[] targets;
        lock (_targetsSync)
        {
            _sourceWidth = width;
            _sourceHeight = height;
            targets = [.. _targets];
        }

        _decoderSurfaceTexture?.SetDefaultBufferSize(width, height);
        foreach (var target in targets)
        {
            target.SurfaceOutput.SetDecodedVideoSize(width, height);
        }
    }

    public void OnDecoderFrameRendered(CancellationToken cancellationToken)
    {
        if (_disposed || _decoderSurfaceTexture is null || _eglDisplay is null || _eglContext is null)
        {
            return;
        }

        using var cancelReg = cancellationToken.Register(static state =>
        {
            var owner = (AndroidSharedSurfaceMediaOutput)state!;
            lock (owner._frameSync)
            {
                Monitor.PulseAll(owner._frameSync);
            }
        }, this);

        lock (_frameSync)
        {
            if (!_frameAvailable && !cancellationToken.IsCancellationRequested && !_disposed)
            {
                Monitor.Wait(_frameSync, FrameWaitTimeoutMilliseconds);
            }

            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!_frameAvailable)
            {
                return;
            }

            _frameAvailable = false;
        }

        MakePbufferCurrent();
        _decoderSurfaceTexture.UpdateTexImage();
        _decoderSurfaceTexture.GetTransformMatrix(_transformMatrix);

        TargetEntry[] targets;
        TargetEntry[] retiredTargets;
        int sourceWidth;
        int sourceHeight;
        lock (_targetsSync)
        {
            targets = [.. _targets];
            retiredTargets = _retiredTargets.Count > 0
                ? [.. _retiredTargets]
                : [];
            _retiredTargets.Clear();
            sourceWidth = _sourceWidth;
            sourceHeight = _sourceHeight;
        }

        foreach (var retired in retiredTargets)
        {
            DestroyEntryEglSurface(retired);
        }

        foreach (var entry in targets)
        {
            RenderToTarget(entry, sourceWidth, sourceHeight);
        }

        Func<int, FfmpegMediaFrameLease>? rentCpuLease;
        FrameLeaseReceivedHandler? onCpuFrame;
        bool cpuRequired;
        lock (_cpuSync)
        {
            rentCpuLease = _rentCpuLease;
            onCpuFrame = _onCpuFrameLeaseReceived;
            cpuRequired = _cpuDeliveryRequired;
        }

        if (cpuRequired &&
            rentCpuLease is not null &&
            onCpuFrame is not null &&
            sourceWidth > 0 &&
            sourceHeight > 0)
        {
            ReadbackCpuFrame(sourceWidth, sourceHeight, rentCpuLease, onCpuFrame);
        }

        MakePbufferCurrent();
    }

    public void OnDecoderDisposed() => ReleaseEglResources();

    public void Dispose()
    {
        TargetEntry[] targets;
        lock (_targetsSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            targets = [.. _targets];
            _targets.Clear();
            foreach (var entry in targets)
            {
                entry.Detached = true;
                _retiredTargets.Add(entry);
            }
        }

        lock (_frameSync)
        {
            Monitor.PulseAll(_frameSync);
        }

        foreach (var entry in targets)
        {
            entry.CancelAcquisition();
        }
    }

    private static void AcquireChildSurfaceAsync(TargetEntry entry, CancellationToken token)
    {
        try
        {
            var surface = entry.SurfaceOutput.AcquireDecoderSurface(token);
            if (entry.Detached || token.IsCancellationRequested)
            {
                return;
            }

            entry.Surface = surface;
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Android shared surface child acquisition failed: {0}",
                exception);
        }
        finally
        {
            entry.CompleteAcquisition();
        }
    }

    private void InitializeEglAndSurface()
    {
        _eglDisplay = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (_eglDisplay is null || _eglDisplay == EGL14.EglNoDisplay)
        {
            throw new PlatformNotSupportedException("Unable to acquire the Android EGL display.");
        }

        var version = new int[2];
        if (!EGL14.EglInitialize(_eglDisplay, version, 0, version, 1))
        {
            throw new PlatformNotSupportedException(
                $"Unable to initialize Android EGL (0x{EGL14.EglGetError():X}).");
        }

        _eglConfig = ChooseEglConfig(_eglDisplay);
        int[] contextAttribs = [EGL14.EglContextClientVersion, 2, EGL14.EglNone];
        _eglContext = EGL14.EglCreateContext(
            _eglDisplay,
            _eglConfig,
            EGL14.EglNoContext,
            contextAttribs,
            0);
        if (_eglContext is null || _eglContext == EGL14.EglNoContext)
        {
            throw new PlatformNotSupportedException(
                $"Unable to create Android shared EGL context (0x{EGL14.EglGetError():X}).");
        }

        int[] pbufferAttribs = [EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone];
        _pbufferSurface = EGL14.EglCreatePbufferSurface(_eglDisplay, _eglConfig, pbufferAttribs, 0);
        if (_pbufferSurface is null || _pbufferSurface == EGL14.EglNoSurface)
        {
            throw new PlatformNotSupportedException(
                $"Unable to create Android shared EGL Pbuffer surface (0x{EGL14.EglGetError():X}).");
        }

        MakePbufferCurrent();
        InitializeGlProgramsAndBuffers();

        var textures = new int[1];
        GLES20.GlGenTextures(1, textures, 0);
        _oesTexture = textures[0];
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _oesTexture);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

        _callbackThread = new HandlerThread("FrameFlux-SharedSurface");
        _callbackThread.Start();
        _callbackHandler = new Handler(_callbackThread.Looper!);

        _decoderSurfaceTexture = new SurfaceTexture(_oesTexture);
        if (_sourceWidth > 0 && _sourceHeight > 0)
        {
            _decoderSurfaceTexture.SetDefaultBufferSize(_sourceWidth, _sourceHeight);
        }

        _decoderSurfaceTexture.SetOnFrameAvailableListener(_frameListener, _callbackHandler);
        _decoderSurface = new Surface(_decoderSurfaceTexture);
    }

    private static EGLConfig ChooseEglConfig(EGLDisplay display)
    {
        int[][] candidateAttribs =
        [
            [
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 8,
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit,
                EGL14.EglNone
            ],
            [
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 0,
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit,
                EGL14.EglNone
            ]
        ];

        var configs = new EGLConfig[1];
        var numConfigs = new int[1];
        foreach (var attribs in candidateAttribs)
        {
            if (EGL14.EglChooseConfig(display, attribs, 0, configs, 0, configs.Length, numConfigs, 0) &&
                numConfigs[0] > 0 &&
                configs[0] is not null)
            {
                return configs[0];
            }
        }

        throw new PlatformNotSupportedException("No compatible Android EGL configuration was found.");
    }

    private void InitializeGlProgramsAndBuffers()
    {
        _windowProgram = BuildProgram(VertexShaderSource, WindowFragmentShaderSource);
        _windowPosLoc = GLES20.GlGetAttribLocation(_windowProgram, "aPosition");
        _windowTexLoc = GLES20.GlGetAttribLocation(_windowProgram, "aTextureCoordinate");
        _windowMatrixLoc = GLES20.GlGetUniformLocation(_windowProgram, "uTextureTransform");
        _windowSamplerLoc = GLES20.GlGetUniformLocation(_windowProgram, "uTexture");

        _fboProgram = BuildProgram(VertexShaderSource, FboBgraFragmentShaderSource);
        _fboPosLoc = GLES20.GlGetAttribLocation(_fboProgram, "aPosition");
        _fboTexLoc = GLES20.GlGetAttribLocation(_fboProgram, "aTextureCoordinate");
        _fboMatrixLoc = GLES20.GlGetUniformLocation(_fboProgram, "uTextureTransform");
        _fboSamplerLoc = GLES20.GlGetUniformLocation(_fboProgram, "uTexture");

        _positionBuffer = AllocateFloatBuffer(
        [
            -1f,  1f,
            -1f, -1f,
             1f, -1f,
            -1f,  1f,
             1f, -1f,
             1f,  1f
        ]);
        _windowTexBuffer = AllocateFloatBuffer(
        [
            0f, 1f,
            0f, 0f,
            1f, 0f,
            0f, 1f,
            1f, 0f,
            1f, 1f
        ]);
        _fboTexBuffer = AllocateFloatBuffer(
        [
            0f, 0f,
            0f, 1f,
            1f, 1f,
            0f, 0f,
            1f, 1f,
            1f, 0f
        ]);
    }

    private static FloatBuffer AllocateFloatBuffer(float[] data)
    {
        var buffer = ByteBuffer.AllocateDirect(data.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        buffer.Put(data);
        buffer.Position(0);
        return buffer;
    }

    private static int BuildProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(GLES20.GlVertexShader, vertexSource);
        var fragmentShader = CompileShader(GLES20.GlFragmentShader, fragmentSource);
        var program = GLES20.GlCreateProgram();
        try
        {
            GLES20.GlAttachShader(program, vertexShader);
            GLES20.GlAttachShader(program, fragmentShader);
            GLES20.GlLinkProgram(program);
            var status = new int[1];
            GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, status, 0);
            if (status[0] == 0)
            {
                var log = GLES20.GlGetProgramInfoLog(program);
                throw new InvalidOperationException($"Unable to link Android shared shader: {log}");
            }

            var linked = program;
            program = 0;
            return linked;
        }
        finally
        {
            GLES20.GlDeleteShader(vertexShader);
            GLES20.GlDeleteShader(fragmentShader);
            if (program != 0)
            {
                GLES20.GlDeleteProgram(program);
            }
        }
    }

    private static int CompileShader(int type, string source)
    {
        var shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        var status = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
        if (status[0] == 0)
        {
            var log = GLES20.GlGetShaderInfoLog(shader);
            GLES20.GlDeleteShader(shader);
            throw new InvalidOperationException($"Unable to compile Android shared shader: {log}");
        }

        return shader;
    }

    private void MakePbufferCurrent()
    {
        if (!EGL14.EglMakeCurrent(_eglDisplay, _pbufferSurface, _pbufferSurface, _eglContext))
        {
            throw new InvalidOperationException(
                $"Unable to activate Android shared EGL Pbuffer context (0x{EGL14.EglGetError():X}).");
        }
    }

    private void RenderToTarget(TargetEntry entry, int sourceWidth, int sourceHeight)
    {
        var surface = entry.Surface;
        if (entry.Detached || surface is null)
        {
            return;
        }

        if (!surface.IsValid)
        {
            DestroyEntryEglSurface(entry);
            return;
        }

        try
        {
            if (entry.EglSurface is null)
            {
                int[] attribs = [EGL14.EglNone];
                entry.EglSurface = EGL14.EglCreateWindowSurface(
                    _eglDisplay,
                    _eglConfig,
                    surface,
                    attribs,
                    0);
                entry.SwapIntervalConfigured = false;
                if (entry.EglSurface is null || entry.EglSurface == EGL14.EglNoSurface)
                {
                    entry.EglSurface = null;
                    return;
                }
            }

            if (!EGL14.EglMakeCurrent(_eglDisplay, entry.EglSurface, entry.EglSurface, _eglContext))
            {
                DestroyEntryEglSurface(entry);
                return;
            }

            if (!entry.SwapIntervalConfigured)
            {
                _ = EGL14.EglSwapInterval(_eglDisplay, 0);
                entry.SwapIntervalConfigured = true;
            }

            var dim = new int[1];
            var targetWidth = EGL14.EglQuerySurface(_eglDisplay, entry.EglSurface, EGL14.EglWidth, dim, 0) && dim[0] > 0
                ? dim[0]
                : Math.Max(1, sourceWidth);
            var targetHeight = EGL14.EglQuerySurface(_eglDisplay, entry.EglSurface, EGL14.EglHeight, dim, 0) && dim[0] > 0
                ? dim[0]
                : Math.Max(1, sourceHeight);

            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
            GLES20.GlViewport(0, 0, targetWidth, targetHeight);
            DrawOesQuad(
                _windowProgram,
                _windowPosLoc,
                _windowTexLoc,
                _windowMatrixLoc,
                _windowSamplerLoc,
                _windowTexBuffer!);
            if (!EGL14.EglSwapBuffers(_eglDisplay, entry.EglSurface))
            {
                DestroyEntryEglSurface(entry);
            }
        }
        catch
        {
            DestroyEntryEglSurface(entry);
        }
    }

    private void DestroyEntryEglSurface(TargetEntry entry)
    {
        if (entry.EglSurface is not { } eglSurface)
        {
            return;
        }

        entry.EglSurface = null;
        entry.SwapIntervalConfigured = false;
        MakePbufferCurrent();
        if (_eglDisplay is not null)
        {
            EGL14.EglDestroySurface(_eglDisplay, eglSurface);
        }
    }

    private unsafe void ReadbackCpuFrame(
        int width,
        int height,
        Func<int, FfmpegMediaFrameLease> rentCpuLease,
        FrameLeaseReceivedHandler onCpuFrameLeaseReceived)
    {
        MakePbufferCurrent();
        EnsureFbo(width, height);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _fbo);
        GLES20.GlViewport(0, 0, width, height);
        DrawOesQuad(
            _fboProgram,
            _fboPosLoc,
            _fboTexLoc,
            _fboMatrixLoc,
            _fboSamplerLoc,
            _fboTexBuffer!);

        var stride = checked(width * 4);
        var requiredBytes = checked(stride * height);
        EnsureReadbackBuffer(requiredBytes);
        _readbackBuffer!.Clear();
        GLES20.GlReadPixels(0, 0, width, height, GLES20.GlRgba, GLES20.GlUnsignedByte, _readbackBuffer);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);

        var lease = rentCpuLease(requiredBytes);
        try
        {
            System.Buffer.MemoryCopy(
                (void*)_readbackAddress,
                (void*)lease.Buffer,
                lease.Size,
                requiredBytes);
            lease.ResetBgra(width, height, stride);
            onCpuFrameLeaseReceived(lease);
            lease = null;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private void DrawOesQuad(
        int program,
        int posLoc,
        int texLoc,
        int matrixLoc,
        int samplerLoc,
        FloatBuffer texCoords)
    {
        GLES20.GlClearColor(0f, 0f, 0f, 1f);
        GLES20.GlClear(GLES20.GlColorBufferBit);
        GLES20.GlUseProgram(program);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _oesTexture);
        GLES20.GlUniform1i(samplerLoc, 0);
        GLES20.GlUniformMatrix4fv(matrixLoc, 1, false, _transformMatrix, 0);

        _positionBuffer!.Position(0);
        GLES20.GlVertexAttribPointer(posLoc, 2, GLES20.GlFloat, false, 0, _positionBuffer);
        GLES20.GlEnableVertexAttribArray(posLoc);

        texCoords.Position(0);
        GLES20.GlVertexAttribPointer(texLoc, 2, GLES20.GlFloat, false, 0, texCoords);
        GLES20.GlEnableVertexAttribArray(texLoc);

        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, 6);
        GLES20.GlDisableVertexAttribArray(posLoc);
        GLES20.GlDisableVertexAttribArray(texLoc);
    }

    private void EnsureFbo(int width, int height)
    {
        if (_fbo != 0 && _fboWidth == width && _fboHeight == height)
        {
            return;
        }

        ReleaseFbo();
        var ids = new int[1];
        GLES20.GlGenTextures(1, ids, 0);
        _fboTexture = ids[0];
        GLES20.GlBindTexture(GLES20.GlTexture2d, _fboTexture);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexImage2D(
            GLES20.GlTexture2d,
            0,
            GLES20.GlRgba,
            width,
            height,
            0,
            GLES20.GlRgba,
            GLES20.GlUnsignedByte,
            null);

        GLES20.GlGenFramebuffers(1, ids, 0);
        _fbo = ids[0];
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _fbo);
        GLES20.GlFramebufferTexture2D(
            GLES20.GlFramebuffer,
            GLES20.GlColorAttachment0,
            GLES20.GlTexture2d,
            _fboTexture,
            0);
        var status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        if (status != GLES20.GlFramebufferComplete)
        {
            ReleaseFbo();
            throw new InvalidOperationException($"Android shared readback FBO incomplete (0x{status:X}).");
        }

        _fboWidth = width;
        _fboHeight = height;
    }

    private void EnsureReadbackBuffer(int requiredBytes)
    {
        if (_readbackBuffer is not null && _readbackCapacity >= requiredBytes)
        {
            return;
        }

        _readbackBuffer?.Dispose();
        _readbackBuffer = ByteBuffer.AllocateDirect(requiredBytes);
        _readbackAddress = JNIEnv.GetDirectBufferAddress(_readbackBuffer.Handle);
        if (_readbackAddress == IntPtr.Zero)
        {
            _readbackBuffer.Dispose();
            _readbackBuffer = null;
            _readbackCapacity = 0;
            throw new InvalidOperationException("Unable to map direct ByteBuffer address for readback.");
        }

        _readbackCapacity = requiredBytes;
    }

    private void ReleaseFbo()
    {
        if (_fbo != 0)
        {
            GLES20.GlDeleteFramebuffers(1, [_fbo], 0);
            _fbo = 0;
        }
        if (_fboTexture != 0)
        {
            GLES20.GlDeleteTextures(1, [_fboTexture], 0);
            _fboTexture = 0;
        }

        _fboWidth = 0;
        _fboHeight = 0;
    }

    private void ReleaseEglResources()
    {
        TargetEntry[] targets;
        TargetEntry[] retiredTargets;
        lock (_targetsSync)
        {
            targets = [.. _targets];
            retiredTargets = [.. _retiredTargets];
            _retiredTargets.Clear();
        }

        if (_decoderSurfaceTexture is not null)
        {
            try
            {
                _decoderSurfaceTexture.SetOnFrameAvailableListener(null);
            }
            catch
            {
            }
        }

        _decoderSurface?.Release();
        _decoderSurface?.Dispose();
        _decoderSurface = null;

        _decoderSurfaceTexture?.Release();
        _decoderSurfaceTexture?.Dispose();
        _decoderSurfaceTexture = null;

        _callbackThread?.QuitSafely();
        _callbackThread?.Dispose();
        _callbackThread = null;
        _callbackHandler?.Dispose();
        _callbackHandler = null;

        _readbackBuffer?.Dispose();
        _readbackBuffer = null;
        _readbackAddress = IntPtr.Zero;
        _readbackCapacity = 0;

        _positionBuffer?.Dispose();
        _positionBuffer = null;
        _windowTexBuffer?.Dispose();
        _windowTexBuffer = null;
        _fboTexBuffer?.Dispose();
        _fboTexBuffer = null;

        if (_eglDisplay is not null && _eglContext is not null)
        {
            if (_pbufferSurface is not null)
            {
                _ = EGL14.EglMakeCurrent(_eglDisplay, _pbufferSurface, _pbufferSurface, _eglContext);
            }

            foreach (var retired in retiredTargets)
            {
                if (retired.EglSurface is { } retiredSurface)
                {
                    retired.EglSurface = null;
                    retired.SwapIntervalConfigured = false;
                    EGL14.EglDestroySurface(_eglDisplay, retiredSurface);
                }
            }

            foreach (var entry in targets)
            {
                if (entry.EglSurface is { } eglSurface)
                {
                    entry.EglSurface = null;
                    entry.SwapIntervalConfigured = false;
                    EGL14.EglDestroySurface(_eglDisplay, eglSurface);
                }
            }

            ReleaseFbo();
            if (_oesTexture != 0)
            {
                GLES20.GlDeleteTextures(1, [_oesTexture], 0);
                _oesTexture = 0;
            }
            if (_windowProgram != 0)
            {
                GLES20.GlDeleteProgram(_windowProgram);
                _windowProgram = 0;
            }
            if (_fboProgram != 0)
            {
                GLES20.GlDeleteProgram(_fboProgram);
                _fboProgram = 0;
            }

            _ = EGL14.EglMakeCurrent(
                _eglDisplay,
                EGL14.EglNoSurface,
                EGL14.EglNoSurface,
                EGL14.EglNoContext);
        }

        if (_eglDisplay is not null && _pbufferSurface is not null)
        {
            EGL14.EglDestroySurface(_eglDisplay, _pbufferSurface);
            _pbufferSurface = null;
        }
        if (_eglDisplay is not null && _eglContext is not null)
        {
            EGL14.EglDestroyContext(_eglDisplay, _eglContext);
            _eglContext = null;
        }
        if (_eglDisplay is not null)
        {
            EGL14.EglTerminate(_eglDisplay);
            _eglDisplay = null;
        }

        _eglConfig = null;
    }

    private void NotifyFrameAvailable()
    {
        lock (_frameSync)
        {
            _frameAvailable = true;
            Monitor.PulseAll(_frameSync);
        }
    }

    private sealed class TargetEntry(
        IMediaVideoOutput output,
        IAndroidVideoSurfaceOutput surfaceOutput)
    {
        private readonly object _ctsSync = new();
        private bool _ctsDisposed;

        internal IMediaVideoOutput Output { get; } = output;
        internal IAndroidVideoSurfaceOutput SurfaceOutput { get; } = surfaceOutput;
        internal CancellationTokenSource AcquireCts { get; } = new();
        internal volatile Surface? Surface;
        internal volatile bool Detached;
        internal EGLSurface? EglSurface;
        internal bool SwapIntervalConfigured;

        internal void CancelAcquisition()
        {
            lock (_ctsSync)
            {
                if (!_ctsDisposed)
                {
                    AcquireCts.Cancel();
                }
            }
        }

        internal void CompleteAcquisition()
        {
            lock (_ctsSync)
            {
                if (!_ctsDisposed)
                {
                    _ctsDisposed = true;
                    AcquireCts.Dispose();
                }
            }
        }
    }

    private sealed class FrameAvailableListener(AndroidSharedSurfaceMediaOutput owner) :
        Java.Lang.Object,
        SurfaceTexture.IOnFrameAvailableListener
    {
        public void OnFrameAvailable(SurfaceTexture? surfaceTexture) =>
            owner.NotifyFrameAvailable();
    }

    private const string VertexShaderSource =
        "attribute vec2 aPosition;\n" +
        "attribute vec2 aTextureCoordinate;\n" +
        "uniform mat4 uTextureTransform;\n" +
        "varying vec2 vTextureCoordinate;\n" +
        "void main() {\n" +
        "  vec4 transformed = uTextureTransform * vec4(aTextureCoordinate, 0.0, 1.0);\n" +
        "  vTextureCoordinate = transformed.xy;\n" +
        "  gl_Position = vec4(aPosition, 0.0, 1.0);\n" +
        "}\n";

    private const string WindowFragmentShaderSource =
        "#extension GL_OES_EGL_image_external : require\n" +
        "precision mediump float;\n" +
        "uniform samplerExternalOES uTexture;\n" +
        "varying vec2 vTextureCoordinate;\n" +
        "void main() {\n" +
        "  gl_FragColor = texture2D(uTexture, vTextureCoordinate);\n" +
        "}\n";

    private const string FboBgraFragmentShaderSource =
        "#extension GL_OES_EGL_image_external : require\n" +
        "precision mediump float;\n" +
        "uniform samplerExternalOES uTexture;\n" +
        "varying vec2 vTextureCoordinate;\n" +
        "void main() {\n" +
        "  gl_FragColor = texture2D(uTexture, vTextureCoordinate).bgra;\n" +
        "}\n";
}
