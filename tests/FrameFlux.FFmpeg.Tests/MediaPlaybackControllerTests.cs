using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaPlaybackControllerTests
{
    [Theory]
    [InlineData(0.25d)]
    [InlineData(4d)]
    public async Task PlaybackRate_AcceptsSupportedBoundary(double playbackRate)
    {
        await using var controller = new MediaPlaybackController();

        controller.PlaybackRate = playbackRate;

        Assert.Equal(playbackRate, controller.PlaybackRate);
    }

    [Theory]
    [InlineData(0.249d)]
    [InlineData(4.001d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task PlaybackRate_RejectsUnsupportedValue(double playbackRate)
    {
        await using var controller = new MediaPlaybackController();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => controller.PlaybackRate = playbackRate);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(playbackRate, exception.ActualValue);
    }

    [Fact]
    public async Task StartAsync_RequiresPlayerFactory()
    {
        await using var controller = new MediaPlaybackController();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync(
                null,
                MediaSource.Parse("rtsp://localhost/live"),
                new MediaOpenOptions(),
                new TestVideoOutput()).AsTask());

        Assert.Contains("PlayerFactory", exception.Message);
        Assert.Equal(MediaPlaybackState.Faulted, controller.State);
        Assert.Equal("OpenFailed", controller.LastError?.Code);
    }

    [Fact]
    public async Task StartAndStop_OwnPlayerLifecycle()
    {
        var player = new TestMediaPlayer();
        var output = new TestVideoOutput();
        await using var controller = new MediaPlaybackController
        {
            Volume = 0.4,
            IsMuted = true
        };

        await controller.StartAsync(
            new TestMediaPlayerFactory(player),
            MediaSource.Parse("rtsp://localhost/live"),
            new MediaOpenOptions(),
            output);
        await controller.StopAsync();

        Assert.Equal(1, player.OpenCount);
        Assert.Equal(1, player.PlayCount);
        Assert.Equal(1, player.StopCount);
        Assert.Equal(1, player.DisposeCount);
        Assert.Same(output, player.VideoOutput);
        Assert.Equal(0.4, player.Volume);
        Assert.True(player.IsMuted);
        Assert.Equal(MediaPlaybackState.Stopped, controller.State);
    }

    [Fact]
    public async Task FrameReceived_SubscribesToPlayerOnlyWhenObserved()
    {
        var player = new TestMediaPlayer();
        await using var controller = new MediaPlaybackController();
        EventHandler<MediaVideoFrame> handler = (_, _) => { };
        controller.FrameReceived += handler;

        await controller.StartAsync(
            new TestMediaPlayerFactory(player),
            MediaSource.Parse("rtsp://localhost/live"),
            new MediaOpenOptions(),
            new TestVideoOutput());
        Assert.Equal(1, player.FrameSubscriberCount);

        controller.FrameReceived -= handler;
        Assert.Equal(0, player.FrameSubscriberCount);
    }

    [Fact]
    public async Task RefreshDiagnostics_ReadsCurrentPlayerDiagnostics()
    {
        var player = new TestMediaPlayer();
        await using var controller = new MediaPlaybackController();
        await controller.StartAsync(
            new TestMediaPlayerFactory(player),
            MediaSource.Parse("rtsp://localhost/live"),
            new MediaOpenOptions(),
            new TestVideoOutput());

        player.Diagnostics = new MediaDiagnostics(
            true,
            "D3D11VA active",
            0,
            0,
            1,
            null);

        var diagnostics = controller.RefreshDiagnostics();

        Assert.True(diagnostics.IsHardwareVideoDecodingActive);
        Assert.Equal("D3D11VA active", controller.Diagnostics.VideoDecoderDiagnostics);
    }

    [Fact]
    public async Task StartAsync_DisposesPlayerWhenConfigurationFails()
    {
        var player = new TestMediaPlayer
        {
            ThrowWhenSettingVolume = true
        };
        await using var controller = new MediaPlaybackController();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartAsync(
                new TestMediaPlayerFactory(player),
                MediaSource.Parse("rtsp://localhost/live"),
                new MediaOpenOptions(),
                new TestVideoOutput()).AsTask());

        Assert.Equal(1, player.StopCount);
        Assert.Equal(1, player.DisposeCount);
        Assert.False(controller.HasPlayer);
    }

    [Fact]
    public async Task StartAsync_CancellationDoesNotPublishOpenFailure()
    {
        var openStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new TestMediaPlayer
        {
            OpenOperation = async cancellationToken =>
            {
                openStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        await using var controller = new MediaPlaybackController();
        var errorCount = 0;
        controller.Error += (_, _) => errorCount++;
        using var cancellation = new CancellationTokenSource();

        var start = controller.StartAsync(
            new TestMediaPlayerFactory(player),
            MediaSource.Parse("rtsp://localhost/live"),
            new MediaOpenOptions(),
            new TestVideoOutput(),
            cancellation.Token).AsTask();
        await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, errorCount);
        Assert.Null(controller.LastError);
        Assert.Equal(MediaPlaybackState.Stopped, controller.State);
        Assert.Equal(1, player.DisposeCount);
    }

    [Fact]
    public async Task StopAsync_ForwardsCancellationAndStillDisposesPlayer()
    {
        var stopStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new TestMediaPlayer
        {
            StopOperation = async cancellationToken =>
            {
                stopStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        await using var controller = new MediaPlaybackController();
        await controller.StartAsync(
            new TestMediaPlayerFactory(player),
            MediaSource.Parse("rtsp://localhost/live"),
            new MediaOpenOptions(),
            new TestVideoOutput());
        using var cancellation = new CancellationTokenSource();

        var stop = controller.StopAsync(cancellation.Token).AsTask();
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.Equal(cancellation.Token, player.LastStopCancellationToken);
        Assert.Equal(1, player.DisposeCount);
        Assert.Equal(MediaPlaybackState.Stopped, controller.State);
    }

    private sealed class TestMediaPlayerFactory(IMediaPlayer player) : IMediaPlayerFactory
    {
        public IMediaPlayer Create() => player;
    }

    private sealed class TestVideoOutput : IMediaVideoOutput
    {
        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => true;

        public bool TryPresent(IMediaFrameLease frame) => false;
    }

    private sealed class TestMediaPlayer : IMediaPlayer
    {
        private EventHandler<MediaVideoFrame>? _frameReceived;
        private double _volume;

        public MediaSource? Source { get; private set; }

        public MediaOpenOptions Options { get; private set; } = new();

        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;

        public MediaCapabilities Capabilities { get; } = MediaCapabilities.None;

        public MediaDiagnostics Diagnostics { get; set; } = MediaDiagnostics.Empty;

        public double Volume
        {
            get => _volume;
            set
            {
                if (ThrowWhenSettingVolume)
                {
                    throw new InvalidOperationException("Volume configuration failed.");
                }

                _volume = value;
            }
        }

        public bool IsMuted { get; set; }

        public IMediaVideoOutput? VideoOutput { get; set; }

        public TimeSpan Position => TimeSpan.Zero;

        public TimeSpan? Duration => null;

        public int OpenCount { get; private set; }

        public int PlayCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int FrameSubscriberCount { get; private set; }

        public bool ThrowWhenSettingVolume { get; init; }

        public Func<CancellationToken, ValueTask>? OpenOperation { get; init; }

        public Func<CancellationToken, ValueTask>? StopOperation { get; init; }

        public CancellationToken LastStopCancellationToken { get; private set; }

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

        public event EventHandler<MediaPlaybackErrorEventArgs>? Error
        {
            add { }
            remove { }
        }

        public event EventHandler<MediaVideoFrame>? FrameReceived
        {
            add
            {
                _frameReceived += value;
                FrameSubscriberCount++;
            }
            remove
            {
                _frameReceived -= value;
                FrameSubscriberCount--;
            }
        }

        public ValueTask OpenAsync(
            MediaSource source,
            MediaOpenOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Source = source;
            Options = options ?? new MediaOpenOptions();
            OpenCount++;
            if (OpenOperation is not null)
            {
                return OpenOperation(cancellationToken);
            }

            ChangeState(MediaPlaybackState.Ready);
            return ValueTask.CompletedTask;
        }

        public ValueTask PlayAsync(CancellationToken cancellationToken = default)
        {
            PlayCount++;
            ChangeState(MediaPlaybackState.Playing);
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            LastStopCancellationToken = cancellationToken;
            if (StopOperation is not null)
            {
                return StopOperation(cancellationToken);
            }

            ChangeState(MediaPlaybackState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask SeekAsync(
            TimeSpan position,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MediaSnapshot?>(null);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        private void ChangeState(MediaPlaybackState state)
        {
            var oldState = State;
            State = state;
            StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
        }
    }
}
