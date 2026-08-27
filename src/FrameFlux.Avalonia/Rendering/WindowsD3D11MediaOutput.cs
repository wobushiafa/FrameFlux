#if !ANDROID
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using FrameFlux.Presentation;
using FrameFlux.Rendering.Windows;

namespace FrameFlux.Avalonia;

internal sealed class WindowsD3D11MediaOutput : NativeControlHost, IMediaVideoOutput, IDisposable
{
    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly WindowsD3D11Presenter _presenter = new();
    private Stretch _stretch = Stretch.Uniform;
    private bool _disposed;

    public MediaRenderPreference Preference => MediaRenderPreference.NativeSurface;

    internal Stretch Stretch
    {
        get => _stretch;
        set => _stretch = value;
    }

    public bool Supports(MediaFramePixelFormat pixelFormat) =>
        pixelFormat == MediaFramePixelFormat.D3D11Texture;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (_disposed ||
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
            try
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    PresentPendingFrame,
                    global::Avalonia.Threading.DispatcherPriority.Render);
            }
            catch
            {
                ClearPendingFrame();
            }
        }

        return true;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var window = _presenter.CreateWindow(parent.Handle);
        return new PlatformHandle(window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _presenter.DestroyWindow();
        base.DestroyNativeControlCore(control);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameSlot.Dispose();
        _presenter.Dispose();
    }

    internal void ClearPendingFrame()
    {
        _frameSlot.Clear();
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
            if (_disposed || !frame.TryGetD3D11Texture(out var texture))
            {
                return;
            }

            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
            _presenter.Present(
                frame.Width,
                frame.Height,
                texture,
                (int)Math.Ceiling(Bounds.Width * scaling),
                (int)Math.Ceiling(Bounds.Height * scaling),
                ToMediaStretchMode(_stretch));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Avalonia D3D11 presentation failed: {0}",
                exception);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static MediaStretchMode ToMediaStretchMode(Stretch stretch) => stretch switch
    {
        Stretch.None => MediaStretchMode.None,
        Stretch.Fill => MediaStretchMode.Fill,
        Stretch.UniformToFill => MediaStretchMode.UniformToFill,
        _ => MediaStretchMode.Uniform
    };
}
#endif
