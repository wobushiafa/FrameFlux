using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace FrameFlux.Avalonia;

internal sealed class LinuxDmaBufEglInterop : IDisposable
{
    internal const uint DrmFormatNv12 = 0x3231564E;
    internal const uint DrmFormatP010 = 0x30313050;
    private const uint DrmFormatR8 = 0x20203852;
    private const uint DrmFormatGr88 = 0x38385247;
    private const uint DrmFormatR16 = 0x20363152;
    private const uint DrmFormatGr1616 = 0x32335247;
    private const int EglHeight = 0x3056;
    private const int EglWidth = 0x3057;
    private const int EglNone = 0x3038;
    private const int EglLinuxDmaBuf = 0x3270;
    private const int EglLinuxDrmFourcc = 0x3271;
    private const int EglDmaBufPlane0Fd = 0x3272;
    private const int EglDmaBufPlane0Offset = 0x3273;
    private const int EglDmaBufPlane0Pitch = 0x3274;
    private const int EglDmaBufPlane0ModifierLo = 0x3443;
    private const int EglDmaBufPlane0ModifierHi = 0x3444;
    private const int GlTexture2D = 0x0DE1;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const ulong DrmFormatModifierInvalid = 0x00FFFFFFFFFFFFFF;

    private readonly IntPtr _display;
    private readonly ILinuxDmaBufGlApi _gl;
    private readonly EglCreateImageDelegate _createImage;
    private readonly EglDestroyImageDelegate _destroyImage;
    private readonly GlEglImageTargetTexture2DDelegate _imageTargetTexture;
    private readonly bool _supportsModifiers;
    private int _textureY;
    private int _textureUv;
    private bool _disposed;

    internal LinuxDmaBufEglInterop(ILinuxDmaBufGlApi gl)
    {
        _gl = gl;
        _display = EglGetCurrentDisplay();
        if (_display == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException(
                "The active Avalonia OpenGL context is not backed by EGL.");
        }

        var extensions = Marshal.PtrToStringAnsi(EglQueryString(_display, 0x3055)) ?? string.Empty;
        if (!extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("EGL_EXT_image_dma_buf_import", StringComparer.Ordinal))
        {
            throw new PlatformNotSupportedException(
                "The active EGL display cannot import DMA-BUF images.");
        }
        _supportsModifiers = extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("EGL_EXT_image_dma_buf_import_modifiers", StringComparer.Ordinal);

        _createImage = LoadEgl<EglCreateImageDelegate>("eglCreateImageKHR");
        _destroyImage = LoadEgl<EglDestroyImageDelegate>("eglDestroyImageKHR");
        var imageTargetAddress = gl.GetProcAddress("glEGLImageTargetTexture2DOES");
        if (imageTargetAddress == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException(
                "The active OpenGL context cannot bind EGL images.");
        }
        _imageTargetTexture = Marshal.GetDelegateForFunctionPointer<
            GlEglImageTargetTexture2DDelegate>(imageTargetAddress);

        _textureY = CreateTexture(gl);
        _textureUv = CreateTexture(gl);
    }

    internal int TextureY => _textureY;

    internal int TextureUv => _textureUv;

    internal void Import(int width, int height, MediaDmaBufFrameBuffer dmaBuf)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var objects = dmaBuf.Objects.Span;
        var layers = dmaBuf.Layers.Span;
        MediaDmaBufPlane planeY;
        MediaDmaBufPlane planeUv;
        uint formatY;
        uint formatUv;

        if (layers.Length == 1 &&
            layers[0].Format is DrmFormatNv12 or DrmFormatP010 &&
            layers[0].Planes.Length == 2)
        {
            var planes = layers[0].Planes.Span;
            var tenBit = layers[0].Format == DrmFormatP010;
            planeY = planes[0];
            planeUv = planes[1];
            formatY = tenBit ? DrmFormatR16 : DrmFormatR8;
            formatUv = tenBit ? DrmFormatGr1616 : DrmFormatGr88;
        }
        else if (layers.Length == 2 &&
                 layers[0].Planes.Length == 1 &&
                 layers[1].Planes.Length == 1 &&
                 ((layers[0].Format == DrmFormatR8 &&
                   layers[1].Format == DrmFormatGr88) ||
                  (layers[0].Format == DrmFormatR16 &&
                   layers[1].Format == DrmFormatGr1616)))
        {
            planeY = layers[0].Planes.Span[0];
            planeUv = layers[1].Planes.Span[0];
            formatY = layers[0].Format;
            formatUv = layers[1].Format;
        }
        else
        {
            throw new NotSupportedException(
                $"Unsupported DRM PRIME layout: {DescribeLayers(layers)}.");
        }

        ImportPlane(_textureY, width, height, formatY, planeY, objects);
        ImportPlane(_textureUv, (width + 1) / 2, (height + 1) / 2,
            formatUv, planeUv, objects);
    }

    public void Dispose() => _disposed = true;

    internal void Release()
    {
        if (_textureY != 0)
        {
            _gl.DeleteTexture(_textureY);
            _textureY = 0;
        }
        if (_textureUv != 0)
        {
            _gl.DeleteTexture(_textureUv);
            _textureUv = 0;
        }
        _disposed = true;
    }

    private void ImportPlane(
        int texture,
        int width,
        int height,
        uint format,
        MediaDmaBufPlane plane,
        ReadOnlySpan<MediaDmaBufObject> objects)
    {
        if ((uint)plane.ObjectIndex >= (uint)objects.Length)
        {
            throw new InvalidOperationException("DMA-BUF plane references an invalid object.");
        }

        var item = objects[plane.ObjectIndex];
        var attributes = new List<int>
        {
            EglWidth, width,
            EglHeight, height,
            EglLinuxDrmFourcc, unchecked((int)format),
            EglDmaBufPlane0Fd, item.FileDescriptor,
            EglDmaBufPlane0Offset, plane.Offset,
            EglDmaBufPlane0Pitch, plane.Pitch
        };
        if (item.FormatModifier != DrmFormatModifierInvalid)
        {
            if (!_supportsModifiers && item.FormatModifier != 0)
            {
                throw new PlatformNotSupportedException(
                    "The DMA-BUF uses a modifier that the active EGL display cannot import.");
            }
            if (_supportsModifiers)
            {
                attributes.Add(EglDmaBufPlane0ModifierLo);
                attributes.Add(unchecked((int)item.FormatModifier));
                attributes.Add(EglDmaBufPlane0ModifierHi);
                attributes.Add(unchecked((int)(item.FormatModifier >> 32)));
            }
        }
        attributes.Add(EglNone);
        var image = _createImage(
            _display, IntPtr.Zero, EglLinuxDmaBuf, IntPtr.Zero, attributes.ToArray());
        if (image == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"EGL rejected DMA-BUF plane import (error 0x{EglGetError():X}).");
        }

        try
        {
            _gl.BindTexture(GlTexture2D, texture);
            _imageTargetTexture(GlTexture2D, image);
        }
        finally
        {
            _destroyImage(_display, image);
        }
    }

    private static int CreateTexture(ILinuxDmaBufGlApi gl)
    {
        var texture = gl.GenTexture();
        gl.BindTexture(GlTexture2D, texture);
        gl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        return texture;
    }

    private static string DescribeLayers(ReadOnlySpan<MediaDmaBufLayer> layers)
    {
        var descriptions = new string[layers.Length];
        for (var index = 0; index < layers.Length; index++)
        {
            descriptions[index] =
                $"layer {index} FourCC '{FormatFourCc(layers[index].Format)}' " +
                $"(0x{layers[index].Format:X8}), {layers[index].Planes.Length} plane(s)";
        }
        return descriptions.Length == 0
            ? "no layers"
            : string.Join("; ", descriptions);
    }

    private static string FormatFourCc(uint format) =>
        new(
        [
            (char)(format & 0xFF),
            (char)((format >> 8) & 0xFF),
            (char)((format >> 16) & 0xFF),
            (char)((format >> 24) & 0xFF)
        ]);

    private static T LoadEgl<T>(string name) where T : Delegate
    {
        var address = EglGetProcAddress(name);
        if (address == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException($"EGL does not expose {name}.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [DllImport("libEGL.so.1", EntryPoint = "eglGetCurrentDisplay")]
    private static extern IntPtr EglGetCurrentDisplay();

    [DllImport("libEGL.so.1", EntryPoint = "eglGetProcAddress")]
    private static extern IntPtr EglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libEGL.so.1", EntryPoint = "eglQueryString")]
    private static extern IntPtr EglQueryString(IntPtr display, int name);

    [DllImport("libEGL.so.1", EntryPoint = "eglGetError")]
    private static extern int EglGetError();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr EglCreateImageDelegate(
        IntPtr display,
        IntPtr context,
        int target,
        IntPtr buffer,
        int[] attributes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EglDestroyImageDelegate(IntPtr display, IntPtr image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlEglImageTargetTexture2DDelegate(int target, IntPtr image);
}

internal interface ILinuxDmaBufGlApi
{
    IntPtr GetProcAddress(string name);

    int GenTexture();

    void BindTexture(int target, int texture);

    void TexParameteri(int target, int name, int value);

    void DeleteTexture(int texture);
}

internal sealed class AvaloniaLinuxDmaBufGlApi(GlInterface gl) : ILinuxDmaBufGlApi
{
    public IntPtr GetProcAddress(string name) => gl.GetProcAddress(name);

    public int GenTexture() => gl.GenTexture();

    public void BindTexture(int target, int texture) => gl.BindTexture(target, texture);

    public void TexParameteri(int target, int name, int value) =>
        gl.TexParameteri(target, name, value);

    public void DeleteTexture(int texture) => gl.DeleteTexture(texture);
}
