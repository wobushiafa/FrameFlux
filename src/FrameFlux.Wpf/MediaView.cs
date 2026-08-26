using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameFlux.FFmpeg;

namespace FrameFlux.Wpf;

public sealed class MediaView : FrameworkElement, IAsyncDisposable
{
    private static readonly DependencyPropertyKey StatePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(State),
            typeof(MediaPlaybackState),
            typeof(MediaView),
            new FrameworkPropertyMetadata(MediaPlaybackState.Idle));

    private static readonly DependencyPropertyKey LastErrorPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(LastError),
            typeof(MediaPlaybackError),
            typeof(MediaView),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(MediaSource),
            typeof(MediaView),
            new FrameworkPropertyMetadata(null, OnRestartPropertyChanged));

    public static readonly DependencyProperty OpenOptionsProperty =
        DependencyProperty.Register(
            nameof(OpenOptions),
            typeof(MediaOpenOptions),
            typeof(MediaView),
            new FrameworkPropertyMetadata(new MediaOpenOptions(), OnRestartPropertyChanged),
            value => value is MediaOpenOptions);

    public static readonly DependencyProperty AutoPlayProperty =
        DependencyProperty.Register(
            nameof(AutoPlay),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(true, OnAutoPlayChanged));

    public static readonly DependencyProperty KeepPlaybackAliveWhenUnloadedProperty =
        DependencyProperty.Register(
            nameof(KeepPlaybackAliveWhenUnloaded),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty EnableAudioProperty =
        DependencyProperty.Register(
            nameof(EnableAudio),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(true, OnRestartPropertyChanged));

    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(
            nameof(Volume),
            typeof(double),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                1d,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnVolumeChanged),
            IsValidVolume);

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(
            nameof(IsMuted),
            typeof(bool),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsMutedChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                Stretch.Uniform,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background),
            typeof(Brush),
            typeof(MediaView),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;

    public static readonly DependencyProperty LastErrorProperty = LastErrorPropertyKey.DependencyProperty;

    private readonly IMediaPlayerFactory _playerFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _frameSync = new();
    private IMediaPlayer? _player;
    private MediaVideoFrame? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private bool _renderScheduled;
    private bool _isLoaded;
    private bool _disposed;

    public MediaView()
        : this(new FfmpegMediaPlayerFactory())
    {
    }

    internal MediaView(IMediaPlayerFactory playerFactory)
    {
        _playerFactory = playerFactory;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SnapsToDevicePixels = true;
    }

    public MediaSource? Source
    {
        get => (MediaSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public MediaOpenOptions OpenOptions
    {
        get => (MediaOpenOptions)GetValue(OpenOptionsProperty);
        set => SetValue(OpenOptionsProperty, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public bool KeepPlaybackAliveWhenUnloaded
    {
        get => (bool)GetValue(KeepPlaybackAliveWhenUnloadedProperty);
        set => SetValue(KeepPlaybackAliveWhenUnloadedProperty, value);
    }

    public bool EnableAudio
    {
        get => (bool)GetValue(EnableAudioProperty);
        set => SetValue(EnableAudioProperty, value);
    }

    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public MediaPlaybackState State => (MediaPlaybackState)GetValue(StateProperty);

    public MediaPlaybackError? LastError => (MediaPlaybackError?)GetValue(LastErrorProperty);

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? PlaybackError;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopSessionCoreAsync(setStoppedState: false);
            var source = Source ?? throw new InvalidOperationException("A media source is required before playback can start.");
            SetValue(LastErrorPropertyKey, null);
            SetState(MediaPlaybackState.Opening);
            var options = OpenOptions with
            {
                EnableAudio = EnableAudio
            };
            options.Validate();

            var player = _playerFactory.Create();
            player.Volume = Volume;
            player.IsMuted = IsMuted;
            player.StateChanged += OnPlayerStateChanged;
            player.Error += OnPlayerError;
            player.FrameReceived += OnFrameReceived;
            _player = player;
            try
            {
                await player.OpenAsync(source, options, cancellationToken);
                await player.PlayAsync(cancellationToken);
            }
            catch
            {
                await StopSessionCoreAsync(setStoppedState: false);
                throw;
            }
        }
        catch (Exception exception)
        {
            ReportError(new MediaPlaybackError(
                "OpenFailed",
                exception.Message,
                IsRecoverable: false,
                exception));
            SetState(MediaPlaybackState.Faulted);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopSessionCoreAsync(setStoppedState: true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        _player?.CaptureSnapshotAsync(cancellationToken) ?? ValueTask.FromResult<MediaSnapshot?>(null);

    public async ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        await StopAsync();
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Background, null, new Rect(RenderSize));
        if (_bitmap is null)
        {
            return;
        }

        drawingContext.DrawImage(_bitmap, CalculateDestinationRect(
            _bitmap.PixelWidth,
            _bitmap.PixelHeight,
            RenderSize,
            Stretch));
    }

    private static void OnRestartPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (view._isLoaded && (view.AutoPlay || view._player is not null))
        {
            _ = view.RestartSafelyAsync();
        }
    }

    private static void OnAutoPlayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (view._isLoaded && (bool)args.NewValue && view.Source is not null)
        {
            _ = view.StartSafelyAsync();
        }
    }

    private static void OnVolumeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (view._player is not null)
        {
            view._player.Volume = (double)args.NewValue;
        }
    }

    private static void OnIsMutedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (MediaView)sender;
        if (view._player is not null)
        {
            view._player.IsMuted = (bool)args.NewValue;
        }
    }

    private static bool IsValidVolume(object value) =>
        value is double volume && volume is >= 0d and <= 1d && !double.IsNaN(volume);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _isLoaded = true;
        if (AutoPlay && Source is not null)
        {
            _ = StartSafelyAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _isLoaded = false;
        if (!KeepPlaybackAliveWhenUnloaded)
        {
            _ = StopSafelyAsync();
        }
    }

    private async Task RestartSafelyAsync()
    {
        await StopSafelyAsync();
        if (_isLoaded && Source is not null)
        {
            await StartSafelyAsync();
        }
    }

    private async Task StartSafelyAsync()
    {
        try
        {
            await StartAsync();
        }
        catch
        {
        }
    }

    private async Task StopSafelyAsync()
    {
        try
        {
            await StopAsync();
        }
        catch (Exception exception)
        {
            ReportError(new MediaPlaybackError(
                "StopFailed",
                exception.Message,
                IsRecoverable: true,
                exception));
        }
    }

    private async Task StopSessionCoreAsync(bool setStoppedState)
    {
        var player = _player;
        _player = null;
        if (player is not null)
        {
            player.StateChanged -= OnPlayerStateChanged;
            player.Error -= OnPlayerError;
            player.FrameReceived -= OnFrameReceived;
            await player.StopAsync();
            await player.DisposeAsync();
        }

        lock (_frameSync)
        {
            _pendingFrame = null;
        }
        _bitmap = null;
        InvalidateVisual();
        if (setStoppedState)
        {
            SetState(MediaPlaybackState.Stopped);
        }
    }

    private void OnPlayerStateChanged(object? sender, MediaPlaybackStateChangedEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                if (ReferenceEquals(_player, sender))
                {
                    SetState(args.NewState);
                }
            }));

    private void OnPlayerError(object? sender, MediaPlaybackErrorEventArgs args) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                if (ReferenceEquals(_player, sender))
                {
                    ReportError(args.Error);
                }
            }));

    private void OnFrameReceived(object? sender, MediaVideoFrame frame)
    {
        if (!ReferenceEquals(_player, sender))
        {
            return;
        }

        lock (_frameSync)
        {
            _pendingFrame = frame;
            if (_renderScheduled)
            {
                return;
            }
            _renderScheduled = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderPendingFrame));
    }

    private void RenderPendingFrame()
    {
        MediaVideoFrame? frame;
        lock (_frameSync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }

        if (frame is null)
        {
            return;
        }

        RenderFrame(frame);
    }

    private unsafe void RenderFrame(MediaVideoFrame frame)
    {
        if (_bitmap is null ||
            _bitmap.PixelWidth != frame.Width ||
            _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
        }

        var rowBytes = checked(frame.Width * 4);
        _bitmap.Lock();
        try
        {
            var source = frame.Data.Span;
            for (var row = 0; row < frame.Height; row++)
            {
                source.Slice(row * frame.Stride, rowBytes).CopyTo(
                    new Span<byte>(
                        (byte*)_bitmap.BackBuffer + row * _bitmap.BackBufferStride,
                        rowBytes));
            }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
        }
        finally
        {
            _bitmap.Unlock();
        }

        InvalidateVisual();
    }

    private void SetState(MediaPlaybackState state)
    {
        var oldState = State;
        if (oldState == state)
        {
            return;
        }

        SetValue(StatePropertyKey, state);
        PlaybackStateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
    }

    private void ReportError(MediaPlaybackError error)
    {
        SetValue(LastErrorPropertyKey, error);
        PlaybackError?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }

    private static Rect CalculateDestinationRect(
        double sourceWidth,
        double sourceHeight,
        Size target,
        Stretch stretch)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return Rect.Empty;
        }

        if (stretch == Stretch.Fill)
        {
            return new Rect(target);
        }

        if (stretch == Stretch.None)
        {
            return new Rect(
                (target.Width - sourceWidth) / 2,
                (target.Height - sourceHeight) / 2,
                sourceWidth,
                sourceHeight);
        }

        var scaleX = target.Width / sourceWidth;
        var scaleY = target.Height / sourceHeight;
        var scale = stretch == Stretch.UniformToFill
            ? Math.Max(scaleX, scaleY)
            : Math.Min(scaleX, scaleY);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new Rect(
            (target.Width - width) / 2,
            (target.Height - height) / 2,
            width,
            height);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaView));
        }
    }
}
