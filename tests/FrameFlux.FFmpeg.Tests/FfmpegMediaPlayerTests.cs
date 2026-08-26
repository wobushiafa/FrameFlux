using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FfmpegMediaPlayerTests
{
    [Fact]
    public async Task GenericPlayer_TransitionsAndForwardsRuntimeAudioControls()
    {
        var factory = new FakeRtspSessionFactory();
        await using var player = new FfmpegMediaPlayer(factory);
        var states = new List<MediaPlaybackState>();
        MediaVideoFrame? receivedFrame = null;
        player.StateChanged += (_, args) => states.Add(args.NewState);
        player.FrameReceived += (_, frame) => receivedFrame = frame;

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions
            {
                LowLatency = true,
                Transport = MediaTransport.Tcp,
                StreamSharing = MediaStreamSharingMode.Shared
            });

        Assert.Equal(MediaPlaybackState.Ready, player.State);
        Assert.True(player.Capabilities.IsLive);
        Assert.False(player.Capabilities.CanPause);
        Assert.False(player.Capabilities.CanSeek);

        player.Volume = 0.25d;
        player.IsMuted = true;
        await player.PlayAsync();

        var session = Assert.IsType<FakeRtspSession>(factory.LastSession);
        Assert.Equal(0.25d, factory.LastOptions?.Volume);
        Assert.True(factory.LastOptions?.IsMuted);
        Assert.Equal(RtspStreamSharingMode.Shared, factory.LastOptions?.StreamSharing);
        Assert.Equal(MediaPlaybackState.Playing, player.State);
        Assert.NotNull(receivedFrame);
        Assert.Equal(MediaFramePixelFormat.Bgra32, receivedFrame.PixelFormat);

        player.Volume = 0.5d;
        player.IsMuted = false;
        Assert.Equal(0.5d, session.Volume);
        Assert.False(session.IsMuted);

        var snapshot = await player.CaptureSnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.Equal(MediaFramePixelFormat.Bgra32, snapshot.PixelFormat);

        await player.StopAsync();
        Assert.Equal(MediaPlaybackState.Stopped, player.State);
        Assert.True(session.Disposed);
        Assert.Contains(MediaPlaybackState.Opening, states);
        Assert.Contains(MediaPlaybackState.Playing, states);
        Assert.Contains(MediaPlaybackState.Stopping, states);
    }

    [Fact]
    public async Task GenericPlayer_RejectsUnsupportedOperationsForLiveRtsp()
    {
        await using var player = new FfmpegMediaPlayer(new FakeRtspSessionFactory());
        await player.OpenAsync(MediaSource.Parse("rtsp://camera/live"));

        Assert.Throws<NotSupportedException>(() => player.PauseAsync());
        Assert.Throws<NotSupportedException>(() => player.SeekAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            player.OpenAsync(MediaSource.FromFile(
                Path.Combine(Path.GetTempPath(), "sample.mp4"))).AsTask());
    }

    [Fact]
    public void GenericOptions_RejectNegativeLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOpenOptions
        {
            ReadTimeout = TimeSpan.FromMilliseconds(-1)
        }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOpenOptions
        {
            MaxVideoWidth = -1
        }.Validate());
    }

    [Fact]
    public async Task GenericPlayer_NormalizesNativeSurfaceToBgraRendering()
    {
        var factory = new FakeRtspSessionFactory();
        await using var player = new FfmpegMediaPlayer(factory);

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions
            {
                RenderPreference = MediaRenderPreference.NativeSurface
            });
        await player.PlayAsync();

        Assert.Equal(RtspRenderPreference.Software, factory.LastOptions?.RenderPreference);
    }

    [Fact]
    public void NativeSurfaceCapableUi_PreservesNativeRenderPreference()
    {
        var options = FfmpegMediaAdapter.ToRtspOptions(
            new MediaOpenOptions
            {
                RenderPreference = MediaRenderPreference.NativeSurface
            },
            volume: 1d,
            muted: false,
            supportsNativeSurface: true);

        Assert.Equal(RtspRenderPreference.NativeSurface, options.RenderPreference);
    }

    private sealed class FakeRtspSessionFactory : IRtspSessionFactory
    {
        internal IRtspSession? LastSession { get; private set; }
        internal RtspSessionOptions? LastOptions { get; private set; }

        public IRtspSession Create(RtspSource source, RtspSessionOptions? options = null)
        {
            LastOptions = options;
            LastSession = new FakeRtspSession(source, options ?? new RtspSessionOptions());
            return LastSession;
        }
    }

    private sealed class FakeRtspSession(
        RtspSource source,
        RtspSessionOptions options) : IRtspSession
    {
        public RtspSource Source { get; } = source;
        public RtspSessionOptions Options { get; } = options;
        public RtspSessionState State { get; private set; } = RtspSessionState.Idle;
        public RtspSessionDiagnostics Diagnostics { get; } =
            new(false, "Test", 1, 2, 3, null);
        public double Volume { get; set; } = options.Volume;
        public bool IsMuted { get; set; } = options.IsMuted;
        internal bool Disposed { get; private set; }

        public event EventHandler<RtspSessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<RtspSessionErrorEventArgs>? Error
        {
            add { }
            remove { }
        }
        public event EventHandler<RtspVideoFrame>? FrameReceived;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            TransitionTo(RtspSessionState.Connecting);
            TransitionTo(RtspSessionState.Connected);
            FrameReceived?.Invoke(this, new RtspVideoFrame(
                new byte[16],
                2,
                2,
                8,
                RtspFramePixelFormat.Bgra32,
                1,
                DateTimeOffset.UtcNow));
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            TransitionTo(RtspSessionState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<RtspSnapshot?> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RtspSnapshot?>(new RtspSnapshot(
                new byte[16],
                2,
                2,
                8,
                RtspFramePixelFormat.Bgra32,
                DateTimeOffset.UtcNow));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private void TransitionTo(RtspSessionState state)
        {
            var oldState = State;
            State = state;
            StateChanged?.Invoke(this, new RtspSessionStateChangedEventArgs(oldState, state));
        }
    }
}
