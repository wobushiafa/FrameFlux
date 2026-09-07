using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using FrameFlux.Presentation;
using FrameFlux.Rendering.Windows;
using SkiaSharp;

namespace FrameFlux.Avalonia;

// All texture access stays on the compositor thread. The original snapshot
// output remains the fallback for contexts without the required EGL extensions.
internal sealed class WindowsD3D11DirectVisualHandler : CompositionCustomVisualHandler
{
    internal static readonly object RenderMessage = new();
    internal static readonly object ClearMessage = new();
    private readonly CompositorMediaFrameQueue _frames;
    private readonly Action<TimeSpan> _frameRendered;
    private readonly Action _presented;
    private readonly Action<Exception> _failed;
    private readonly WindowsD3D11CompositionTexture _texture = new();
    private IGlContext? _context;
    private WindowsD3D11EglImage? _imported;
    private GRBackendTexture? _backend;
    private SKImage? _image;
    private SKPaint? _paint;
    private Stretch _stretch = Stretch.Uniform;
    private long _generation;
    private long _submittedFrames;
    private long _drawCalls;
    private long _animationTicks;
    internal long DrawCalls => Interlocked.Read(ref _drawCalls);
    internal long AnimationTicks => Interlocked.Read(ref _animationTicks);
    internal long SubmittedFrames => Interlocked.Read(ref _submittedFrames);
    private bool _notified;
    private bool _hasFrame;
    private bool _broken;
    private bool _running;
    private TimeSpan _lastFrameAt;

    internal WindowsD3D11DirectVisualHandler(CompositorMediaFrameQueue frames,
        Action presented, Action<Exception> failed, Action<TimeSpan> frameRendered)
    {
        _frames = frames;
        _presented = presented;
        _failed = failed;
        _frameRendered = frameRendered;
    }

    public override void OnMessage(object message)
    {
        if (message is TaskCompletionSource completed)
        {
            _running = false;
            try
            {
                ReleaseResources();
                completed.TrySetResult();
            }
            catch (Exception error)
            {
                completed.TrySetException(error);
            }
            return;
        }
        if (ReferenceEquals(message, ClearMessage))
        {
            _running = false;
            _hasFrame = false;
            _notified = false;
        }
        else if (ReferenceEquals(message, RenderMessage))
        {
            _lastFrameAt = CompositionNow;
            if (!_running)
            {
                _running = true;
                RegisterForNextAnimationFrameUpdate();
            }
        }
        else if (message is Stretch stretch)
            _stretch = stretch;
        Invalidate();
    }

    public override void OnAnimationFrameUpdate()
    {
        Interlocked.Increment(ref _animationTicks);
        if (!_running || _broken)
            return;
        if (_frames.HasPendingFrame)
            Invalidate();
        else if (CompositionNow - _lastFrameAt > TimeSpan.FromMilliseconds(250))
        {
            // Complete under the slot lock: a racing producer either wakes us
            // through the UI or leaves a pending frame reserved for this pump.
            _running = _frames.CompletePresentation();
            if (!_running)
                return;
            Invalidate();
        }
        RegisterForNextAnimationFrameUpdate();
    }

    public override void OnRender(ImmediateDrawingContext drawingContext)
    {
        Interlocked.Increment(ref _drawCalls);
        if (_broken)
            return;
        using var frame = _frames.TakeForPresentation(out var queuedFor);
        if (frame is not null)
            _lastFrameAt = CompositionNow;
        if (frame is null && !_hasFrame)
            return;
        try
        {
            var feature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>() ??
                throw new PlatformNotSupportedException("Skia drawing leases are unavailable.");
            using var lease = feature.Lease();
            var gr = lease.GrContext ??
                throw new PlatformNotSupportedException("A GPU Skia context is required.");
            var acquireKey = 0u;
            using (var api = lease.TryLeasePlatformGraphicsApi())
            {
                var context = api?.Context as IGlContext ??
                    throw new PlatformNotSupportedException("Direct D3D11 drawing requires ANGLE/OpenGL.");
                if (!ReferenceEquals(context, _context))
                {
                    ReleaseResources();
                    _context = context;
                }
                if (frame is not null)
                {
                    if (!frame.TryGetD3D11Texture(out var source))
                        throw new InvalidOperationException("Expected a D3D11 frame.");
                    if (_texture.RequiresReset(frame.Width, frame.Height, frame.Width, frame.Height, source))
                    {
                        ReleaseImportedImage();
                        _texture.Reset();
                    }
                    if (!_texture.TryPresent(frame.Width, frame.Height, frame.Width, frame.Height,
                            source, out var output))
                        return;
                    acquireKey = 1;
                    if (_imported is null || _generation != output.Generation)
                    {
                        ReleaseImportedImage();
                        _imported = new WindowsD3D11EglImage(context, output);
                        _generation = output.Generation;
                    }
                    _hasFrame = true;
                }
                if (_imported is null)
                    return;
                _imported.AcquireKeyedMutex(acquireKey);
            }
            try
            {
                if (_image is null)
                {
                    _backend = new GRBackendTexture(_imported.Width, _imported.Height,
                        false, new GRGlTextureInfo((uint)_imported.TextureType,
                            (uint)_imported.TextureId, (uint)_imported.InternalFormat));
                    // ANGLE exposes BGRA D3D storage as RGBA GL texels, as in Avalonia's importer.
                    _image = SKImage.FromTexture(gr, _backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888) ??
                        throw new InvalidOperationException("Skia could not wrap the imported texture.");
                }
                _paint ??= new SKPaint { IsAntialias = true };
                _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(255 * lease.CurrentOpacity, 0, 255));
                var size = new Size(_image.Width, _image.Height);
                var scale = _stretch.CalculateScaling(new Size(EffectiveSize.X, EffectiveSize.Y), size);
                var width = (float)(size.Width * scale.X);
                var height = (float)(size.Height * scale.Y);
                var x = ((float)EffectiveSize.X - width) / 2;
                var y = ((float)EffectiveSize.Y - height) / 2;
                lease.SkCanvas.DrawImage(_image, new SKRect(x, y, x + width, y + height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), _paint);
                // Submit the read before returning the shared texture to D3D11.
                gr.Flush();
            }
            finally
            {
                using var api = lease.TryLeasePlatformGraphicsApi();
                _context!.GlInterface.Flush();
                _imported!.ReleaseKeyedMutex(0);
            }
            if (!_notified)
            {
                _notified = true;
                System.Diagnostics.Trace.WriteLine("FrameFlux direct GPU drawing active (no composition snapshot).");
                _presented();
            }
            if (frame is not null)
            {
                Interlocked.Increment(ref _submittedFrames);
                _frameRendered(queuedFor);
            }
        }
        catch (Exception error)
        {
            _broken = true;
            _failed(error);
        }
    }

    internal void ReleaseResources()
    {
        if (_context is { IsLost: false })
        {
            using var current = _context.EnsureCurrent();
            ReleaseImportedImage();
        }
        else
            ReleaseImportedImage();
        _texture.Reset();
        _context = null;
        _hasFrame = false;
        _notified = false;
        _paint?.Dispose();
        _paint = null;
    }

    private void ReleaseImportedImage()
    {
        _image?.Dispose();
        _image = null;
        _backend?.Dispose();
        _backend = null;
        _imported?.Dispose();
        _imported = null;
        _generation = 0;
    }
}
