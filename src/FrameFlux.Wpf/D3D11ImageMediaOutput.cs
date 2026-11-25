using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private readonly MediaPresentationFailureTracker _failureTracker = new();
    private readonly D3DImage _image = new();
    private readonly Action _presentPendingFrame;
    private IDirect3DDevice9Ex? _device;
    private IDirect3DQuery9? _completionQuery;
    private IDirect3DTexture9? _sharedTexture;
    private IDirect3DSurface9? _surface;
    private IDirect3DSurface9? _backBufferSurface;
    private long _generation;
    private int _width;
    private int _height;
    private bool _backBufferAttached;
    private bool _hadPresentationFailure;
    private bool _presentationNotified;
    private bool _disposed;

    internal D3D11ImageMediaOutput()
    {
        _presentPendingFrame = PresentPendingFrame;
        Source = _image;
        IsHitTestVisible = false;
        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;

    internal event EventHandler? FramePresented;

    internal event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.D3D11Texture;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed || _failureTracker.IsExhausted ||
            !frame.TryGetD3D11Texture(out _))
        {
            return false;
        }

        if (!_frameSlot.TrySubmit(frame, out var schedulePresentation))
        {
            return false;
        }

        if (schedulePresentation)
        {
            SchedulePresentation();
        }

        return true;
    }

    internal void Clear()
    {
        _failureTracker.Reset();
        _presentationNotified = false;
        _frameSlot.ReleasePendingFrame();
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
        ReleaseCompletionQuery();
        _device = null;
        _texture.Dispose();
        Source = null;
        GC.SuppressFinalize(this);
    }

    private void SchedulePresentation()
    {
        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                _presentPendingFrame);
        }
        catch
        {
            _frameSlot.Clear();
        }
    }

    private void PresentPendingFrame()
    {
        var frame = _frameSlot.TakeForPresentation();
        if (frame is null)
        {
            if (_frameSlot.CompletePresentation())
            {
                SchedulePresentation();
            }
            return;
        }

        try
        {
            if (_disposed || !frame.TryGetD3D11Texture(out var sourceTexture))
            {
                return;
            }

            if (_texture.RequiresReset(
                    frame.Width,
                    frame.Height,
                    frame.Width,
                    frame.Height,
                    sourceTexture))
            {
                ResetPresentationResources();
            }

            if (!_texture.TryPresent(
                    frame.Width,
                    frame.Height,
                    frame.Width,
                    frame.Height,
                    sourceTexture,
                    out var compositionFrame))
            {
                return;
            }

            if (_surface is null ||
                _backBufferSurface is null ||
                _generation != compositionFrame.Generation)
            {
                DetachBackBuffer();
                ReleaseSharedSurface();
                OpenSharedSurface(compositionFrame);
            }

            if (_image.IsFrontBufferAvailable &&
                _device is not null &&
                _surface is not null &&
                _backBufferSurface is not null)
            {
                _image.Lock();
                try
                {
                    if (!_backBufferAttached)
                    {
                        AttachBackBufferLocked();
                    }

                    var rect = new Vortice.Direct3D9.Rect(0, 0, _width, _height);
                    _device.StretchRect(_surface, rect, _backBufferSurface, rect, TextureFilter.None);
                    WaitForD3D9Completion();
                    _image.AddDirtyRect(
                        new Int32Rect(0, 0, _width, _height));
                }
                finally
                {
                    _image.Unlock();
                }
            }

            if (_hadPresentationFailure)
            {
                _failureTracker.ReportSuccess();
                _hadPresentationFailure = false;
            }
            if (!_presentationNotified)
            {
                _presentationNotified = true;
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "WPF D3D11 composition presentation failed: {0}",
                exception);
            ResetPresentationResources();
            ReleaseCompletionQuery();
            _device = null;
            _presentationNotified = false;
            _hadPresentationFailure = true;
            PresentationFailed?.Invoke(this, _failureTracker.Register(exception));
        }
        finally
        {
            frame.Dispose();
            if (_frameSlot.CompletePresentation())
            {
                SchedulePresentation();
            }
        }
    }

    private void OpenSharedSurface(
        WindowsD3D11CompositionFrame compositionFrame)
    {
        if (_device is not null &&
            TryOpenSharedSurface(_device, compositionFrame))
        {
            _completionQuery ??= TryCreateCompletionQuery(_device);
            return;
        }

        ReleaseCompletionQuery();
        _device = null;

        var adapterCount = SharedD3D9DevicePool.GetAdapterCount();
        for (uint adapter = 0; adapter < adapterCount; adapter++)
        {
            try
            {
                var candidate = SharedD3D9DevicePool.GetOrCreateDevice(adapter);
                if (TryOpenSharedSurface(candidate, compositionFrame))
                {
                    _device = candidate;
                    _completionQuery = TryCreateCompletionQuery(candidate);
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
        }

        throw new InvalidOperationException(
            $"None of the {adapterCount} D3D9Ex adapters could open the D3D11 shared texture. " +
            "The decoder and WPF renderer may be using incompatible graphics adapters.");
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
            _backBufferSurface = device.CreateRenderTarget(
                checked((uint)compositionFrame.Width),
                checked((uint)compositionFrame.Height),
                Format.A8R8G8B8,
                MultisampleType.None,
                0,
                lockable: false);
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
        if (_disposed || !_image.IsFrontBufferAvailable || _backBufferSurface is null)
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
        if (_backBufferSurface is null || !_image.IsFrontBufferAvailable)
        {
            return;
        }

        _image.SetBackBuffer(
            D3DResourceType.IDirect3DSurface9,
            _backBufferSurface.NativePointer);
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

    private static IDirect3DQuery9? TryCreateCompletionQuery(IDirect3DDevice9Ex device)
    {
        try
        {
            return device.CreateQuery(QueryType.Event);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Unable to create D3D9 event completion query: {0}",
                exception.Message);
            return null;
        }
    }

    private void WaitForD3D9Completion()
    {
        var query = _completionQuery;
        if (query is null)
        {
            return;
        }

        query.Issue(Issue.End);
        var start = Stopwatch.GetTimestamp();
        var timeoutTicks = Stopwatch.Frequency / 10; // 100 ms guard
        var iteration = 0;
        while (true)
        {
            // Flush once on the initial query; subsequent polls avoid redundant D3D9 flushes.
            var flush = iteration == 0;
            if (query.GetData(out bool completed, flush) && completed)
            {
                return;
            }

            if (Stopwatch.GetTimestamp() - start >= timeoutTicks)
            {
                return;
            }

            if (iteration++ < 64)
            {
                Thread.SpinWait(32);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    private void ReleaseCompletionQuery()
    {
        _completionQuery?.Dispose();
        _completionQuery = null;
    }

    private void ReleaseSharedSurface()
    {
        _backBufferSurface?.Dispose();
        _backBufferSurface = null;
        _surface?.Dispose();
        _surface = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _generation = 0;
        _width = 0;
        _height = 0;
    }

    private static class SharedD3D9DevicePool
    {
        private static readonly object Sync = new();
        private static IDirect3D9Ex? _direct3D;
        private static readonly Dictionary<uint, IDirect3DDevice9Ex> Devices = [];

        internal static uint GetAdapterCount()
        {
            lock (Sync)
            {
                _direct3D ??= D3D9.Direct3DCreate9Ex();
                return _direct3D.AdapterCount;
            }
        }

        internal static IDirect3DDevice9Ex GetOrCreateDevice(uint adapter)
        {
            lock (Sync)
            {
                _direct3D ??= D3D9.Direct3DCreate9Ex();
                if (Devices.TryGetValue(adapter, out var existing))
                {
                    return existing;
                }

                var window = GetDesktopWindow();
                var parameters = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    DeviceWindowHandle = window,
                    PresentationInterval = PresentInterval.Default
                };
                var device = _direct3D.CreateDeviceEx(
                    adapter,
                    DeviceType.Hardware,
                    window,
                    CreateFlags.HardwareVertexProcessing |
                    CreateFlags.Multithreaded |
                    CreateFlags.FpuPreserve,
                    parameters);
                Devices[adapter] = device;
                return device;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
