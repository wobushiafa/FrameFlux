using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FfmpegMediaPlayerTests
{
    [Fact]
    public async Task GenericPlayer_TransitionsAndForwardsRuntimeControls()
    {
        var factory = new FakeMediaSessionFactory();
        await using var player = new FfmpegMediaPlayer(factory);
        var states = new List<MediaPlaybackState>();
        MediaVideoFrame? receivedFrame = null;
        player.StateChanged += (_, args) => states.Add(args.NewState);
        player.FrameReceived += (_, frame) => receivedFrame = frame;

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions
            {
                SessionSharing = MediaSessionSharingMode.Shared,
                Network = new MediaNetworkOptions
                {
                    LatencyMode = MediaLatencyMode.Low,
                    Transport = MediaTransport.Tcp
                },
                Video = new MediaVideoOptions
                {
                    SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame
                }
            });

        player.Volume = 0.25d;
        player.IsMuted = true;
        await player.PlayAsync();

        var session = Assert.IsType<FakeMediaSession>(factory.LastSession);
        Assert.Equal(0.25d, factory.LastVolume);
        Assert.True(factory.LastIsMuted);
        Assert.Equal(MediaSessionSharingMode.Shared, factory.LastOptions?.SessionSharing);
        Assert.Equal(MediaPlaybackState.Playing, player.State);
        Assert.NotNull(receivedFrame);
        Assert.Equal(MediaPixelFormat.Bgra32, receivedFrame.PixelFormat);

        player.Volume = 0.5d;
        player.IsMuted = false;
        Assert.Equal(0.5d, session.Volume);
        Assert.False(session.IsMuted);
        Assert.NotNull(await player.CaptureSnapshotAsync());

        await player.StopAsync();
        Assert.True(session.Disposed);
        Assert.Contains(MediaPlaybackState.Opening, states);
        Assert.Contains(MediaPlaybackState.Playing, states);
        Assert.Contains(MediaPlaybackState.Stopping, states);
    }

    [Fact]
    public async Task GenericPlayer_RejectsUnsupportedOperationsForLiveSource()
    {
        await using var player = new FfmpegMediaPlayer(new FakeMediaSessionFactory());
        await player.OpenAsync(MediaSource.Parse("rtsp://camera/live"));

        Assert.Throws<NotSupportedException>(() => player.PauseAsync());
        Assert.Throws<NotSupportedException>(() => player.SeekAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            player.OpenAsync(MediaSource.FromFile(Path.Combine(Path.GetTempPath(), "sample.mp4"))).AsTask());
    }

    [Fact]
    public void GenericOptions_RejectNegativeLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOpenOptions
        {
            Network = new MediaNetworkOptions
            {
                ReadTimeout = TimeSpan.FromMilliseconds(-1)
            }
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOpenOptions
        {
            Video = new MediaVideoOptions { MaximumWidth = -1 }
        }.Validate());
    }

    [Fact]
    public async Task GenericPlayer_ForwardsConfiguredVideoOutput()
    {
        var factory = new FakeMediaSessionFactory();
        var output = new FakeVideoOutput();
        await using var player = new FfmpegMediaPlayer(factory) { VideoOutput = output };

        await player.OpenAsync(MediaSource.Parse("rtsp://camera/live"));
        Assert.False(player.Capabilities.CanCaptureSnapshots);
        await player.PlayAsync();

        Assert.Same(output, factory.LastVideoOutput);
        Assert.Throws<InvalidOperationException>(() => player.VideoOutput = null);
    }

    [Fact]
    public async Task GenericPlayer_SubscribesToFramesOnlyWhileConsumersExist()
    {
        var factory = new FakeMediaSessionFactory();
        await using var player = new FfmpegMediaPlayer(factory)
        {
            VideoOutput = new FakeVideoOutput(MediaFrameStorageKind.CpuMemory)
        };

        await player.OpenAsync(MediaSource.Parse("rtsp://camera/live"));
        await player.PlayAsync();

        var session = Assert.IsType<FakeMediaSession>(factory.LastSession);
        Assert.Equal(0, session.FrameSubscriberCount);

        MediaVideoFrame? receivedFrame = null;
        EventHandler<MediaVideoFrame> handler = (_, frame) => receivedFrame = frame;
        player.FrameReceived += handler;
        Assert.Equal(1, session.FrameSubscriberCount);

        session.EmitFrame();
        Assert.NotNull(receivedFrame);

        player.FrameReceived -= handler;
        Assert.Equal(0, session.FrameSubscriberCount);
    }

    [Fact]
    public async Task GenericPlayer_DisablesCpuSnapshotsForGpuCompositionOutput()
    {
        await using var player = new FfmpegMediaPlayer(new FakeMediaSessionFactory())
        {
            VideoOutput = new FakeVideoOutput(MediaFrameStorageKind.D3D11Texture)
        };

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions
                {
                    SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame
                }
            });

        Assert.False(player.Capabilities.CanCaptureSnapshots);
    }

    [Fact]
    public async Task GenericPlayer_KeepsSnapshotsWhenRequestedGpuModeFallsBackToSoftware()
    {
        await using var player = new FfmpegMediaPlayer(new FakeMediaSessionFactory())
        {
            VideoOutput = new FakeVideoOutput(MediaFrameStorageKind.CpuMemory)
        };

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions
                {
                    SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame
                }
            });

        Assert.True(player.Capabilities.CanCaptureSnapshots);
    }

    [Fact]
    public async Task GenericPlayer_DisposesSessionWhenStopFails()
    {
        var factory = new FakeMediaSessionFactory();
        await using var player = new FfmpegMediaPlayer(factory);
        await player.OpenAsync(MediaSource.Parse("rtsp://camera/live"));
        await player.PlayAsync();
        var session = Assert.IsType<FakeMediaSession>(factory.LastSession);
        var expected = new InvalidOperationException("Stop failed.");
        session.StopException = expected;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.StopAsync().AsTask());

        Assert.Same(expected, actual);
        Assert.True(session.Disposed);
        Assert.Equal(MediaPlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task GenericPlayer_ContinuesNotifyingAfterSubscriberFailure()
    {
        var factory = new FakeMediaSessionFactory();
        await using var player = new FfmpegMediaPlayer(factory);
        var stateNotifications = 0;
        var frameNotifications = 0;
        var errorNotifications = 0;
        player.StateChanged += (_, _) => throw new InvalidOperationException("State subscriber failed.");
        player.StateChanged += (_, _) => stateNotifications++;
        player.FrameReceived += (_, _) => throw new InvalidOperationException("Frame subscriber failed.");
        player.FrameReceived += (_, _) => frameNotifications++;
        player.Error += (_, _) => throw new InvalidOperationException("Error subscriber failed.");
        player.Error += (_, _) => errorNotifications++;

        await player.OpenAsync(MediaSource.Parse("rtsp://camera/live"));
        await player.PlayAsync();
        var session = Assert.IsType<FakeMediaSession>(factory.LastSession);
        session.EmitError();

        Assert.True(stateNotifications > 0);
        Assert.Equal(1, frameNotifications);
        Assert.Equal(1, errorNotifications);
    }

    [Theory]
    [InlineData(MediaFrameStorageKind.D3D11Texture, true)]
    [InlineData(MediaFrameStorageKind.CpuMemory, false)]
    public void Session_ResolvesOutputStorageToFrameDeliveryMode(
        MediaFrameStorageKind storageKind,
        bool expectsD3D11Textures)
    {
        var deliveryMode = FfmpegMediaSession.ResolveFrameDeliveryMode(
            new FakeVideoOutput(storageKind));

        Assert.Equal(
            expectsD3D11Textures,
            deliveryMode == RtspFrameDeliveryMode.D3D11Texture);
    }

    private sealed class FakeMediaSessionFactory : IFfmpegMediaSessionFactory
    {
        internal IFfmpegMediaSession? LastSession { get; private set; }
        internal MediaOpenOptions? LastOptions { get; private set; }
        internal double LastVolume { get; private set; }
        internal bool LastIsMuted { get; private set; }
        internal IMediaVideoOutput? LastVideoOutput { get; private set; }

        public IFfmpegMediaSession Create(
            MediaSource source,
            MediaOpenOptions options,
            double volume,
            bool isMuted,
            IMediaVideoOutput? videoOutput)
        {
            LastOptions = options;
            LastVolume = volume;
            LastIsMuted = isMuted;
            LastVideoOutput = videoOutput;
            LastSession = new FakeMediaSession(source, options, volume, isMuted);
            return LastSession;
        }
    }

    private sealed class FakeMediaSession(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted) : IFfmpegMediaSession
    {
        public MediaSource Source { get; } = source;
        public MediaOpenOptions Options { get; } = options;
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public MediaDiagnostics Diagnostics { get; } = new(false, "Test", 1, 2, 3, null);
        public double Volume { get; set; } = volume;
        public bool IsMuted { get; set; } = isMuted;
        internal bool Disposed { get; private set; }
        internal Exception? StopException { get; set; }
        internal int FrameSubscriberCount =>
            _frameReceived?.GetInvocationList().Length ?? 0;
        private EventHandler<MediaVideoFrame>? _frameReceived;

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaPlaybackErrorEventArgs>? Error;
        public event EventHandler<MediaVideoFrame>? FrameReceived
        {
            add
            {
                _frameReceived += value;
            }
            remove
            {
                _frameReceived -= value;
            }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            TransitionTo(MediaPlaybackState.Opening);
            TransitionTo(MediaPlaybackState.Playing);
            EmitFrame();
            return ValueTask.CompletedTask;
        }

        internal void EmitFrame() =>
            _frameReceived?.Invoke(this, new MediaVideoFrame(
                new byte[16], 2, 2, 8, MediaPixelFormat.Bgra32, 1, DateTimeOffset.UtcNow));

        internal void EmitError() =>
            Error?.Invoke(this, new MediaPlaybackErrorEventArgs(new MediaPlaybackError(
                "Test",
                "Test error.",
                IsRecoverable: true,
                Exception: null)));

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            if (StopException is not null)
            {
                return ValueTask.FromException(StopException);
            }

            TransitionTo(MediaPlaybackState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MediaSnapshot?>(new MediaSnapshot(
                new byte[16], 2, 2, 8, MediaPixelFormat.Bgra32, DateTimeOffset.UtcNow));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private void TransitionTo(MediaPlaybackState state)
        {
            var oldState = State;
            State = state;
            StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
        }
    }

    private sealed class FakeVideoOutput : IMediaVideoOutput
    {
        internal FakeVideoOutput(
            MediaFrameStorageKind preferredFrameStorage = MediaFrameStorageKind.D3D11Texture)
        {
            PreferredFrameStorage = preferredFrameStorage;
        }

        public MediaFrameStorageKind PreferredFrameStorage { get; }
        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => true;
        public bool TryPresent(IMediaFrameLease frame)
        {
            frame.Dispose();
            return true;
        }
    }
}
