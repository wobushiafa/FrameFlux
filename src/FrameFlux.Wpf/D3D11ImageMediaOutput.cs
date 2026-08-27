using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameFlux.Presentation;
using FrameFlux.Rendering.Windows;
using Vortice.Direct3D9;

namespace FrameFlux.Wpf;

internal sealed class D3D11ImageMediaOutput :
    Image,
    IMediaVideoOutput,
    IDisposable
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly WindowsD3D11CompositionTexture _texture = new(
        WindowsD3D11CompositionSynchronization.Shared);
    private readonly D3DImage _image = new();
    private IDirect3D9Ex? _direct3D;
    private IDirect3DDevice9Ex? _device;
    private IDirect3DTexture9? _sharedTexture;
    private IDirect3DSurface9? _surface;
    private long _generation;
    private int _width;
    private int _height;
    private bool _backBufferAttached;
    private bool _faulted;
    private bool _disposed;

    internal D3D11ImageMediaOutput()
    {
        Source = _image;
        IsHitTestVisible = false;
        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;

    internal event EventHandler? FramePresented;

    internal event EventHandler<Exception>? PresentationFailed;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.D3D11Texture;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed || _faulted || !frame.TryGetD3D11Texture(out _))
        {
            return false;
        }

        if (!_frameSlot.TrySubmit(frame, out var schedulePresentation))
        {
            return false;
        }

        if (schedulePresentation)
        {
            try
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(PresentPendingFrame));
            }
            catch
            {
                _frameSlot.Clear();
            }
        }

        return true;
    }

    internal void Clear()
    {
        _faulted = false;
        _frameSlot.Clear();
        ResetPresentationResources();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        _frameSlot.Dispose();
        ResetPresentationResources();
        _device?.Dispose();
        _device = null;
        _direct3D?.Dispose();
        _direct3D = null;
        _texture.Dispose();
        Source = null;
        GC.SuppressFinalize(this);
    }

    private void PresentPendingFrame()
    {
        var frame = _frameSlot.Take();
        if (frame is null)
        {
            return;
        }

        try
        {
            if (_disposed || !frame.TryGetD3D11Texture(out var sourceTexture))
            {
                return;
            }

            _image.Lock();
            try
            {
                if (_texture.RequiresReset(
                        frame.Width,
                        frame.Height,
                        sourceTexture))
                {
                    DetachBackBufferLocked();
                    ReleaseSharedSurface();
                    _texture.Reset();
                }

                if (!_texture.TryPresent(
                        frame.Width,
                        frame.Height,
                        sourceTexture,
                        out var compositionFrame))
                {
                    return;
                }

                if (_surface is null ||
                    _generation != compositionFrame.Generation)
                {
                    DetachBackBufferLocked();
                    ReleaseSharedSurface();
                    OpenSharedSurface(compositionFrame);
                    AttachBackBufferLocked();
                }

                if (_image.IsFrontBufferAvailable)
                {
                    _image.AddDirtyRect(
                        new Int32Rect(0, 0, _width, _height));
                }
            }
            finally
            {
                _image.Unlock();
            }

            FramePresented?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _faulted = true;
            System.Diagnostics.Trace.TraceError(
                "WPF D3D11 composition presentation failed: {0}",
                exception);
            ResetPresentationResources();
            PresentationFailed?.Invoke(this, exception);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void OpenSharedSurface(
        WindowsD3D11CompositionFrame compositionFrame)
    {
        _direct3D ??= D3D9.Direct3DCreate9Ex();
        if (_device is not null &&
            TryOpenSharedSurface(_device, compositionFrame))
        {
            return;
        }

        _device?.Dispose();
        _device = null;
        for (uint adapter = 0; adapter < _direct3D.AdapterCount; adapter++)
        {
            IDirect3DDevice9Ex? candidate = null;
            try
            {
                candidate = CreateDevice(adapter);
                if (TryOpenSharedSurface(candidate, compositionFrame))
                {
                    _device = candidate;
                    return;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Unable to open the D3D11 texture on D3D9 adapter {0}: {1}",
                    adapter,
                    exception.Message);
            }

            candidate?.Dispose();
        }

        throw new InvalidOperationException(
            "No D3D9Ex adapter could open the D3D11 shared texture.");
    }

    private IDirect3DDevice9Ex CreateDevice(uint adapter)
    {
        var window = GetDesktopWindow();
        var parameters = new PresentParameters
        {
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            DeviceWindowHandle = window,
            PresentationInterval = PresentInterval.Default
        };
        return _direct3D!.CreateDeviceEx(
            adapter,
            DeviceType.Hardware,
            window,
            CreateFlags.HardwareVertexProcessing |
            CreateFlags.Multithreaded |
            CreateFlags.FpuPreserve,
            parameters);
    }

    private bool TryOpenSharedSurface(
        IDirect3DDevice9Ex device,
        WindowsD3D11CompositionFrame compositionFrame)
    {
        var sharedHandle = compositionFrame.SharedHandle;
        try
        {
            _sharedTexture = device.CreateTexture(
                checked((uint)compositionFrame.Width),
                checked((uint)compositionFrame.Height),
                1,
                Usage.RenderTarget,
                Format.A8R8G8B8,
                Pool.Default,
                ref sharedHandle);
            _surface = _sharedTexture.GetSurfaceLevel(0);
            _generation = compositionFrame.Generation;
            _width = compositionFrame.Width;
            _height = compositionFrame.Height;
            return true;
        }
        catch
        {
            ReleaseSharedSurface();
            return false;
        }
    }

    private void OnFrontBufferAvailableChanged(
        object sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (_disposed || !_image.IsFrontBufferAvailable || _surface is null)
        {
            return;
        }

        _image.Lock();
        try
        {
            AttachBackBufferLocked();
            _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        }
        finally
        {
            _image.Unlock();
        }
    }

    private void AttachBackBufferLocked()
    {
        if (_surface is null || !_image.IsFrontBufferAvailable)
        {
            return;
        }

        _image.SetBackBuffer(
            D3DResourceType.IDirect3DSurface9,
            _surface.NativePointer);
        _backBufferAttached = true;
    }

    private void DetachBackBuffer()
    {
        _image.Lock();
        try
        {
            DetachBackBufferLocked();
        }
        finally
        {
            _image.Unlock();
        }
    }

    private void DetachBackBufferLocked()
    {
        if (!_backBufferAttached)
        {
            return;
        }

        _image.SetBackBuffer(
            D3DResourceType.IDirect3DSurface9,
            IntPtr.Zero);
        _backBufferAttached = false;
    }

    private void ResetPresentationResources()
    {
        DetachBackBuffer();
        ReleaseSharedSurface();
        _texture.Reset();
    }

    private void ReleaseSharedSurface()
    {
        _surface?.Dispose();
        _surface = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _generation = 0;
        _width = 0;
        _height = 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
