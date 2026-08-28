using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class LinuxOpenGlMediaOutput :
    OpenGlControlBase,
    IAvaloniaPlatformMediaOutput
{
    private const int GlArrayBuffer = 0x8892;
    private const int GlBgra = 0x80E1;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlDepthTest = 0x0B71;
    private const int GlDynamicDraw = 0x88E8;
    private const int GlFloat = 0x1406;
    private const int GlFragmentShader = 0x8B30;
    private const int GlFramebuffer = 0x8D40;
    private const int GlLinear = 0x2601;
    private const int GlRgba = 0x1908;
    private const int GlTexture0 = 0x84C0;
    private const int GlTexture2D = 0x0DE1;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlTriangles = 0x0004;
    private const int GlUnsignedByte = 0x1401;
    private const int GlVertexShader = 0x8B31;
    private const int GlClampToEdge = 0x812F;

    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly MediaPresentationFailureTracker _failureTracker = new();
    private TexSubImage2DDelegate? _texSubImage2D;
    private byte[]? _stagingBuffer;
    private Stretch _stretch = Stretch.Uniform;
    private int _program;
    private int _dmaBufProgram;
    private int _vertexShader;
    private int _fragmentShader;
    private int _dmaBufFragmentShader;
    private int _vertexBuffer;
    private int _vertexArray;
    private int _texture;
    private int _textureWidth;
    private int _textureHeight;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _isOpenGlEs;
    private bool _hasTexture;
    private bool _hasDmaBufTexture;
    private bool _clearRequested;
    private bool _disposed;

    internal LinuxOpenGlMediaOutput()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public Control Surface => this;

    public MediaFrameStorageKind PreferredFrameStorage =>
        MediaFrameStorageKind.DmaBuf;

    private LinuxDmaBufEglInterop? _dmaBufInterop;
    private AvaloniaLinuxDmaBufGlApi? _dmaBufGl;

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

    public bool Supports(
        MediaFrameStorageKind storageKind,
        MediaPixelFormat pixelFormat) =>
        (storageKind == MediaFrameStorageKind.CpuMemory &&
         pixelFormat == MediaPixelFormat.Bgra32) ||
        storageKind == MediaFrameStorageKind.DmaBuf;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed ||
            _failureTracker.IsExhausted ||
            !Supports(frame.StorageKind, frame.PixelFormat) ||
            (frame.StorageKind == MediaFrameStorageKind.CpuMemory &&
             !frame.TryGetCpuBuffer(out _)) ||
            (frame.StorageKind == MediaFrameStorageKind.DmaBuf &&
             !frame.TryGetDmaBuf(out _)))
        {
            return false;
        }

        if (!_frameSlot.TrySubmit(frame, out var schedulePresentation))
        {
            return false;
        }

        if (schedulePresentation)
        {
            RequestRender();
        }
        return true;
    }

    public void Clear()
    {
        _frameSlot.Clear();
        _failureTracker.Reset();
        _clearRequested = true;
        RequestRender();
    }

    public ValueTask ReleaseResourcesAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _frameSlot.Dispose();
        _clearRequested = true;
        RequestRender();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _isOpenGlEs = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
            _vertexShader = CompileShader(gl, GlVertexShader, CreateVertexShader(_isOpenGlEs));
            _fragmentShader = CompileShader(
                gl,
                GlFragmentShader,
                CreateFragmentShader(_isOpenGlEs));
            _program = gl.CreateProgram();
            gl.AttachShader(_program, _vertexShader);
            gl.AttachShader(_program, _fragmentShader);
            gl.BindAttribLocationString(_program, 0, "aPosition");
            gl.BindAttribLocationString(_program, 1, "aTextureCoordinate");
            var linkError = gl.LinkProgramAndGetError(_program);
            if (!string.IsNullOrEmpty(linkError))
            {
                throw new InvalidOperationException(
                    $"Unable to link the Linux video shader: {linkError}");
            }

            _dmaBufFragmentShader = CompileShader(
                gl,
                GlFragmentShader,
                CreateDmaBufFragmentShader(_isOpenGlEs));
            _dmaBufProgram = gl.CreateProgram();
            gl.AttachShader(_dmaBufProgram, _vertexShader);
            gl.AttachShader(_dmaBufProgram, _dmaBufFragmentShader);
            gl.BindAttribLocationString(_dmaBufProgram, 0, "aPosition");
            gl.BindAttribLocationString(_dmaBufProgram, 1, "aTextureCoordinate");
            var dmaBufLinkError = gl.LinkProgramAndGetError(_dmaBufProgram);
            if (!string.IsNullOrEmpty(dmaBufLinkError))
            {
                throw new InvalidOperationException(
                    $"Unable to link the Linux DMA-BUF shader: {dmaBufLinkError}");
            }

            _vertexArray = gl.IsGenVertexArraysAvailable ? gl.GenVertexArray() : 0;
            if (_vertexArray != 0)
            {
                gl.BindVertexArray(_vertexArray);
            }
            _vertexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlArrayBuffer, _vertexBuffer);
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

            _texture = gl.GenTexture();
            gl.ActiveTexture(GlTexture0);
            gl.BindTexture(GlTexture2D, _texture);
            gl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
            gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
            gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
            gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);

            var texSubImageAddress = gl.GetProcAddress("glTexSubImage2D");
            if (texSubImageAddress == IntPtr.Zero)
            {
                throw new PlatformNotSupportedException(
                    "The active OpenGL context does not expose glTexSubImage2D.");
            }
            _texSubImage2D = Marshal.GetDelegateForFunctionPointer<TexSubImage2DDelegate>(
                texSubImageAddress);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            throw;
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        IMediaFrameLease? frame = null;
        try
        {
            gl.BindFramebuffer(GlFramebuffer, framebuffer);
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
            var targetWidth = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
            var targetHeight = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));
            gl.Viewport(0, 0, targetWidth, targetHeight);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlColorBufferBit);

            if (_disposed)
            {
                ReleaseOpenGlResources(gl);
                return;
            }

            if (_clearRequested)
            {
                _clearRequested = false;
                _hasTexture = false;
                _hasDmaBufTexture = false;
                _sourceWidth = 0;
                _sourceHeight = 0;
            }

            frame = _frameSlot.Take();
            if (frame is not null)
            {
                if (frame.StorageKind == MediaFrameStorageKind.DmaBuf)
                {
                    if (!frame.TryGetDmaBuf(out var dmaBuf))
                    {
                        throw new InvalidOperationException(
                            "The Linux renderer received an invalid DMA-BUF frame.");
                    }
                    _dmaBufGl ??= new AvaloniaLinuxDmaBufGlApi(gl);
                    _dmaBufInterop ??= new LinuxDmaBufEglInterop(_dmaBufGl);
                    _dmaBufInterop.Import(frame.Width, frame.Height, dmaBuf);
                    _hasDmaBufTexture = true;
                }
                else
                {
                    UploadFrame(gl, frame);
                    _hasDmaBufTexture = false;
                }
                _sourceWidth = frame.Width;
                _sourceHeight = frame.Height;
                _hasTexture = true;
            }

            if (!_hasTexture)
            {
                return;
            }

            DrawTexture(gl, targetWidth, targetHeight);
            gl.Flush();
            _failureTracker.ReportSuccess();
            if (frame is not null)
            {
                Dispatcher.UIThread.Post(
                    () => FramePresented?.Invoke(this, EventArgs.Empty),
                    DispatcherPriority.Render);
            }
        }
        catch (Exception exception)
        {
            _hasTexture = false;
            ReportFailure(exception);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        ReleaseOpenGlResources(gl);
    }

    protected override void OnOpenGlLost()
    {
        _hasTexture = false;
        ReportFailure(new PlatformNotSupportedException(
            "The Avalonia OpenGL context was lost."));
    }

    private static int CompileShader(GlInterface gl, int type, string source)
    {
        var shader = gl.CreateShader(type);
        var error = gl.CompileShaderAndGetError(shader, source);
        if (string.IsNullOrEmpty(error))
        {
            return shader;
        }

        gl.DeleteShader(shader);
        throw new InvalidOperationException(
            $"Unable to compile the Linux video shader: {error}");
    }

    private unsafe void UploadFrame(GlInterface gl, IMediaFrameLease frame)
    {
        if (!frame.TryGetCpuBuffer(out var source) ||
            source.Plane0 == IntPtr.Zero ||
            source.Plane0Stride < checked(frame.Width * 4))
        {
            throw new InvalidOperationException(
                "The Linux OpenGL output received an invalid BGRA frame.");
        }

        var rowBytes = checked(frame.Width * 4);
        var requiredBytes = checked(
            (long)source.Plane0Stride * (frame.Height - 1) + rowBytes);
        if (source.Size > 0 && source.Size < requiredBytes)
        {
            throw new InvalidOperationException(
                "The Linux OpenGL output received a truncated BGRA frame.");
        }

        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _texture);
        var externalFormat = _isOpenGlEs ? GlRgba : GlBgra;
        if (source.Plane0Stride == rowBytes)
        {
            UploadPixels(gl, frame.Width, frame.Height, externalFormat, source.Plane0);
            return;
        }

        var packedSize = checked(rowBytes * frame.Height);
        if (_stagingBuffer is null || _stagingBuffer.Length < packedSize)
        {
            _stagingBuffer = GC.AllocateUninitializedArray<byte>(packedSize);
        }
        for (var row = 0; row < frame.Height; row++)
        {
            var sourceRow = new ReadOnlySpan<byte>(
                (void*)(source.Plane0 + row * source.Plane0Stride),
                rowBytes);
            sourceRow.CopyTo(_stagingBuffer.AsSpan(row * rowBytes, rowBytes));
        }
        fixed (byte* packed = _stagingBuffer)
        {
            UploadPixels(
                gl,
                frame.Width,
                frame.Height,
                externalFormat,
                (IntPtr)packed);
        }
    }

    private void UploadPixels(
        GlInterface gl,
        int width,
        int height,
        int externalFormat,
        IntPtr pixels)
    {
        if (_textureWidth != width || _textureHeight != height)
        {
            gl.TexImage2D(
                GlTexture2D,
                0,
                GlRgba,
                width,
                height,
                0,
                externalFormat,
                GlUnsignedByte,
                pixels);
            _textureWidth = width;
            _textureHeight = height;
            return;
        }

        _texSubImage2D!(
            GlTexture2D,
            0,
            0,
            0,
            width,
            height,
            externalFormat,
            GlUnsignedByte,
            pixels);
    }

    private unsafe void DrawTexture(
        GlInterface gl,
        int targetWidth,
        int targetHeight)
    {
        var vertices = BuildVertices(
            _sourceWidth,
            _sourceHeight,
            targetWidth,
            targetHeight,
            _stretch);
        gl.Disable(GlDepthTest);
        var program = _hasDmaBufTexture ? _dmaBufProgram : _program;
        gl.UseProgram(program);
        gl.ActiveTexture(GlTexture0);
        if (_hasDmaBufTexture)
        {
            gl.Uniform1i(gl.GetUniformLocationString(program, "uTextureY"), 0);
            gl.BindTexture(GlTexture2D, _dmaBufInterop!.TextureY);
            gl.ActiveTexture(GlTexture0 + 1);
            gl.Uniform1i(gl.GetUniformLocationString(program, "uTextureUv"), 1);
            gl.BindTexture(GlTexture2D, _dmaBufInterop.TextureUv);
        }
        else
        {
            gl.Uniform1i(gl.GetUniformLocationString(program, "uTexture"), 0);
            gl.Uniform1i(
                gl.GetUniformLocationString(program, "uSwapRedBlue"),
                _isOpenGlEs ? 1 : 0);
            gl.BindTexture(GlTexture2D, _texture);
        }
        if (_vertexArray != 0)
        {
            gl.BindVertexArray(_vertexArray);
        }
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
            if (sourceAspect > targetAspect)
            {
                positionScaleY = targetAspect / sourceAspect;
            }
            else
            {
                positionScaleX = sourceAspect / targetAspect;
            }
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
            left, top, (float)u0, (float)v0,
            left, bottom, (float)u0, (float)v1,
            right, bottom, (float)u1, (float)v1,
            left, top, (float)u0, (float)v0,
            right, bottom, (float)u1, (float)v1,
            right, top, (float)u1, (float)v0
        ];
    }

    private void ReleaseOpenGlResources(GlInterface gl)
    {
        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
            _texture = 0;
        }
        if (_vertexBuffer != 0)
        {
            gl.DeleteBuffer(_vertexBuffer);
            _vertexBuffer = 0;
        }
        if (_vertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
        {
            gl.DeleteVertexArray(_vertexArray);
            _vertexArray = 0;
        }
        if (_program != 0)
        {
            gl.DeleteProgram(_program);
            _program = 0;
        }
        if (_dmaBufProgram != 0)
        {
            gl.DeleteProgram(_dmaBufProgram);
            _dmaBufProgram = 0;
        }
        if (_vertexShader != 0)
        {
            gl.DeleteShader(_vertexShader);
            _vertexShader = 0;
        }
        if (_fragmentShader != 0)
        {
            gl.DeleteShader(_fragmentShader);
            _fragmentShader = 0;
        }
        if (_dmaBufFragmentShader != 0)
        {
            gl.DeleteShader(_dmaBufFragmentShader);
            _dmaBufFragmentShader = 0;
        }
        _dmaBufInterop?.Release();
        _dmaBufInterop = null;
        _dmaBufGl = null;
        _texSubImage2D = null;
        _textureWidth = 0;
        _textureHeight = 0;
        _hasTexture = false;
        _hasDmaBufTexture = false;
    }

    private void RequestRender()
    {
        if (_disposed && !_clearRequested)
        {
            return;
        }

        try
        {
            Dispatcher.UIThread.Post(
                RequestNextFrameRendering,
                DispatcherPriority.Render);
        }
        catch
        {
            _frameSlot.Clear();
        }
    }

    private void ReportFailure(Exception exception)
    {
        Console.Error.WriteLine(
            $"[{DateTimeOffset.Now:O}] FrameFlux Linux GPU presentation failed:\n{exception}");
        System.Diagnostics.Trace.TraceError(
            "Avalonia Linux OpenGL presentation failed: {0}",
            exception);
        var failure = _failureTracker.Register(exception);
        Dispatcher.UIThread.Post(
            () => PresentationFailed?.Invoke(this, failure),
            DispatcherPriority.Render);
    }

    private static string CreateVertexShader(bool openGlEs) =>
        (openGlEs ? "#version 300 es\n" : "#version 150\n") +
        "in vec2 aPosition;\n" +
        "in vec2 aTextureCoordinate;\n" +
        "out vec2 vTextureCoordinate;\n" +
        "void main() {\n" +
        "  vTextureCoordinate = aTextureCoordinate;\n" +
        "  gl_Position = vec4(aPosition, 0.0, 1.0);\n" +
        "}\n";

    private static string CreateFragmentShader(bool openGlEs) =>
        (openGlEs
            ? "#version 300 es\nprecision mediump float;\n"
            : "#version 150\n") +
        "uniform sampler2D uTexture;\n" +
        "uniform int uSwapRedBlue;\n" +
        "in vec2 vTextureCoordinate;\n" +
        "out vec4 outputColor;\n" +
        "void main() {\n" +
        "  vec4 color = texture(uTexture, vTextureCoordinate);\n" +
        "  outputColor = uSwapRedBlue == 1 ? color.bgra : color;\n" +
        "}\n";

    private static string CreateDmaBufFragmentShader(bool openGlEs) =>
        (openGlEs
            ? "#version 300 es\nprecision mediump float;\n"
            : "#version 150\n") +
        "uniform sampler2D uTextureY;\n" +
        "uniform sampler2D uTextureUv;\n" +
        "in vec2 vTextureCoordinate;\n" +
        "out vec4 outputColor;\n" +
        "void main() {\n" +
        "  float y = 1.16438356 * (texture(uTextureY, vTextureCoordinate).r - 0.0627451);\n" +
        "  vec2 uv = texture(uTextureUv, vTextureCoordinate).rg - vec2(0.5019608);\n" +
        "  outputColor = vec4(y + 1.79274107 * uv.y, " +
        "y - 0.21324861 * uv.x - 0.53290933 * uv.y, " +
        "y + 2.11240179 * uv.x, 1.0);\n" +
        "}\n";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TexSubImage2DDelegate(
        int target,
        int level,
        int xOffset,
        int yOffset,
        int width,
        int height,
        int format,
        int type,
        IntPtr pixels);
}
