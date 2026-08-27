using Avalonia.Controls;
using Avalonia.Media;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class MediaPresentationCoordinator : IAsyncDisposable
{
    private readonly Action<MediaVideoPresentationMode?> _modeChanged;
    private readonly Action<Exception> _presentationFailed;
    private readonly SoftwareBitmapMediaOutput _softwareOutput = new();
#if !ANDROID
    private readonly WindowsD3D11CompositionMediaOutput _compositedOutput = new();
    private readonly WindowsD3D11MediaOutput _nativeOutput = new();
#endif
    private Control? _overlay;
    private bool _disposed;

    internal MediaPresentationCoordinator(
        Action<MediaVideoPresentationMode?> modeChanged,
        Action<Exception> presentationFailed)
    {
        _modeChanged = modeChanged;
        _presentationFailed = presentationFailed;
        Surface = new Grid();
        _softwareOutput.FramePresented += OnSoftwareFramePresented;
        Surface.Children.Add(_softwareOutput);
#if !ANDROID
        _compositedOutput.IsVisible = false;
        _compositedOutput.FramePresented += OnCompositedFramePresented;
        _compositedOutput.PresentationFailed += OnCompositedPresentationFailed;
        Surface.Children.Add(_compositedOutput);
        _nativeOutput.IsVisible = false;
        Surface.Children.Add(_nativeOutput);
#endif
    }

    internal Grid Surface { get; }

    internal IMediaVideoOutput Configure(
        MediaOpenOptions options,
        MediaVideoPresentationMode requestedMode,
        Stretch stretch)
    {
        var plan = MediaPresentationPolicy.Resolve(
            requestedMode,
            options,
            OperatingSystem.IsWindows(),
            _overlay is not null);
        SetStretch(stretch);
#if !ANDROID
        _compositedOutput.IsVisible = plan.UsesGpuComposition;
        _nativeOutput.IsVisible = plan.UsesNativeSurface;
        var primaryOutput = plan.UsesNativeSurface
            ? (IMediaVideoOutput)_nativeOutput
            : plan.UsesGpuComposition
                ? _compositedOutput
                : _softwareOutput;
        var output = plan.UsesNativeSurface || plan.UsesGpuComposition
            ? new AdaptiveMediaVideoOutput(primaryOutput, _softwareOutput)
            : primaryOutput;
#else
        var output = (IMediaVideoOutput)_softwareOutput;
#endif
        _softwareOutput.IsVisible =
            !plan.UsesNativeSurface && !plan.UsesGpuComposition;
        _modeChanged(plan.EffectiveMode);
        return output;
    }

    internal void SetStretch(Stretch stretch)
    {
        _softwareOutput.Stretch = stretch;
#if !ANDROID
        _compositedOutput.Stretch = stretch;
        _nativeOutput.Stretch = stretch;
#endif
    }

    internal void SetOverlay(Control? overlay)
    {
        if (_overlay is not null)
        {
            Surface.Children.Remove(_overlay);
        }

        _overlay = overlay;
        if (_overlay is not null)
        {
            Surface.Children.Add(_overlay);
        }
    }

    internal void Reset()
    {
        _softwareOutput.Clear();
#if !ANDROID
        _compositedOutput.Clear();
        _compositedOutput.IsVisible = false;
        _nativeOutput.ClearPendingFrame();
        _nativeOutput.IsVisible = false;
#endif
        _softwareOutput.IsVisible = true;
        _modeChanged(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _softwareOutput.FramePresented -= OnSoftwareFramePresented;
        _softwareOutput.Dispose();
#if !ANDROID
        _compositedOutput.FramePresented -= OnCompositedFramePresented;
        _compositedOutput.PresentationFailed -= OnCompositedPresentationFailed;
        await _compositedOutput.DisposeAsync();
        _nativeOutput.Dispose();
#endif
    }

    private void OnSoftwareFramePresented(object? sender, EventArgs args)
    {
#if !ANDROID
        _compositedOutput.IsVisible = false;
        _nativeOutput.IsVisible = false;
#endif
        _softwareOutput.IsVisible = true;
        _modeChanged(MediaVideoPresentationMode.SoftwareBitmap);
    }

#if !ANDROID
    private void OnCompositedFramePresented(object? sender, EventArgs args)
    {
        _nativeOutput.IsVisible = false;
        _softwareOutput.IsVisible = false;
        _compositedOutput.IsVisible = true;
        _modeChanged(MediaVideoPresentationMode.GpuComposition);
    }

    private void OnCompositedPresentationFailed(object? sender, Exception exception) =>
        _presentationFailed(exception);
#endif
}
