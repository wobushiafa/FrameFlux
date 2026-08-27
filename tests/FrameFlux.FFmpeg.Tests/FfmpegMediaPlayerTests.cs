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
                LowLatency = true,
                Transport = MediaTransport.Tcp,
                StreamSharing = MediaStreamSharingMode.Shared
            });

        player.Volume = 0.25d;
        player.IsMuted = true;
        await player.PlayAsync();

        var session = Assert.IsType<FakeMediaSession>(factory.LastSession);
        Assert.Equal(0.25d, factory.LastVolume);
        Assert.True(factory.LastIsMuted);
        Assert.Equal(MediaStreamSharingMode.Shared, factory.LastOptions?.StreamSharing);
        Assert.Equal(MediaPlaybackState.Playing, player.State);
        Assert.NotNull(receivedFrame);
        Assert.Equal(MediaFramePixelFormat.Bgra32, receivedFrame.PixelFormat);

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
            ReadTimeout = TimeSpan.FromMilliseconds(-1)
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOpenOptions
        {
            MaxVideoWidth = -1
        }.Validate());
    }

    [Fact]
    public async Task GenericPlayer_ForwardsConfiguredVideoOutput()
    {
        var factory = new FakeMediaSessionFactory();
        var output = new FakeVideoOutput();
        await using var player = new FfmpegMediaPlayer(factory) { VideoOutput = output };

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions { RenderPreference = MediaRenderPreference.NativeSurface });
        Assert.False(player.Capabilities.CanCaptureSnapshots);
        await player.PlayAsync();

        Assert.Same(output, factory.LastVideoOutput);
        Assert.Throws<InvalidOperationException>(() => player.VideoOutput = null);
    }

    [Fact]
    public async Task GenericPlayer_DisablesCpuSnapshotsForCompositedGpuOutput()
    {
        await using var player = new FfmpegMediaPlayer(new FakeMediaSessionFactory())
        {
            VideoOutput = new FakeVideoOutput(MediaRenderPreference.CompositedGpu)
        };

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions { RenderPreference = MediaRenderPreference.CompositedGpu });

        Assert.False(player.Capabilities.CanCaptureSnapshots);
    }

    [Fact]
    public async Task GenericPlayer_KeepsSnapshotsWhenRequestedGpuModeFallsBackToSoftware()
    {
        await using var player = new FfmpegMediaPlayer(new FakeMediaSessionFactory())
        {
            VideoOutput = new FakeVideoOutput(MediaRenderPreference.Software)
        };

        await player.OpenAsync(
            MediaSource.Parse("rtsp://camera/live"),
            new MediaOpenOptions { RenderPreference = MediaRenderPreference.CompositedGpu });

        Assert.True(player.Capabilities.CanCaptureSnapshots);
    }

    [Theory]
    [InlineData(MediaRenderPreference.NativeSurface, true)]
    [InlineData(MediaRenderPreference.CompositedGpu, true)]
    [InlineData(MediaRenderPreference.Software, false)]
    public void Session_ResolvesOutputPreferenceToDecoderRenderMode(
        MediaRenderPreference preference,
        bool expectsNativeFrames)
    {
        var renderMode = FfmpegMediaSession.ResolveRenderMode(
            new FakeVideoOutput(preference));

        Assert.Equal(
            expectsNativeFrames,
            renderMode == RtspRenderMode.NativeSurface);
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

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaPlaybackErrorEventArgs>? Error { add { } remove { } }
        public event EventHandler<MediaVideoFrame>? FrameReceived;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            TransitionTo(MediaPlaybackState.Opening);
            TransitionTo(MediaPlaybackState.Playing);
            FrameReceived?.Invoke(this, new MediaVideoFrame(
                new byte[16], 2, 2, 8, MediaFramePixelFormat.Bgra32, 1, DateTimeOffset.UtcNow));
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            TransitionTo(MediaPlaybackState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MediaSnapshot?>(new MediaSnapshot(
                new byte[16], 2, 2, 8, MediaFramePixelFormat.Bgra32, DateTimeOffset.UtcNow));

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
        private readonly MediaRenderPreference _preference;

        internal FakeVideoOutput(
            MediaRenderPreference preference = MediaRenderPreference.NativeSurface)
        {
            _preference = preference;
        }

        public MediaRenderPreference Preference => _preference;
        public bool Supports(MediaFramePixelFormat pixelFormat) => true;
        public bool TryPresent(IMediaFrameLease frame)
        {
            frame.Dispose();
            return true;
        }
    }
}
