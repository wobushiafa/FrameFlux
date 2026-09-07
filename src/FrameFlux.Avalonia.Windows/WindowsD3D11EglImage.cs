using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using FrameFlux.Rendering.Windows;
using Vortice.DXGI;

namespace FrameFlux.Avalonia;

// EGL_ANGLE_d3d_share_handle_client_buffer and EGL_ANGLE_keyed_mutex.
// The shared handle is borrowed from the producer, never closed here.
internal sealed class WindowsD3D11EglImage : IDisposable
{
    private const int Texture2D = 0x0DE1;
    private const int EglBackBuffer = 0x3084;
    private readonly EglContext _context;
    private EglSurface? _surface;
    private IDXGIKeyedMutex? _mutex;
    private AcquireSync? _acquire;
    internal int TextureId { get; private set; }
    internal int TextureType => Texture2D;
    internal int InternalFormat => 0x8058;
    internal int Width { get; }
    internal int Height { get; }

    internal WindowsD3D11EglImage(IGlContext context, WindowsD3D11CompositionFrame frame)
    {
        _context = context as EglContext ??
            throw new PlatformNotSupportedException("An EGL context is required for D3D11 textures.");
        Width = frame.Width;
        Height = frame.Height;
        try
        {
            _surface = _context.Display.CreatePBufferFromClientBuffer(0x3200, frame.SharedHandle,
                new[] { 0x3057, Width, 0x3056, Height, 0x3080, 0x305E, 0x3081, 0x305F, 0x3038 });
            var name = Marshal.StringToCoTaskMemUTF8("eglQuerySurfacePointerANGLE");
            IntPtr address;
            try { address = _context.EglInterface.GetProcAddress(name); }
            finally { Marshal.FreeCoTaskMem(name); }
            if (address == IntPtr.Zero)
                throw new PlatformNotSupportedException("ANGLE surface mutex queries are unavailable.");
            var query = Marshal.GetDelegateForFunctionPointer<QuerySurfacePointer>(address);
            if (query(_context.Display.Handle, _surface.DangerousGetHandle(), 0x33A2, out var mutex) == 0 ||
                mutex == IntPtr.Zero)
                throw new PlatformNotSupportedException("The EGL surface has no DXGI keyed mutex.");
            Marshal.AddRef(mutex);
            _mutex = new IDXGIKeyedMutex(mutex);
            _acquire = Marshal.GetDelegateForFunctionPointer<AcquireSync>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(mutex), 8 * IntPtr.Size));
            var gl = _context.GlInterface;
            gl.GetIntegerv(0x8069, out var previous);
            TextureId = gl.GenTexture();
            try
            {
                gl.BindTexture(Texture2D, TextureId);
                if (_context.EglInterface.BindTexImage(_context.Display.Handle,
                        _surface.DangerousGetHandle(), EglBackBuffer) == 0)
                    throw new OpenGlException("Unable to bind the D3D11 EGL surface.");
            }
            finally { gl.BindTexture(Texture2D, previous); }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void AcquireKeyedMutex(uint key)
    {
        var result = _acquire!(_mutex!.NativePointer, key, 0);
        // WAIT_TIMEOUT and WAIT_ABANDONED are non-negative, not successful locks.
        if (result != 0)
            throw new InvalidOperationException($"GPU texture lock unavailable: 0x{result:X8}.");
    }

    internal void ReleaseKeyedMutex(uint key) => _mutex!.ReleaseSync(key);

    public void Dispose()
    {
        if (!_context.IsLost && TextureId != 0)
        {
            using var current = _context.EnsureCurrent();
            _context.GlInterface.DeleteTexture(TextureId);
        }
        TextureId = 0;
        _surface?.Dispose();
        _surface = null;
        _mutex?.Dispose();
        _mutex = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AcquireSync(IntPtr mutex, ulong key, uint timeout);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QuerySurfacePointer(IntPtr display, IntPtr surface, int attribute, out IntPtr value);
}
