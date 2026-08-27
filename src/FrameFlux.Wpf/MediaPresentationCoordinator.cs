using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameFlux.Presentation;

namespace FrameFlux.Wpf;

internal sealed class MediaPresentationCoordinator : IDisposable
{
    private readonly Grid _surface;
    private readonly Action<MediaVideoPresentationMode?> _modeChanged;
    private readonly Action<Exception> _presentationFailed;
    private readonly SoftwareBitmapMediaOutput _softwareOutput = new();
    private readonly D3D11ImageMediaOutput _compositedOutput = new();
    private readonly D3D11SwapChainPresenter _nativePresenter = new();
    private bool _disposed;

    internal MediaPresentationCoordinator(
        Grid surface,
        Action<MediaVideoPresentationMode?> modeChanged,
        Action<Exception> presentationFailed)
    {
        _surface = surface;
        _modeChanged = modeChanged;
        _presentationFailed = presentationFailed;
        _softwareOutput.FramePresented += OnSoftwareFramePresented;
        _surface.Children.Add(_softwareOutput);
        _compositedOutput.Visibility = Visibility.Collapsed;
        _compositedOutput.FramePresented += OnCompositedFramePresented;
        _compositedOutput.PresentationFailed += OnCompositedPresentationFailed;
        _surface.Children.Add(_compositedOutput);
        _nativePresenter.Visibility = Visibility.Collapsed;
        _surface.Children.Add(_nativePresenter);
    }

    internal IMediaVideoOutput Configure(
        MediaOpenOptions options,
        MediaVideoPresentationMode requestedMode,
        Stretch stretch)
    {
        var plan = MediaPresentationPolicy.Resolve(
            requestedMode,
            options,
            OperatingSystem.IsWindows(),
            HasOverlayChildren());
        SetStretch(stretch);
        _nativePresenter.Visibility =
            plan.UsesNativeSurface ? Visibility.Visible : Visibility.Collapsed;
        _compositedOutput.Visibility =
            plan.UsesGpuComposition ? Visibility.Visible : Visibility.Collapsed;
        _softwareOutput.Visibility =
            plan.UsesNativeSurface || plan.UsesGpuComposition
                ? Visibility.Collapsed
                : Visibility.Visible;
        _modeChanged(plan.EffectiveMode);

        if (plan.UsesNativeSurface)
        {
            return new AdaptiveMediaVideoOutput(_nativePresenter, _softwareOutput);
        }

        return plan.UsesGpuComposition
            ? new AdaptiveMediaVideoOutput(_compositedOutput, _softwareOutput)
            : _softwareOutput;
    }

    internal void SetStretch(Stretch stretch)
    {
        _softwareOutput.Stretch = stretch;
        _compositedOutput.Stretch = stretch;
        _nativePresenter.SetStretch(stretch);
    }

    internal void Reset()
    {
        _softwareOutput.Clear();
        _compositedOutput.Clear();
        _compositedOutput.Visibility = Visibility.Collapsed;
        _nativePresenter.ClearPendingFrame();
        _nativePresenter.Visibility = Visibility.Collapsed;
        _softwareOutput.Visibility = Visibility.Visible;
        _modeChanged(null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _softwareOutput.FramePresented -= OnSoftwareFramePresented;
        _compositedOutput.FramePresented -= OnCompositedFramePresented;
        _compositedOutput.PresentationFailed -= OnCompositedPresentationFailed;
        _softwareOutput.Dispose();
        _compositedOutput.Dispose();
        _nativePresenter.Dispose();
    }

    private bool HasOverlayChildren() =>
        _surface.Children.Cast<UIElement>().Any(child =>
            child != _softwareOutput &&
            child != _compositedOutput &&
            child != _nativePresenter);

    private void OnSoftwareFramePresented(object? sender, EventArgs args)
    {
        _compositedOutput.Visibility = Visibility.Collapsed;
        _nativePresenter.Visibility = Visibility.Collapsed;
        _softwareOutput.Visibility = Visibility.Visible;
        _modeChanged(MediaVideoPresentationMode.SoftwareBitmap);
    }

    private void OnCompositedFramePresented(object? sender, EventArgs args)
    {
        _nativePresenter.Visibility = Visibility.Collapsed;
        _softwareOutput.Visibility = Visibility.Collapsed;
        _compositedOutput.Visibility = Visibility.Visible;
        _modeChanged(MediaVideoPresentationMode.GpuComposition);
    }

    private void OnCompositedPresentationFailed(object? sender, Exception exception) =>
        _presentationFailed(exception);
}
