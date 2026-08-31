using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using FrameFlux.FFmpeg.Android;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class AndroidSurfaceTextureMediaOutput :
    OpenGlControlBase,
    IAvaloniaPlatformMediaOutput,
    IAndroidVideoSurfaceOutput,
    IMediaVideoOutputFeatureProvider
{
    private const int GlArrayBuffer = 0x8892;
    private const int GlClampToEdge = 0x812F;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlDepthTest = 0x0B71;
    private const int GlDynamicDraw = 0x88E8;
    private const int GlFloat = 0x1406;
    private const int GlFragmentShader = 0x8B30;
    private const int GlFramebuffer = 0x8D40;
    private const int GlLinear = 0x2601;
    private const int GlTexture0 = 0x84C0;
    private const int GlTextureExternalOes = 0x8D65;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlTriangles = 0x0004;
    private const int GlVertexShader = 0x8B31;

    private readonly object _surfaceSync = new();
    private readonly ManualResetEventSlim _surfaceReady = new(false);
    private readonly MediaPresentationFailureTracker _failureTracker = new(maximumAttempts: 1);
    private readonly float[] _textureTransform = new float[16];
    private SurfaceTexture? _surfaceTexture;
    private global::Android.Views.Surface? _decoderSurface;
    private Exception? _surfaceFailure;
    private TaskCompletionSource<object?>? _releaseCompletion;
    private UniformMatrix4Delegate? _uniformMatrix4;
    private Stretch _stretch = Stretch.Uniform;
    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _vertexBuffer;
    private int _texture;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _frameAvailable;
    private bool _surfaceRequested;
    private bool _releaseRequested;
    private bool _clearRequested;
    private bool _hasFrame;
    private bool _disposed;

    internal AndroidSurfaceTextureMediaOutput()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        _textureTransform[0] = 1;
        _textureTransform[5] = 1;
        _textureTransform[10] = 1;
        _textureTransform[15] = 1;
    }

    public Control Surface => this;

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

    public Stretch Stretch
    {
        get => _stretch;
        set
        {
            _stretch = value;
            RequestRender();
        }
    }

    public event EventHandler? FramePresented;

    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => false;

    public bool TryPresent(IMediaFrameLease frame) => false;

    public object? GetVideoOutputFeature(Type featureType)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        return featureType.IsInstanceOfType(this) ? this : null;
    }

    public global::Android.Views.Surface AcquireDecoderSurface(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_surfaceSync)
        {
            if (_decoderSurface is not null) return _decoderSurface;
            _surfaceFailure = null;
            _surfaceRequested = true;
            _surfaceReady.Reset();
        }

        RequestRender();
        _surfaceReady.Wait(cancellationToken);
        lock (_surfaceSync)
        {
            if (_surfaceFailure is not null)
            {
                throw new PlatformNotSupportedException(
                    "The Avalonia Android OpenGL Surface is unavailable.",
                    _surfaceFailure);
            }

            return _decoderSurface ?? throw new PlatformNotSupportedException(
                "The Avalonia Android OpenGL Surface was not created.");
        }
    }

    public void SetDecodedVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_surfaceSync)
        {
            _sourceWidth = width;
            _sourceHeight = height;
            _surfaceTexture?.SetDefaultBufferSize(width, height);
        }
        RequestRender();
    }

    public void Clear()
    {
        _failureTracker.Reset();
        _clearRequested = true;
        RequestRender();
    }

    public ValueTask ReleaseResourcesAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        TaskCompletionSource<object?> completion;
        lock (_surfaceSync)
        {
            if (_decoderSurface is null && _texture == 0)
            {
                return ValueTask.CompletedTask;
            }

            completion = _releaseCompletion ??= new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseRequested = true;
            _surfaceRequested = false;
            _surfaceReady.Reset();
        }
        RequestRender();
        return new ValueTask(completion.Task);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ReleaseResourcesAsync();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _vertexShader = CompileShader(gl, GlVertexShader, VertexShaderSource);
            _fragmentShader = CompileShader(gl, GlFragmentShader, FragmentShaderSource);
            _program = gl.CreateProgram();
            gl.AttachShader(_program, _vertexShader);
            gl.AttachShader(_program, _fragmentShader);
            gl.BindAttribLocationString(_program, 0, "aPosition");
            gl.BindAttribLocationString(_program, 1, "aTextureCoordinate");
            var linkError = gl.LinkProgramAndGetError(_program);
            if (!string.IsNullOrEmpty(linkError))
            {
                throw new InvalidOperationException(
                    $"Unable to link the Android external-texture shader: {linkError}");
            }

            var matrixAddress = gl.GetProcAddress("glUniformMatrix4fv");
            if (matrixAddress == IntPtr.Zero)
            {
                throw new PlatformNotSupportedException(
                    "The Android OpenGL ES context does not expose glUniformMatrix4fv.");
            }
            _uniformMatrix4 = Marshal.GetDelegateForFunctionPointer<UniformMatrix4Delegate>(
                matrixAddress);
            _vertexBuffer = gl.GenBuffer();
            RequestRender();
        }
        catch (Exception exception)
        {
            SignalSurfaceFailure(exception);
            ReportFailure(exception);
            throw;
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        try
        {
            ProcessSurfaceRequests(gl);
            gl.BindFramebuffer(GlFramebuffer, framebuffer);
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
            var targetWidth = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
            var targetHeight = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));
            gl.Viewport(0, 0, targetWidth, targetHeight);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlColorBufferBit);

            if (_clearRequested)
            {
                _clearRequested = false;
                _hasFrame = false;
            }

            var updated = false;
            if (_surfaceTexture is not null && Interlocked.Exchange(ref _frameAvailable, 0) != 0)
            {
                _surfaceTexture.UpdateTexImage();
                _surfaceTexture.GetTransformMatrix(_textureTransform);
                _hasFrame = true;
                updated = true;
            }

            if (_hasFrame && _texture != 0 && _sourceWidth > 0 && _sourceHeight > 0)
            {
                DrawTexture(gl, targetWidth, targetHeight);
                gl.Flush();
                _failureTracker.ReportSuccess();
                if (updated)
                {
                    Dispatcher.UIThread.Post(
                        () => FramePresented?.Invoke(this, EventArgs.Empty),
                        DispatcherPriority.Render);
                }
            }
        }
        catch (Exception exception)
        {
            _hasFrame = false;
            SignalSurfaceFailure(exception);
            ReportFailure(exception);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        ReleaseSurface(gl);
        ReleaseProgram(gl);
        TaskCompletionSource<object?>? releaseCompletion;
        lock (_surfaceSync)
        {
            releaseCompletion = _releaseCompletion;
            _releaseCompletion = null;
            _releaseRequested = false;
        }
        releaseCompletion?.TrySetResult(null);
    }

    protected override void OnOpenGlLost()
    {
        var exception = new PlatformNotSupportedException(
            "The Avalonia Android OpenGL ES context was lost.");
        SignalSurfaceFailure(exception);
        ReportFailure(exception);
    }

    private void ProcessSurfaceRequests(GlInterface gl)
    {
        TaskCompletionSource<object?>? releaseCompletion = null;
        var createSurface = false;
        lock (_surfaceSync)
        {
            if (_releaseRequested)
            {
                _releaseRequested = false;
                releaseCompletion = _releaseCompletion;
                _releaseCompletion = null;
            }
            createSurface = !_disposed && _surfaceRequested && _decoderSurface is null;
        }

        if (releaseCompletion is not null)
        {
            ReleaseSurface(gl);
            releaseCompletion.TrySetResult(null);
        }
        if (createSurface)
        {
            CreateSurface(gl);
        }
    }

    private void CreateSurface(GlInterface gl)
    {
        _texture = gl.GenTexture();
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTextureExternalOes, _texture);
        gl.TexParameteri(GlTextureExternalOes, GlTextureMinFilter, GlLinear);
        gl.TexParameteri(GlTextureExternalOes, GlTextureMagFilter, GlLinear);
        gl.TexParameteri(GlTextureExternalOes, GlTextureWrapS, GlClampToEdge);
        gl.TexParameteri(GlTextureExternalOes, GlTextureWrapT, GlClampToEdge);

        var surfaceTexture = new SurfaceTexture(_texture);
        if (_sourceWidth > 0 && _sourceHeight > 0)
        {
            surfaceTexture.SetDefaultBufferSize(_sourceWidth, _sourceHeight);
        }
        surfaceTexture.FrameAvailable += OnFrameAvailable;
        var surface = new global::Android.Views.Surface(surfaceTexture);
        lock (_surfaceSync)
        {
            _surfaceTexture = surfaceTexture;
            _decoderSurface = surface;
            _surfaceFailure = null;
            _surfaceRequested = false;
            _surfaceReady.Set();
        }
    }

    private void ReleaseSurface(GlInterface gl)
    {
        SurfaceTexture? surfaceTexture;
        global::Android.Views.Surface? surface;
        lock (_surfaceSync)
        {
            surfaceTexture = _surfaceTexture;
            surface = _decoderSurface;
            _surfaceTexture = null;
            _decoderSurface = null;
            _surfaceReady.Reset();
        }

        if (surfaceTexture is not null)
        {
            surfaceTexture.FrameAvailable -= OnFrameAvailable;
        }
        surface?.Release();
        surface?.Dispose();
        surfaceTexture?.Release();
        surfaceTexture?.Dispose();
        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
            _texture = 0;
        }
        _hasFrame = false;
        Interlocked.Exchange(ref _frameAvailable, 0);
    }

    private void ReleaseProgram(GlInterface gl)
    {
        if (_vertexBuffer != 0) gl.DeleteBuffer(_vertexBuffer);
        if (_program != 0) gl.DeleteProgram(_program);
        if (_vertexShader != 0) gl.DeleteShader(_vertexShader);
        if (_fragmentShader != 0) gl.DeleteShader(_fragmentShader);
        _vertexBuffer = 0;
        _program = 0;
        _vertexShader = 0;
        _fragmentShader = 0;
        _uniformMatrix4 = null;
    }

    private unsafe void DrawTexture(GlInterface gl, int targetWidth, int targetHeight)
    {
        var vertices = BuildVertices(
            _sourceWidth,
            _sourceHeight,
            targetWidth,
            targetHeight,
            _stretch);
        gl.Disable(GlDepthTest);
        gl.UseProgram(_program);
        gl.Uniform1i(gl.GetUniformLocationString(_program, "uTexture"), 0);
        var matrixLocation = gl.GetUniformLocationString(_program, "uTextureTransform");
        fixed (float* matrix = _textureTransform)
        {
            _uniformMatrix4!(matrixLocation, 1, 0, (IntPtr)matrix);
        }
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTextureExternalOes, _texture);
        gl.BindBuffer(GlArrayBuffer, _vertexBuffer);
        fixed (float* vertexPointer = vertices)
        {
            gl.BufferData(
                GlArrayBuffer,
                (IntPtr)(vertices.Length * sizeof(float)),
                (IntPtr)vertexPointer,
                GlDynamicDraw);
        }
        gl.VertexAttribPointer(0, 2, GlFloat, 0, sizeof(float) * 4, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(
            1,
            2,
            GlFloat,
            0,
            sizeof(float) * 4,
            (IntPtr)(sizeof(float) * 2));
        gl.EnableVertexAttribArray(1);
        gl.DrawArrays(GlTriangles, 0, (IntPtr)6);
    }

    internal static float[] BuildVertices(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Stretch stretch)
    {
        var positionScaleX = 1d;
        var positionScaleY = 1d;
        var u0 = 0d;
        var u1 = 1d;
        var v0 = 0d;
        var v1 = 1d;
        var sourceAspect = sourceWidth / (double)sourceHeight;
        var targetAspect = targetWidth / (double)targetHeight;

        if (stretch == Stretch.Uniform)
        {
            if (sourceAspect > targetAspect) positionScaleY = targetAspect / sourceAspect;
            else positionScaleX = sourceAspect / targetAspect;
        }
        else if (stretch == Stretch.UniformToFill)
        {
            if (sourceAspect > targetAspect)
            {
                var visibleWidth = targetAspect / sourceAspect;
                u0 = (1d - visibleWidth) / 2d;
                u1 = 1d - u0;
            }
            else
            {
                var visibleHeight = sourceAspect / targetAspect;
                v0 = (1d - visibleHeight) / 2d;
                v1 = 1d - v0;
            }
        }
        else if (stretch == Stretch.None)
        {
            positionScaleX = sourceWidth / (double)targetWidth;
            positionScaleY = sourceHeight / (double)targetHeight;
        }

        var left = (float)-positionScaleX;
        var right = (float)positionScaleX;
        var bottom = (float)-positionScaleY;
        var top = (float)positionScaleY;
        return
        [
            left, top, (float)u0, (float)v1,
            left, bottom, (float)u0, (float)v0,
            right, bottom, (float)u1, (float)v0,
            left, top, (float)u0, (float)v1,
            right, bottom, (float)u1, (float)v0,
            right, top, (float)u1, (float)v1
        ];
    }

    private static int CompileShader(GlInterface gl, int type, string source)
    {
        var shader = gl.CreateShader(type);
        var error = gl.CompileShaderAndGetError(shader, source);
        if (string.IsNullOrEmpty(error)) return shader;
        gl.DeleteShader(shader);
        throw new InvalidOperationException(
            $"Unable to compile the Android external-texture shader: {error}");
    }

    private void OnFrameAvailable(object? sender, SurfaceTexture.FrameAvailableEventArgs args)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _frameAvailable, 1);
        RequestRender();
    }

    private void SignalSurfaceFailure(Exception exception)
    {
        lock (_surfaceSync)
        {
            _surfaceFailure = exception;
            _surfaceReady.Set();
            _releaseCompletion?.TrySetException(exception);
            _releaseCompletion = null;
        }
    }

    private void RequestRender()
    {
        if (_disposed) return;
        try
        {
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
        }
        catch (Exception exception)
        {
            SignalSurfaceFailure(exception);
        }
    }

    private void ReportFailure(Exception exception)
    {
        System.Diagnostics.Trace.TraceError(
            "Avalonia Android SurfaceTexture presentation failed: {0}",
            exception);
        var failure = _failureTracker.Register(exception);
        Dispatcher.UIThread.Post(
            () => PresentationFailed?.Invoke(this, failure),
            DispatcherPriority.Render);
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

    private const string FragmentShaderSource =
        "#extension GL_OES_EGL_image_external : require\n" +
        "precision mediump float;\n" +
        "uniform samplerExternalOES uTexture;\n" +
        "varying vec2 vTextureCoordinate;\n" +
        "void main() {\n" +
        "  gl_FragColor = texture2D(uTexture, vTextureCoordinate);\n" +
        "}\n";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UniformMatrix4Delegate(
        int location,
        int count,
        byte transpose,
        IntPtr value);
}
