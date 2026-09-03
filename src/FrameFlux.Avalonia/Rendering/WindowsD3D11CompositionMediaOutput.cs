#if !ANDROID
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlux.Presentation;
using FrameFlux.Rendering.Windows;

namespace FrameFlux.Avalonia;

internal sealed class WindowsD3D11CompositionMediaOutput :
    Control,
    IAvaloniaPlatformMediaOutput
{
    private const ulong ProducerKey = 0;
    private const ulong ConsumerKey = 1;

    private readonly LatestMediaFrameSlot _frameSlot = new();
    private readonly WindowsD3D11CompositionTexture _texture = new();
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly MediaPresentationFailureTracker _failureTracker = new();
    private ICompositionGpuInterop? _gpuInterop;
    private ICompositionImportedGpuImage? _importedImage;
    private CompositionDrawingSurface? _drawingSurface;
    private CompositionSurfaceVisual? _surfaceVisual;
    private Stretch _stretch = Stretch.Uniform;
    private long _importedGeneration;
    private int _sourceWidth;
    private int _sourceHeight;

    private bool _hadPresentationFailure;
    private bool _surfaceIsVisible;
    private bool _gpuPresentationNotified;
    private bool _disposed;

    internal WindowsD3D11CompositionMediaOutput()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;

    public Control Surface => this;

    public Stretch Stretch
    {
        get => _stretch;
        set
        {
            _stretch = value;
            UpdateVisualLayout(Bounds.Size);
        }
    }

    public event EventHandler? FramePresented;

    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

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
            try
            {
                Dispatcher.UIThread.Post(
                    PresentPendingFrame,
                    DispatcherPriority.Render);
            }
            catch
            {
                _frameSlot.Clear();
            }
        }

        return true;
    }

    public void Clear()
    {
        _failureTracker.Reset();
        _frameSlot.Clear();

        _surfaceIsVisible = false;
        _gpuPresentationNotified = false;
        if (_surfaceVisual is not null)
        {
            _surfaceVisual.Visible = false;
        }
    }

    public async ValueTask ReleaseResourcesAsync()
    {
        Clear();
        await _presentationGate.WaitAsync();
        try
        {
            if (_surfaceVisual is not null)
            {
                _surfaceVisual.Visible = false;
            }

            await ReleaseImportedImageAsync();
            _drawingSurface?.Dispose();
            _drawingSurface = null;
            _surfaceVisual = null;
            _gpuInterop = null;
            _sourceWidth = 0;
            _sourceHeight = 0;
            _texture.Reset();
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameSlot.Dispose();
        await _presentationGate.WaitAsync();
        try
        {
            if (_surfaceVisual is not null)
            {
                _surfaceVisual.Visible = false;
            }

            await ReleaseImportedImageAsync();
            _drawingSurface?.Dispose();
            _drawingSurface = null;
            _surfaceVisual = null;
            _gpuInterop = null;
            _texture.Dispose();
        }
        finally
        {
            _presentationGate.Release();
        }
        GC.SuppressFinalize(this);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateVisualLayout(finalSize);
        return finalSize;
    }

    private async void PresentPendingFrame()
    {
        await _presentationGate.WaitAsync();
        IMediaFrameLease? frame = null;
        try
        {

            frame = _frameSlot.Take();
            if (frame is null)
            {
                return;
            }

            if (_disposed || !frame.TryGetD3D11Texture(out var texture))
            {
                return;
            }

            await EnsureCompositionAsync();
            if (_texture.RequiresReset(
                    frame.Width,
                    frame.Height,
                    frame.Width,
                    frame.Height,
                    texture))
            {
                await ReleaseImportedImageAsync();
                _texture.Reset();
            }

            if (!_texture.TryPresent(
                    frame.Width,
                    frame.Height,
                    frame.Width,
                    frame.Height,
                    texture,
                    out var compositionFrame))
            {
                return;
            }

            if (_importedImage is null ||
                _importedGeneration != compositionFrame.Generation)
            {
                await ReleaseImportedImageAsync();
                var handleType =
                    KnownPlatformGraphicsExternalImageHandleTypes
                        .D3D11TextureGlobalSharedHandle;
                _importedImage = _gpuInterop!.ImportImage(
                    new PlatformHandle(compositionFrame.SharedHandle, handleType),
                    new PlatformGraphicsExternalImageProperties
                    {
                        Width = compositionFrame.Width,
                        Height = compositionFrame.Height,
                        Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                        TopLeftOrigin = true
                    });
                _importedGeneration = compositionFrame.Generation;
                _sourceWidth = compositionFrame.Width;
                _sourceHeight = compositionFrame.Height;
                UpdateVisualLayout(Bounds.Size);
            }

            await _drawingSurface!.UpdateWithKeyedMutexAsync(
                _importedImage,
                checked((uint)ConsumerKey),
                checked((uint)ProducerKey));
            if (!_surfaceIsVisible)
            {
                _surfaceVisual!.Visible = true;
                _surfaceIsVisible = true;
            }


            if (_hadPresentationFailure)
            {
                _failureTracker.ReportSuccess();
                _hadPresentationFailure = false;
            }
            if (!_gpuPresentationNotified)
            {
                _gpuPresentationNotified = true;
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Avalonia D3D11 composition presentation failed: {0}",
                exception);
            await ReleaseImportedImageAsync();
            _texture.Reset();
            _surfaceIsVisible = false;
            _gpuPresentationNotified = false;
            _hadPresentationFailure = true;
            PresentationFailed?.Invoke(this, _failureTracker.Register(exception));
        }
        finally
        {
            frame?.Dispose();
            _presentationGate.Release();
        }
    }

    private async ValueTask EnsureCompositionAsync()
    {
        if (_gpuInterop is not null)
        {
            return;
        }

        var elementVisual = ElementComposition.GetElementVisual(this) ??
            throw new InvalidOperationException(
                "The composition output must be attached to a visual tree.");
        var compositor = elementVisual.Compositor;
        var gpuInterop = await compositor.TryGetCompositionGpuInterop();
        var handleType =
            KnownPlatformGraphicsExternalImageHandleTypes
                .D3D11TextureGlobalSharedHandle;
        if (gpuInterop is null ||
            !gpuInterop.SupportedImageHandleTypes.Contains(handleType))
        {
            throw new PlatformNotSupportedException(
                "The active Avalonia renderer cannot import D3D11 shared textures.");
        }

        _gpuInterop = gpuInterop;
        _drawingSurface = compositor.CreateDrawingSurface();
        _surfaceVisual = compositor.CreateSurfaceVisual();
        _surfaceVisual.Surface = _drawingSurface;
        _surfaceVisual.Visible = false;
        ElementComposition.SetElementChildVisual(this, _surfaceVisual);
    }

    private async ValueTask ReleaseImportedImageAsync()
    {
        if (_importedImage is not null)
        {
            await _importedImage.DisposeAsync();
            _importedImage = null;
        }

        _importedGeneration = 0;
    }

    private void UpdateVisualLayout(Size target)
    {
        if (_surfaceVisual is null ||
            _sourceWidth <= 0 ||
            _sourceHeight <= 0 ||
            target.Width <= 0 ||
            target.Height <= 0)
        {
            return;
        }

        var scaleX = target.Width / _sourceWidth;
        var scaleY = target.Height / _sourceHeight;
        if (_stretch == Stretch.Uniform)
        {
            scaleX = scaleY = Math.Min(scaleX, scaleY);
        }
        else if (_stretch == Stretch.UniformToFill)
        {
            scaleX = scaleY = Math.Max(scaleX, scaleY);
        }
        else if (_stretch == Stretch.None)
        {
            scaleX = scaleY = 1d;
        }

        var width = _sourceWidth * scaleX;
        var height = _sourceHeight * scaleY;
        _surfaceVisual.Size = new Vector(_sourceWidth, _sourceHeight);
        _surfaceVisual.Scale = new Vector3D(scaleX, scaleY, 1d);
        _surfaceVisual.Offset = new Vector3D(
            (target.Width - width) / 2d,
            (target.Height - height) / 2d,
            0d);
    }
}
#endif
