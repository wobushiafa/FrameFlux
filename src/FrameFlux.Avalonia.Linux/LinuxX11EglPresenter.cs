using System.Runtime.InteropServices;
using Avalonia.Media;

namespace FrameFlux.Avalonia;

internal sealed class LinuxX11EglPresenter : ILinuxDmaBufGlApi, IDisposable
{
    private const int EglNone = 0x3038;
    private const int EglRedSize = 0x3024;
    private const int EglGreenSize = 0x3023;
    private const int EglBlueSize = 0x3022;
    private const int EglAlphaSize = 0x3021;
    private const int EglSurfaceType = 0x3033;
    private const int EglWindowBit = 0x0004;
    private const int EglRenderableType = 0x3040;
    private const int EglNativeVisualId = 0x302E;
    private const int EglOpenGlEs3Bit = 0x0040;
    private const int EglContextClientVersion = 0x3098;
    private const int EglOpenGlEsApi = 0x30A0;
    private const long VisualIdMask = 0x1;
    private const uint InputOutput = 1;
    private const ulong CwBackPixel = 1UL << 1;
    private const ulong CwBorderPixel = 1UL << 3;
    private const ulong CwColormap = 1UL << 13;
    private const int GlArrayBuffer = 0x8892;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlDepthTest = 0x0B71;
    private const int GlCompileStatus = 0x8B81;
    private const int GlDynamicDraw = 0x88E8;
    private const int GlFloat = 0x1406;
    private const int GlFragmentShader = 0x8B30;
    private const int GlLinear = 0x2601;
    private const int GlLinkStatus = 0x8B82;
    private const int GlRgba = 0x1908;
    private const int GlTexture0 = 0x84C0;
    private const int GlTexture2D = 0x0DE1;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlClampToEdge = 0x812F;
    private const int GlTriangles = 0x0004;
    private const int GlUnpackRowLength = 0x0CF2;
    private const int GlUnsignedByte = 0x1401;
    private const int GlVertexShader = 0x8B31;

    private readonly float[] _vertices = new float[24];
    private IntPtr _xDisplay;
    private IntPtr _window;
    private IntPtr _colormap;
    private IntPtr _eglDisplay;
    private IntPtr _eglContext;
    private IntPtr _eglSurface;
    private NativeGl? _gl;
    private LinuxDmaBufEglInterop? _dmaBufInterop;
    private int _program;
    private int _cpuProgram;
    private int _vertexShader;
    private int _fragmentShader;
    private int _cpuFragmentShader;
    private int _cpuTexture;
    private int _cpuTextureWidth;
    private int _cpuTextureHeight;
    private byte[]? _stagingBuffer;
    private int _vertexBuffer;
    private int _vertexArray;
    private int _uTextureYLocation = -1;
    private int _uTextureUvLocation = -1;
    private int _uCpuTextureLocation = -1;
    private int _cachedSourceWidth;
    private int _cachedSourceHeight;
    private int _cachedTargetWidth;
    private int _cachedTargetHeight;
    private Stretch _cachedStretch;
    private bool _verticesUploaded;
    private bool _disposed;

    internal IntPtr CreateWindow(IntPtr parent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window != IntPtr.Zero)
        {
            return _window;
        }

        _xDisplay = XOpenDisplay(IntPtr.Zero);
        if (_xDisplay == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("Unable to open the X11 display.");
        }

        try
        {
            var config = InitializeEglDisplayAndChooseConfig();
            if (EglGetConfigAttrib(
                    _eglDisplay, config, EglNativeVisualId, out var visualId) == 0)
            {
                throw new PlatformNotSupportedException(
                    $"Unable to query the opaque EGL native visual (error 0x{EglGetError():X}).");
            }

            var visualTemplate = new XVisualInfo
            {
                VisualId = (UIntPtr)(uint)visualId
            };
            var visualInfos = XGetVisualInfo(
                _xDisplay, VisualIdMask, ref visualTemplate, out var visualCount);
            if (visualInfos == IntPtr.Zero || visualCount == 0)
            {
                throw new PlatformNotSupportedException(
                    $"Unable to resolve opaque X11 visual 0x{visualId:X}.");
            }

            XVisualInfo visualInfo;
            try
            {
                visualInfo = Marshal.PtrToStructure<XVisualInfo>(visualInfos);
            }
            finally
            {
                XFree(visualInfos);
            }

            _colormap = XCreateColormap(
                _xDisplay, parent, visualInfo.Visual, 0);
            if (_colormap == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Unable to create the opaque X11 video colormap.");
            }

            var attributes = new XSetWindowAttributes
            {
                BackgroundPixel = UIntPtr.Zero,
                BorderPixel = UIntPtr.Zero,
                Colormap = _colormap
            };
            _window = XCreateWindow(
                _xDisplay,
                parent,
                0,
                0,
                1,
                1,
                0,
                visualInfo.Depth,
                InputOutput,
                visualInfo.Visual,
                CwBackPixel | CwBorderPixel | CwColormap,
                ref attributes);
            if (_window == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unable to create the X11 video child window.");
            }

            XMapWindow(_xDisplay, _window);
            XFlush(_xDisplay);
            InitializeEglContext(config);
            return _window;
        }
        catch
        {
            DestroyWindow();
            throw;
        }
    }

    internal void Present(
        MediaDmaBufFrameBuffer dmaBuf,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Stretch stretch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MakeCurrent();
        _dmaBufInterop ??= new LinuxDmaBufEglInterop(this);
        _dmaBufInterop.Import(sourceWidth, sourceHeight, dmaBuf);

        var gl = _gl!;
        var clampedTargetWidth = Math.Max(1, targetWidth);
        var clampedTargetHeight = Math.Max(1, targetHeight);
        gl.Viewport(0, 0, clampedTargetWidth, clampedTargetHeight);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GlColorBufferBit);
        gl.Disable(GlDepthTest);
        gl.UseProgram(_program);
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _dmaBufInterop.TextureY);
        gl.Uniform1i(_uTextureYLocation, 0);
        gl.ActiveTexture(GlTexture0 + 1);
        gl.BindTexture(GlTexture2D, _dmaBufInterop.TextureUv);
        gl.Uniform1i(_uTextureUvLocation, 1);

        DrawQuadAndSwap(gl, sourceWidth, sourceHeight, clampedTargetWidth, clampedTargetHeight, stretch);
    }

    internal unsafe void PresentCpuBgra(
        MediaCpuFrameBuffer source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Stretch stretch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            source.Plane0 == IntPtr.Zero ||
            source.Plane0Stride < checked(sourceWidth * 4))
        {
            throw new InvalidOperationException(
                "The Linux X11 EGL presenter received an invalid BGRA frame.");
        }

        var rowBytes = checked(sourceWidth * 4);
        var requiredBytes = checked((long)source.Plane0Stride * (sourceHeight - 1) + rowBytes);
        if (source.Size > 0 && source.Size < requiredBytes)
        {
            throw new InvalidOperationException(
                "The Linux X11 EGL presenter received a truncated BGRA frame.");
        }

        MakeCurrent();
        var gl = _gl!;
        var clampedTargetWidth = Math.Max(1, targetWidth);
        var clampedTargetHeight = Math.Max(1, targetHeight);
        gl.Viewport(0, 0, clampedTargetWidth, clampedTargetHeight);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GlColorBufferBit);
        gl.Disable(GlDepthTest);

        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _cpuTexture);
        UploadCpuPixels(gl, source, sourceWidth, sourceHeight, rowBytes);

        gl.UseProgram(_cpuProgram);
        gl.Uniform1i(_uCpuTextureLocation, 0);
        DrawQuadAndSwap(gl, sourceWidth, sourceHeight, clampedTargetWidth, clampedTargetHeight, stretch);
    }

    private unsafe void UploadCpuPixels(
        NativeGl gl,
        MediaCpuFrameBuffer source,
        int width,
        int height,
        int rowBytes)
    {
        if (source.Plane0Stride == rowBytes)
        {
            UploadCpuTexture(gl, width, height, source.Plane0);
            return;
        }

        if ((source.Plane0Stride & 3) == 0)
        {
            gl.PixelStorei(GlUnpackRowLength, source.Plane0Stride >> 2);
            try
            {
                UploadCpuTexture(gl, width, height, source.Plane0);
            }
            finally
            {
                gl.PixelStorei(GlUnpackRowLength, 0);
            }
            return;
        }

        var packedSize = checked(rowBytes * height);
        if (_stagingBuffer is null || _stagingBuffer.Length < packedSize)
        {
            _stagingBuffer = GC.AllocateUninitializedArray<byte>(packedSize);
        }

        for (var row = 0; row < height; row++)
        {
            var srcRow = new ReadOnlySpan<byte>(
                (void*)(source.Plane0 + ((long)row * source.Plane0Stride)),
                rowBytes);
            srcRow.CopyTo(_stagingBuffer.AsSpan(row * rowBytes, rowBytes));
        }

        fixed (byte* packed = _stagingBuffer)
        {
            UploadCpuTexture(gl, width, height, (IntPtr)packed);
        }
    }

    private void UploadCpuTexture(NativeGl gl, int width, int height, IntPtr pixels)
    {
        if (_cpuTextureWidth != width || _cpuTextureHeight != height)
        {
            gl.TexImage2D(
                GlTexture2D,
                0,
                GlRgba,
                width,
                height,
                0,
                GlRgba,
                GlUnsignedByte,
                pixels);
            _cpuTextureWidth = width;
            _cpuTextureHeight = height;
            return;
        }

        gl.TexSubImage2D(
            GlTexture2D,
            0,
            0,
            0,
            width,
            height,
            GlRgba,
            GlUnsignedByte,
            pixels);
    }

    private void DrawQuadAndSwap(
        NativeGl gl,
        int sourceWidth,
        int sourceHeight,
        int clampedTargetWidth,
        int clampedTargetHeight,
        Stretch stretch)
    {
        gl.BindVertexArray(_vertexArray);
        gl.BindBuffer(GlArrayBuffer, _vertexBuffer);
        if (!_verticesUploaded ||
            _cachedSourceWidth != sourceWidth ||
            _cachedSourceHeight != sourceHeight ||
            _cachedTargetWidth != clampedTargetWidth ||
            _cachedTargetHeight != clampedTargetHeight ||
            _cachedStretch != stretch)
        {
            LinuxOpenGlMediaOutput.FillVertices(
                _vertices, sourceWidth, sourceHeight, clampedTargetWidth, clampedTargetHeight, stretch);
            gl.BufferData(GlArrayBuffer, _vertices, GlDynamicDraw);
            _cachedSourceWidth = sourceWidth;
            _cachedSourceHeight = sourceHeight;
            _cachedTargetWidth = clampedTargetWidth;
            _cachedTargetHeight = clampedTargetHeight;
            _cachedStretch = stretch;
            _verticesUploaded = true;
        }
        gl.VertexAttribPointer(0, 2, GlFloat, false, sizeof(float) * 4, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(
            1, 2, GlFloat, false, sizeof(float) * 4, (IntPtr)(sizeof(float) * 2));
        gl.EnableVertexAttribArray(1);
        gl.DrawArrays(GlTriangles, 0, 6);
        if (EglSwapBuffers(_eglDisplay, _eglSurface) == 0)
        {
            throw new InvalidOperationException(
                $"Unable to swap the X11 EGL video surface (error 0x{EglGetError():X}).");
        }
    }

    internal void Clear()
    {
        if (_eglDisplay == IntPtr.Zero || _eglSurface == IntPtr.Zero || _gl is null)
        {
            return;
        }
        MakeCurrent();
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(GlColorBufferBit);
        EglSwapBuffers(_eglDisplay, _eglSurface);
    }

    internal void DestroyWindow()
    {
        if (_eglDisplay != IntPtr.Zero)
        {
            if (_eglContext != IntPtr.Zero && _eglSurface != IntPtr.Zero)
            {
                EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext);
                _dmaBufInterop?.Release();
                _dmaBufInterop = null;
                ReleaseGlResources();
                EglMakeCurrent(_eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                _dmaBufInterop = null;
            }
            if (_eglSurface != IntPtr.Zero)
            {
                EglDestroySurface(_eglDisplay, _eglSurface);
                _eglSurface = IntPtr.Zero;
            }
            if (_eglContext != IntPtr.Zero)
            {
                EglDestroyContext(_eglDisplay, _eglContext);
                _eglContext = IntPtr.Zero;
            }
            EglTerminate(_eglDisplay);
            _eglDisplay = IntPtr.Zero;
        }
        if (_window != IntPtr.Zero && _xDisplay != IntPtr.Zero)
        {
            XDestroyWindow(_xDisplay, _window);
            _window = IntPtr.Zero;
        }
        if (_colormap != IntPtr.Zero && _xDisplay != IntPtr.Zero)
        {
            XFreeColormap(_xDisplay, _colormap);
            _colormap = IntPtr.Zero;
        }
        if (_xDisplay != IntPtr.Zero)
        {
            XCloseDisplay(_xDisplay);
            _xDisplay = IntPtr.Zero;
        }
        _gl = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        DestroyWindow();
        _disposed = true;
    }

    IntPtr ILinuxDmaBufGlApi.GetProcAddress(string name) => GetProcAddress(name);

    int ILinuxDmaBufGlApi.GenTexture() => _gl!.GenTexture();

    void ILinuxDmaBufGlApi.BindTexture(int target, int texture) =>
        _gl!.BindTexture(target, texture);

    void ILinuxDmaBufGlApi.TexParameteri(int target, int name, int value) =>
        _gl!.TexParameteri(target, name, value);

    void ILinuxDmaBufGlApi.DeleteTexture(int texture) => _gl!.DeleteTexture(texture);

    private IntPtr InitializeEglDisplayAndChooseConfig()
    {
        _eglDisplay = EglGetDisplay(_xDisplay);
        if (_eglDisplay == IntPtr.Zero || EglInitialize(_eglDisplay, out _, out _) == 0)
        {
            throw new PlatformNotSupportedException(
                $"Unable to initialize EGL for X11 (error 0x{EglGetError():X}).");
        }
        if (EglBindApi(EglOpenGlEsApi) == 0)
        {
            throw new PlatformNotSupportedException("EGL cannot bind the OpenGL ES API.");
        }

        var configAttributes = new[]
        {
            EglSurfaceType, EglWindowBit,
            EglRenderableType, EglOpenGlEs3Bit,
            EglRedSize, 8,
            EglGreenSize, 8,
            EglBlueSize, 8,
            EglAlphaSize, 0,
            EglNone
        };
        var configs = new IntPtr[64];
        if (EglChooseConfig(
                _eglDisplay, configAttributes, configs, configs.Length, out var count) == 0 ||
            count == 0)
        {
            throw new PlatformNotSupportedException("No compatible X11 EGL configuration was found.");
        }

        var config = IntPtr.Zero;
        for (var index = 0; index < Math.Min(count, configs.Length); index++)
        {
            if (EglGetConfigAttrib(
                    _eglDisplay, configs[index], EglAlphaSize, out var alphaSize) != 0 &&
                alphaSize == 0)
            {
                config = configs[index];
                break;
            }
        }
        if (config == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException(
                "The X11 EGL driver has no opaque window configuration.");
        }

        return config;
    }

    private void InitializeEglContext(IntPtr config)
    {
        _eglContext = EglCreateContext(
            _eglDisplay, config, IntPtr.Zero,
            [EglContextClientVersion, 3, EglNone]);
        if (_eglContext == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException(
                $"Unable to create the X11 EGL context (error 0x{EglGetError():X}).");
        }
        _eglSurface = EglCreateWindowSurface(
            _eglDisplay, config, _window, [EglNone]);
        if (_eglSurface == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException(
                $"Unable to create the X11 EGL window surface (error 0x{EglGetError():X}).");
        }

        MakeCurrent();
        _ = EglSwapInterval(_eglDisplay, 0);
        _gl = new NativeGl(GetProcAddress);
        InitializeGlResources();
    }

    private void InitializeGlResources()
    {
        var gl = _gl!;
        _vertexShader = gl.CompileShader(GlVertexShader, VertexShaderSource);
        _fragmentShader = gl.CompileShader(GlFragmentShader, FragmentShaderSource);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, _vertexShader);
        gl.AttachShader(_program, _fragmentShader);
        gl.BindAttribLocation(_program, 0, "aPosition");
        gl.BindAttribLocation(_program, 1, "aTextureCoordinate");
        gl.LinkProgram(_program);
        if (gl.GetProgramParameter(_program, GlLinkStatus) == 0)
        {
            throw new InvalidOperationException(
                $"Unable to link the X11 native video shader: {gl.GetProgramLog(_program)}");
        }
        _uTextureYLocation = gl.GetUniformLocation(_program, "uTextureY");
        _uTextureUvLocation = gl.GetUniformLocation(_program, "uTextureUv");

        _cpuFragmentShader = gl.CompileShader(GlFragmentShader, CpuBgraFragmentShaderSource);
        _cpuProgram = gl.CreateProgram();
        gl.AttachShader(_cpuProgram, _vertexShader);
        gl.AttachShader(_cpuProgram, _cpuFragmentShader);
        gl.BindAttribLocation(_cpuProgram, 0, "aPosition");
        gl.BindAttribLocation(_cpuProgram, 1, "aTextureCoordinate");
        gl.LinkProgram(_cpuProgram);
        if (gl.GetProgramParameter(_cpuProgram, GlLinkStatus) == 0)
        {
            throw new InvalidOperationException(
                $"Unable to link the X11 native BGRA video shader: {gl.GetProgramLog(_cpuProgram)}");
        }
        _uCpuTextureLocation = gl.GetUniformLocation(_cpuProgram, "uTexture");

        _cpuTexture = gl.GenTexture();
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _cpuTexture);
        gl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        _cpuTextureWidth = 0;
        _cpuTextureHeight = 0;

        _verticesUploaded = false;
        _vertexArray = gl.GenVertexArray();
        _vertexBuffer = gl.GenBuffer();
    }

    private void ReleaseGlResources()
    {
        if (_gl is null)
        {
            return;
        }
        if (_cpuTexture != 0) _gl.DeleteTexture(_cpuTexture);
        if (_vertexBuffer != 0) _gl.DeleteBuffer(_vertexBuffer);
        if (_vertexArray != 0) _gl.DeleteVertexArray(_vertexArray);
        if (_cpuProgram != 0) _gl.DeleteProgram(_cpuProgram);
        if (_program != 0) _gl.DeleteProgram(_program);
        if (_cpuFragmentShader != 0) _gl.DeleteShader(_cpuFragmentShader);
        if (_fragmentShader != 0) _gl.DeleteShader(_fragmentShader);
        if (_vertexShader != 0) _gl.DeleteShader(_vertexShader);
        _cpuTexture = _vertexBuffer = _vertexArray = _cpuProgram = _program = _cpuFragmentShader = _fragmentShader = _vertexShader = 0;
        _cpuTextureWidth = 0;
        _cpuTextureHeight = 0;
        _uTextureYLocation = -1;
        _uTextureUvLocation = -1;
        _uCpuTextureLocation = -1;
        _verticesUploaded = false;
    }

    private void MakeCurrent()
    {
        if (EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext) == 0)
        {
            throw new InvalidOperationException(
                $"Unable to activate the X11 EGL video context (error 0x{EglGetError():X}).");
        }
    }

    private static IntPtr GetProcAddress(string name) => EglGetProcAddress(name);

    private const string VertexShaderSource =
        "#version 300 es\n" +
        "in vec2 aPosition;\n" +
        "in vec2 aTextureCoordinate;\n" +
        "out vec2 vTextureCoordinate;\n" +
        "void main() { vTextureCoordinate = aTextureCoordinate; " +
        "gl_Position = vec4(aPosition, 0.0, 1.0); }\n";

    private const string FragmentShaderSource =
        "#version 300 es\nprecision mediump float;\n" +
        "uniform sampler2D uTextureY;\n" +
        "uniform sampler2D uTextureUv;\n" +
        "in vec2 vTextureCoordinate;\n" +
        "out vec4 outputColor;\n" +
        "void main() {\n" +
        "float y = 1.16438356 * (texture(uTextureY, vTextureCoordinate).r - 0.0627451);\n" +
        "vec2 uv = texture(uTextureUv, vTextureCoordinate).rg - vec2(0.5019608);\n" +
        "outputColor = vec4(y + 1.79274107 * uv.y, " +
        "y - 0.21324861 * uv.x - 0.53290933 * uv.y, " +
        "y + 2.11240179 * uv.x, 1.0); }\n";

    private const string CpuBgraFragmentShaderSource =
        "#version 300 es\nprecision mediump float;\n" +
        "uniform sampler2D uTexture;\n" +
        "in vec2 vTextureCoordinate;\n" +
        "out vec4 outputColor;\n" +
        "void main() { outputColor = texture(uTexture, vTextureCoordinate).bgra; }\n";

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XCreateWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, int depth, uint windowClass, IntPtr visual,
        ulong valueMask, ref XSetWindowAttributes attributes);

    [DllImport("libX11.so.6")]
    private static extern int XMapWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetVisualInfo(
        IntPtr display, long mask, ref XVisualInfo template, out int count);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XCreateColormap(
        IntPtr display, IntPtr window, IntPtr visual, int allocation);

    [DllImport("libX11.so.6")]
    private static extern int XFreeColormap(IntPtr display, IntPtr colormap);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libEGL.so.1", EntryPoint = "eglGetDisplay")]
    private static extern IntPtr EglGetDisplay(IntPtr nativeDisplay);

    [DllImport("libEGL.so.1", EntryPoint = "eglInitialize")]
    private static extern int EglInitialize(IntPtr display, out int major, out int minor);

    [DllImport("libEGL.so.1", EntryPoint = "eglBindAPI")]
    private static extern int EglBindApi(int api);

    [DllImport("libEGL.so.1", EntryPoint = "eglChooseConfig")]
    private static extern int EglChooseConfig(
        IntPtr display, int[] attributes, IntPtr[] configs, int configSize, out int count);

    [DllImport("libEGL.so.1", EntryPoint = "eglGetConfigAttrib")]
    private static extern int EglGetConfigAttrib(
        IntPtr display, IntPtr config, int attribute, out int value);

    [DllImport("libEGL.so.1", EntryPoint = "eglCreateContext")]
    private static extern IntPtr EglCreateContext(
        IntPtr display, IntPtr config, IntPtr shareContext, int[] attributes);

    [DllImport("libEGL.so.1", EntryPoint = "eglCreateWindowSurface")]
    private static extern IntPtr EglCreateWindowSurface(
        IntPtr display, IntPtr config, IntPtr window, int[] attributes);

    [DllImport("libEGL.so.1", EntryPoint = "eglMakeCurrent")]
    private static extern int EglMakeCurrent(
        IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [DllImport("libEGL.so.1", EntryPoint = "eglSwapBuffers")]
    private static extern int EglSwapBuffers(IntPtr display, IntPtr surface);

    [DllImport("libEGL.so.1", EntryPoint = "eglSwapInterval")]
    private static extern int EglSwapInterval(IntPtr display, int interval);

    [DllImport("libEGL.so.1", EntryPoint = "eglDestroySurface")]
    private static extern int EglDestroySurface(IntPtr display, IntPtr surface);

    [DllImport("libEGL.so.1", EntryPoint = "eglDestroyContext")]
    private static extern int EglDestroyContext(IntPtr display, IntPtr context);

    [DllImport("libEGL.so.1", EntryPoint = "eglTerminate")]
    private static extern int EglTerminate(IntPtr display);

    [DllImport("libEGL.so.1", EntryPoint = "eglGetProcAddress")]
    private static extern IntPtr EglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libEGL.so.1", EntryPoint = "eglGetError")]
    private static extern int EglGetError();

    [StructLayout(LayoutKind.Sequential)]
    private struct XVisualInfo
    {
        internal IntPtr Visual;
        internal UIntPtr VisualId;
        internal int Screen;
        internal int Depth;
        internal int Class;
        internal UIntPtr RedMask;
        internal UIntPtr GreenMask;
        internal UIntPtr BlueMask;
        internal int ColormapSize;
        internal int BitsPerRgb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        internal IntPtr BackgroundPixmap;
        internal UIntPtr BackgroundPixel;
        internal IntPtr BorderPixmap;
        internal UIntPtr BorderPixel;
        internal int BitGravity;
        internal int WinGravity;
        internal int BackingStore;
        internal UIntPtr BackingPlanes;
        internal UIntPtr BackingPixel;
        internal int SaveUnder;
        internal IntPtr EventMask;
        internal IntPtr DoNotPropagateMask;
        internal int OverrideRedirect;
        internal IntPtr Colormap;
        internal IntPtr Cursor;
    }

    private sealed class NativeGl
    {
        private readonly GetIntegerDelegate _getShaderIv;
        private readonly GetIntegerDelegate _getProgramIv;
        private readonly GetLogDelegate _getShaderInfoLog;
        private readonly GetLogDelegate _getProgramInfoLog;
        private readonly ShaderSourceDelegate _shaderSource;
        private readonly BufferDataDelegate _bufferData;
        private readonly VertexAttribPointerDelegate _vertexAttribPointer;
        private readonly GenObjectsDelegate _genBuffers;
        private readonly GenObjectsDelegate _genTextures;
        private readonly GenObjectsDelegate _genVertexArrays;
        private readonly DeleteObjectsDelegate _deleteBuffers;
        private readonly DeleteObjectsDelegate _deleteTextures;
        private readonly DeleteObjectsDelegate _deleteVertexArrays;

        internal NativeGl(Func<string, IntPtr> getProcAddress)
        {
            T Load<T>(string name) where T : Delegate
            {
                var address = getProcAddress(name);
                if (address == IntPtr.Zero)
                {
                    throw new PlatformNotSupportedException(
                        $"The OpenGL ES driver does not expose {name}.");
                }
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
            CreateShader = Load<CreateShaderDelegate>("glCreateShader");
            _shaderSource = Load<ShaderSourceDelegate>("glShaderSource");
            CompileShaderCore = Load<ObjectActionDelegate>("glCompileShader");
            _getShaderIv = Load<GetIntegerDelegate>("glGetShaderiv");
            _getShaderInfoLog = Load<GetLogDelegate>("glGetShaderInfoLog");
            CreateProgram = Load<CreateProgramDelegate>("glCreateProgram");
            AttachShader = Load<TwoObjectActionDelegate>("glAttachShader");
            BindAttribLocation = Load<BindAttribLocationDelegate>("glBindAttribLocation");
            LinkProgram = Load<ObjectActionDelegate>("glLinkProgram");
            _getProgramIv = Load<GetIntegerDelegate>("glGetProgramiv");
            _getProgramInfoLog = Load<GetLogDelegate>("glGetProgramInfoLog");
            UseProgram = Load<ObjectActionDelegate>("glUseProgram");
            GetUniformLocation = Load<GetLocationDelegate>("glGetUniformLocation");
            Uniform1i = Load<Uniform1iDelegate>("glUniform1i");
            ActiveTexture = Load<IntActionDelegate>("glActiveTexture");
            BindTexture = Load<TwoIntActionDelegate>("glBindTexture");
            TexParameteri = Load<ThreeIntActionDelegate>("glTexParameteri");
            TexImage2D = Load<TexImage2DDelegate>("glTexImage2D");
            TexSubImage2D = Load<TexSubImage2DDelegate>("glTexSubImage2D");
            PixelStorei = Load<TwoIntActionDelegate>("glPixelStorei");
            Disable = Load<IntActionDelegate>("glDisable");
            _genTextures = Load<GenObjectsDelegate>("glGenTextures");
            _deleteTextures = Load<DeleteObjectsDelegate>("glDeleteTextures");
            _genBuffers = Load<GenObjectsDelegate>("glGenBuffers");
            BindBuffer = Load<TwoIntActionDelegate>("glBindBuffer");
            _bufferData = Load<BufferDataDelegate>("glBufferData");
            _deleteBuffers = Load<DeleteObjectsDelegate>("glDeleteBuffers");
            _genVertexArrays = Load<GenObjectsDelegate>("glGenVertexArrays");
            BindVertexArray = Load<IntActionDelegate>("glBindVertexArray");
            _deleteVertexArrays = Load<DeleteObjectsDelegate>("glDeleteVertexArrays");
            _vertexAttribPointer = Load<VertexAttribPointerDelegate>("glVertexAttribPointer");
            EnableVertexAttribArray = Load<IntActionDelegate>("glEnableVertexAttribArray");
            DrawArrays = Load<ThreeIntActionDelegate>("glDrawArrays");
            Viewport = Load<FourIntActionDelegate>("glViewport");
            ClearColor = Load<ClearColorDelegate>("glClearColor");
            Clear = Load<IntActionDelegate>("glClear");
            Flush = Load<VoidActionDelegate>("glFlush");
            DeleteShader = Load<ObjectActionDelegate>("glDeleteShader");
            DeleteProgram = Load<ObjectActionDelegate>("glDeleteProgram");
        }

        internal CreateShaderDelegate CreateShader { get; }
        private ObjectActionDelegate CompileShaderCore { get; }
        internal CreateProgramDelegate CreateProgram { get; }
        internal TwoObjectActionDelegate AttachShader { get; }
        internal BindAttribLocationDelegate BindAttribLocation { get; }
        internal ObjectActionDelegate LinkProgram { get; }
        internal ObjectActionDelegate UseProgram { get; }
        internal GetLocationDelegate GetUniformLocation { get; }
        internal Uniform1iDelegate Uniform1i { get; }
        internal IntActionDelegate ActiveTexture { get; }
        internal TwoIntActionDelegate BindTexture { get; }
        internal ThreeIntActionDelegate TexParameteri { get; }
        internal TexImage2DDelegate TexImage2D { get; }
        internal TexSubImage2DDelegate TexSubImage2D { get; }
        internal TwoIntActionDelegate PixelStorei { get; }
        internal IntActionDelegate Disable { get; }
        internal TwoIntActionDelegate BindBuffer { get; }
        internal IntActionDelegate BindVertexArray { get; }
        internal IntActionDelegate EnableVertexAttribArray { get; }
        internal ThreeIntActionDelegate DrawArrays { get; }
        internal FourIntActionDelegate Viewport { get; }
        internal ClearColorDelegate ClearColor { get; }
        internal IntActionDelegate Clear { get; }
        internal VoidActionDelegate Flush { get; }
        internal ObjectActionDelegate DeleteShader { get; }
        internal ObjectActionDelegate DeleteProgram { get; }

        internal int CompileShader(int type, string source)
        {
            var shader = CreateShader(type);
            var sourcePointer = Marshal.StringToCoTaskMemUTF8(source);
            var pointers = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(pointers, sourcePointer);
                _shaderSource(shader, 1, pointers, IntPtr.Zero);
                CompileShaderCore(shader);
            }
            finally
            {
                Marshal.FreeHGlobal(pointers);
                Marshal.FreeCoTaskMem(sourcePointer);
            }
            if (GetShaderParameter(shader, GlCompileStatus) == 0)
            {
                throw new InvalidOperationException(
                    $"Unable to compile the X11 native video shader: {GetShaderLog(shader)}");
            }
            return shader;
        }

        internal int GetShaderParameter(int shader, int name) => GetInteger(_getShaderIv, shader, name);
        internal int GetProgramParameter(int program, int name) => GetInteger(_getProgramIv, program, name);
        internal string GetShaderLog(int shader) => GetLog(_getShaderInfoLog, shader);
        internal string GetProgramLog(int program) => GetLog(_getProgramInfoLog, program);
        internal int GenTexture() => Gen(_genTextures);
        internal int GenBuffer() => Gen(_genBuffers);
        internal int GenVertexArray() => Gen(_genVertexArrays);
        internal void DeleteTexture(int value) => Delete(_deleteTextures, value);
        internal void DeleteBuffer(int value) => Delete(_deleteBuffers, value);
        internal void DeleteVertexArray(int value) => Delete(_deleteVertexArrays, value);

        internal unsafe void BufferData(int target, float[] values, int usage)
        {
            fixed (float* pointer = values)
            {
                _bufferData(target, (IntPtr)(values.Length * sizeof(float)), (IntPtr)pointer, usage);
            }
        }

        internal void VertexAttribPointer(
            int index, int size, int type, bool normalized, int stride, IntPtr pointer) =>
            _vertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, pointer);

        private static int Gen(GenObjectsDelegate action)
        {
            action(1, out var value);
            return value;
        }

        private static void Delete(DeleteObjectsDelegate action, int value) => action(1, ref value);

        private static int GetInteger(GetIntegerDelegate action, int target, int name)
        {
            action(target, name, out var value);
            return value;
        }

        private static string GetLog(GetLogDelegate action, int target)
        {
            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                action(target, 4096, out var length, buffer);
                return length > 0 ? Marshal.PtrToStringUTF8(buffer, length) ?? string.Empty : string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateShaderDelegate(int type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateProgramDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ObjectActionDelegate(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TwoObjectActionDelegate(int first, int second);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void IntActionDelegate(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TwoIntActionDelegate(int first, int second);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ThreeIntActionDelegate(int first, int second, int third);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FourIntActionDelegate(int first, int second, int third, int fourth);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VoidActionDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ClearColorDelegate(float red, float green, float blue, float alpha);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Uniform1iDelegate(int location, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetLocationDelegate(int program, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void BindAttribLocationDelegate(int program, int index, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShaderSourceDelegate(int shader, int count, IntPtr strings, IntPtr lengths);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetIntegerDelegate(int target, int name, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetLogDelegate(int target, int capacity, out int length, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GenObjectsDelegate(int count, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteObjectsDelegate(int count, ref int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BufferDataDelegate(int target, IntPtr size, IntPtr data, int usage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VertexAttribPointerDelegate(int index, int size, int type, byte normalized, int stride, IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TexImage2DDelegate(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TexSubImage2DDelegate(int target, int level, int xOffset, int yOffset, int width, int height, int format, int type, IntPtr pixels);
    }
}
