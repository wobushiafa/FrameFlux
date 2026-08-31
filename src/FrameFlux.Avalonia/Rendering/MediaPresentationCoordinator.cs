using Avalonia.Controls;
using Avalonia.Media;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

internal sealed class MediaPresentationCoordinator : IAsyncDisposable
{
    private readonly Action<MediaVideoPresentationMode?> _modeChanged;
    private readonly Action<MediaPresentationFailure> _presentationFailed;
    private readonly SoftwareBitmapMediaOutput _softwareOutput = new();
    private readonly IAvaloniaPlatformMediaOutput? _gpuOutput;
    private readonly IAvaloniaPlatformMediaOutput? _nativeOutput;
    private Control? _overlay;
    private MediaVideoPresentationMode? _presentationFallbackMode;
    private bool _disposed;

    internal MediaPresentationCoordinator(
        Action<MediaVideoPresentationMode?> modeChanged,
        Action<MediaPresentationFailure> presentationFailed)
    {
        _modeChanged = modeChanged;
        _presentationFailed = presentationFailed;
        Surface = new Grid();
        _softwareOutput.FramePresented += OnSoftwareFramePresented;
        Surface.Children.Add(_softwareOutput);
        var platformOutputs = AvaloniaPlatformMediaOutputRegistry.TryCreate();
        _gpuOutput = platformOutputs.GpuComposition;
        _nativeOutput = platformOutputs.NativeSurface;
        if (_gpuOutput is not null)
        {
            _gpuOutput.Surface.IsVisible = false;
            _gpuOutput.FramePresented += OnGpuFramePresented;
            _gpuOutput.PresentationFailed += OnPlatformPresentationFailed;
            Surface.Children.Add(_gpuOutput.Surface);
        }
        if (_nativeOutput is not null)
        {
            _nativeOutput.Surface.IsVisible = false;
            _nativeOutput.FramePresented += OnNativeFramePresented;
            _nativeOutput.PresentationFailed += OnPlatformPresentationFailed;
            Surface.Children.Add(_nativeOutput.Surface);
        }
    }

    internal Grid Surface { get; }

    internal IMediaVideoOutput Configure(
        MediaOpenOptions options,
        MediaVideoPresentationMode requestedMode,
        Stretch stretch)
    {
        var presentationMode = _presentationFallbackMode ?? requestedMode;
        var platformGpuPresentationAvailable =
            presentationMode == MediaVideoPresentationMode.NativeSurface
                ? _nativeOutput is not null
                : _gpuOutput is not null;
        var plan = MediaPresentationPolicy.Resolve(
            presentationMode,
            options,
            platformGpuPresentationAvailable,
            _overlay is not null);
        SetStretch(stretch);
        if (_gpuOutput is not null)
        {
            _gpuOutput.Surface.IsVisible = plan.UsesGpuComposition;
        }
        if (_nativeOutput is not null)
        {
            _nativeOutput.Surface.IsVisible = plan.UsesNativeSurface;
        }
        var primaryOutput = plan.UsesNativeSurface
            ? (IMediaVideoOutput)_nativeOutput!
            : plan.UsesGpuComposition
                ? _gpuOutput!
                : _softwareOutput;
        var output = plan.UsesNativeSurface || plan.UsesGpuComposition
            ? new AdaptiveMediaVideoOutput(primaryOutput, _softwareOutput)
            : primaryOutput;
        _softwareOutput.IsVisible =
            !plan.UsesNativeSurface && !plan.UsesGpuComposition;
        _modeChanged(plan.EffectiveMode);
        return output;
    }

    internal void SetStretch(Stretch stretch)
    {
        _softwareOutput.Stretch = stretch;
        if (_gpuOutput is not null)
        {
            _gpuOutput.Stretch = stretch;
        }
        if (_nativeOutput is not null)
        {
            _nativeOutput.Stretch = stretch;
        }
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

    internal void ClearSoftwareFallback() => _presentationFallbackMode = null;

    internal void Reset()
    {
        _softwareOutput.Clear();
        _gpuOutput?.Clear();
        if (_gpuOutput is not null)
        {
            _gpuOutput.Surface.IsVisible = false;
        }
        _nativeOutput?.Clear();
        if (_nativeOutput is not null)
        {
            _nativeOutput.Surface.IsVisible = false;
        }
        _softwareOutput.IsVisible = true;
        _modeChanged(null);
    }

    internal async ValueTask ReleaseResourcesAsync()
    {
        if (_gpuOutput is not null)
        {
            await _gpuOutput.ReleaseResourcesAsync();
        }
        if (_nativeOutput is not null)
        {
            await _nativeOutput.ReleaseResourcesAsync();
        }
        Reset();
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
        if (_gpuOutput is not null)
        {
            _gpuOutput.FramePresented -= OnGpuFramePresented;
            _gpuOutput.PresentationFailed -= OnPlatformPresentationFailed;
            await _gpuOutput.DisposeAsync();
        }
        if (_nativeOutput is not null)
        {
            _nativeOutput.FramePresented -= OnNativeFramePresented;
            _nativeOutput.PresentationFailed -= OnPlatformPresentationFailed;
            await _nativeOutput.DisposeAsync();
        }
    }

    private void OnSoftwareFramePresented(object? sender, EventArgs args)
    {
        if (_gpuOutput is not null)
        {
            _gpuOutput.Surface.IsVisible = false;
        }
        if (_nativeOutput is not null)
        {
            _nativeOutput.Surface.IsVisible = false;
        }
        _softwareOutput.IsVisible = true;
        _modeChanged(MediaVideoPresentationMode.SoftwareBitmap);
    }

    private void OnGpuFramePresented(object? sender, EventArgs args)
    {
        if (_nativeOutput is not null)
        {
            _nativeOutput.Surface.IsVisible = false;
        }
        _softwareOutput.IsVisible = false;
        if (_gpuOutput is not null)
        {
            _gpuOutput.Surface.IsVisible = true;
        }
        _modeChanged(MediaVideoPresentationMode.GpuComposition);
    }

    private void OnNativeFramePresented(object? sender, EventArgs args)
    {
        if (_gpuOutput is not null)
        {
            _gpuOutput.Surface.IsVisible = false;
        }
        _softwareOutput.IsVisible = false;
        if (_nativeOutput is not null)
        {
            _nativeOutput.Surface.IsVisible = true;
        }
        _modeChanged(MediaVideoPresentationMode.NativeSurface);
    }

    private void OnPlatformPresentationFailed(
        object? sender,
        MediaPresentationFailure failure)
    {
        if (failure.RequiresSoftwareFallback)
        {
            _presentationFallbackMode = MediaPresentationFallbackPolicy.Resolve(
                ReferenceEquals(sender, _nativeOutput),
                _gpuOutput is not null);
        }

        _presentationFailed(failure);
    }
}
