using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlux.Presentation;

namespace FrameFlux.Avalonia;

// Keep ordinary Avalonia composition and fall back to snapshots if EGL interop fails.
internal sealed class WindowsD3D11DirectMediaOutput : Grid, IAvaloniaPlatformMediaOutput
{
    private readonly CompositorMediaFrameQueue _frames = new(TimeSpan.FromMilliseconds(50));
    private readonly object _submissionGate = new();
    private int _releaseDepth;
    private readonly Action _invalidate;
    private readonly Control _surface = new() { IsHitTestVisible = false };
    private WindowsD3D11DirectVisualHandler? _handler;
    private CompositionCustomVisual? _visual;
    private Compositor? _compositor;
    private WindowsD3D11CompositionMediaOutput? _fallback;
    private Stretch _stretch = Stretch.Uniform;
    private bool _disposed;
    private long _receivedFrames;

    public WindowsD3D11DirectMediaOutput()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        Children.Add(_surface);
        _invalidate = InvalidateFrame;
    }

    public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;
    public Control Surface => this;
    internal long SubmittedFrames => _handler?.SubmittedFrames ?? 0;
    internal bool IsUsingFallback => _fallback is not null;
    internal long ReceivedFrames => Interlocked.Read(ref _receivedFrames);
    internal long DrawCalls => _handler?.DrawCalls ?? 0;
    internal long AnimationTicks => _handler?.AnimationTicks ?? 0;
    internal long DroppedFrames => _frames.DroppedFrames;
    internal event Action<TimeSpan>? FrameRenderedForDiagnostics;
    public event EventHandler? FramePresented;
    public event Action<object?, MediaPresentationFailure>? PresentationFailed;

    public Stretch Stretch
    {
        get => _stretch;
        set
        {
            _stretch = value;
            if (_fallback is not null)
                _fallback.Stretch = value;
            _visual?.SendHandlerMessage(value);
        }
    }

    public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
        storageKind == MediaFrameStorageKind.D3D11Texture;

    public bool TryPresent(IMediaFrameLease frame)
    {
        if (!frame.TryGetD3D11Texture(out _))
            return false;
        bool schedule;
        lock (_submissionGate)
        {
            if (_disposed || _releaseDepth != 0)
                return false;
            var fallback = Volatile.Read(ref _fallback);
            if (fallback is not null)
                return fallback.TryPresent(frame);
            if (!_frames.TrySubmit(frame, out schedule))
                return false;
            Interlocked.Increment(ref _receivedFrames);
        }
        if (schedule)
            Dispatcher.UIThread.Post(_invalidate, DispatcherPriority.Render);
        return true;
    }

    private void InvalidateFrame()
    {
        if (_disposed || _fallback is not null || _releaseDepth != 0 || !_frames.HasPendingFrame)
            return;
        try
        {
            if (_visual is null)
            {
                _compositor = ElementComposition.GetElementVisual(_surface)?.Compositor ??
                    throw new InvalidOperationException("The GPU output is not attached.");
                _handler = new WindowsD3D11DirectVisualHandler(_frames,
                    () => Dispatcher.UIThread.Post(() =>
                    {
                        if (!_disposed && _fallback is null)
                            FramePresented?.Invoke(this, EventArgs.Empty);
                    }),
                    error => Dispatcher.UIThread.Post(() => UseSnapshotFallback(error)),
                    queuedFor => FrameRenderedForDiagnostics?.Invoke(queuedFor));
                _visual = _compositor.CreateCustomVisual(_handler);
                _visual.Size = new Vector(Bounds.Width, Bounds.Height);
                ElementComposition.SetElementChildVisual(_surface, _visual);
                _visual.SendHandlerMessage(_stretch);
            }
            _visual.Visible = true;
            _visual.SendHandlerMessage(WindowsD3D11DirectVisualHandler.RenderMessage);
        }
        catch (Exception error)
        {
            UseSnapshotFallback(error);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (_visual is not null)
            _visual.Size = new Vector(finalSize.Width, finalSize.Height);
        return result;
    }

    private async void UseSnapshotFallback(Exception error)
    {
        if (_disposed || _fallback is not null)
            return;
        System.Diagnostics.Trace.TraceWarning("Direct GPU drawing unavailable; using GPU snapshots: {0}", error);
        var fallback = new WindowsD3D11CompositionMediaOutput { Stretch = _stretch };
        fallback.FramePresented += (_, _) => FramePresented?.Invoke(this, EventArgs.Empty);
        fallback.PresentationFailed += (_, failure) => PresentationFailed?.Invoke(this, failure);
        Children.Add(fallback);
        Volatile.Write(ref _fallback, fallback);
        if (_visual is not null)
            _visual.Visible = false;
        // Stop accepting direct frames before draining a concurrently submitted frame.
        _frames.Dispose();
        try
        {
            await ReleaseDirectResourcesAsync();
        }
        catch (Exception releaseError)
        {
            System.Diagnostics.Trace.TraceError("Direct GPU cleanup failed: {0}", releaseError);
        }
    }

    public void Clear()
    {
        _frames.Clear();
        _fallback?.Clear();
        if (_visual is not null)
        {
            _visual.Visible = false;
            _visual.SendHandlerMessage(WindowsD3D11DirectVisualHandler.ClearMessage);
        }
    }

    public async ValueTask ReleaseResourcesAsync()
    {
        // A late decoder callback must not reserve a wakeup between Clear and
        // the compositor-thread release message. Reject it without taking ownership.
        lock (_submissionGate)
        {
            if (_disposed)
                return;
            _releaseDepth++;
        }
        try
        {
            Clear();
            await ReleaseDirectResourcesAsync();
            if (_fallback is not null)
                await _fallback.ReleaseResourcesAsync();
        }
        finally
        {
            lock (_submissionGate)
                _releaseDepth--;
        }
    }

    private async ValueTask ReleaseDirectResourcesAsync()
    {
        if (_handler is not null && _visual is not null)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _visual.SendHandlerMessage(completed);
            await completed.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_submissionGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _frames.Dispose();
        if (_visual is not null)
            _visual.Visible = false;
        await ReleaseDirectResourcesAsync();
        if (_fallback is not null)
            await _fallback.DisposeAsync();
    }
}
