using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class SharedMediaSessionPoolTests
{
    [Fact]
    public async Task SharedMode_ReusesPhysicalSessionUntilLastLeaseStops()
    {
        var sessions = new List<TrackingSession>();
        var pool = new SharedMediaSessionPool();
        var source = MediaSource.Parse("rtsp://camera/live");
        var options = new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared };

        IFfmpegMediaSession CreatePhysical()
        {
            var session = new TrackingSession(source, options);
            sessions.Add(session);
            return session;
        }

        await using var first = pool.Acquire(source, options, 0.25d, false, CreatePhysical);
        await using var second = pool.Acquire(source, options, 0.75d, true, CreatePhysical);
        Assert.Single(sessions);

        var firstFrames = 0;
        var secondFrames = 0;
        first.FrameReceived += (_, _) => firstFrames++;
        second.FrameReceived += (_, _) => secondFrames++;
        await Task.WhenAll(first.StartAsync().AsTask(), second.StartAsync().AsTask());

        var physical = sessions[0];
        Assert.Equal(1, physical.StartCount);
        Assert.Equal(MediaPlaybackState.Playing, first.State);
        Assert.Equal(MediaPlaybackState.Playing, second.State);
        Assert.Equal(0.75d, physical.Volume);
        Assert.True(physical.IsMuted);

        physical.EmitFrame();
        Assert.Equal(1, firstFrames);
        Assert.Equal(1, secondFrames);

        await first.StopAsync();
        Assert.Equal(0, physical.StopCount);
        Assert.Equal(MediaPlaybackState.Stopped, first.State);
        Assert.Equal(MediaPlaybackState.Playing, second.State);

        await second.StopAsync();
        Assert.Equal(1, physical.StopCount);
    }

    [Fact]
    public async Task SharedMode_DoesNotReuseSessionsWithDifferentOptions()
    {
        var created = 0;
        var pool = new SharedMediaSessionPool();
        var source = MediaSource.Parse("rtsp://camera/live");
        IFfmpegMediaSession Create(MediaOpenOptions options)
        {
            created++;
            return new TrackingSession(source, options);
        }

        var tcpOptions = new MediaOpenOptions
        {
            SessionSharing = MediaSessionSharingMode.Shared,
            Network = new MediaNetworkOptions { Transport = MediaTransport.Tcp }
        };
        var udpOptions = tcpOptions with
        {
            Network = tcpOptions.Network with { Transport = MediaTransport.Udp }
        };
        await using var tcp = pool.Acquire(source, tcpOptions, 1d, false, () => Create(tcpOptions));
        await using var udp = pool.Acquire(source, udpOptions, 1d, false, () => Create(udpOptions));

        Assert.Equal(2, created);
    }

    [Fact]
    public async Task SharedMode_DisposesPhysicalSessionAfterLastLease()
    {
        var pool = new SharedMediaSessionPool();
        var source = MediaSource.Parse("rtsp://camera/live");
        var options = new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared };
        var physical = new TrackingSession(source, options);
        var first = pool.Acquire(source, options, 1d, false, () => physical);
        var second = pool.Acquire(source, options, 1d, false, () => physical);

        await first.DisposeAsync();
        Assert.False(physical.Disposed);
        await second.DisposeAsync();
        Assert.True(physical.Disposed);
    }

    [Fact]
    public async Task SharedMode_DeliversManagedLeaseToEachLogicalOutput()
    {
        var pool = new SharedMediaSessionPool();
        var source = MediaSource.Parse("rtsp://camera/live");
        var options = new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared };
        var physical = new TrackingSession(source, options);
        var firstOutput = new TrackingVideoOutput();
        var secondOutput = new TrackingVideoOutput();

        await using var first = pool.Acquire(
            source,
            options,
            1d,
            false,
            () => physical,
            firstOutput);
        await using var second = pool.Acquire(
            source,
            options,
            1d,
            false,
            () => physical,
            secondOutput);
        await first.StartAsync();
        await second.StartAsync();

        physical.EmitFrame();

        Assert.Equal(1, firstOutput.FrameCount);
        Assert.Equal(1, secondOutput.FrameCount);
    }

    private sealed class TrackingSession(
        MediaSource source,
        MediaOpenOptions options) : IFfmpegMediaSession
    {
        public MediaSource Source { get; } = source;
        public MediaOpenOptions Options { get; } = options;
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public MediaDiagnostics Diagnostics { get; } = MediaDiagnostics.Empty;
        public double Volume { get; set; } = 1d;
        public bool IsMuted { get; set; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal bool Disposed { get; private set; }

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaPlaybackErrorEventArgs>? Error { add { } remove { } }
        public event EventHandler<MediaVideoFrame>? FrameReceived;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State == MediaPlaybackState.Playing)
            {
                return ValueTask.CompletedTask;
            }

            StartCount++;
            TransitionTo(MediaPlaybackState.Opening);
            TransitionTo(MediaPlaybackState.Playing);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State is MediaPlaybackState.Idle or MediaPlaybackState.Stopped)
            {
                return ValueTask.CompletedTask;
            }

            StopCount++;
            TransitionTo(MediaPlaybackState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MediaSnapshot?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        internal void EmitFrame() =>
            FrameReceived?.Invoke(this, new MediaVideoFrame(
                new byte[16], 2, 2, 8, MediaPixelFormat.Bgra32, 1, DateTimeOffset.UtcNow));

        private void TransitionTo(MediaPlaybackState state)
        {
            var oldState = State;
            State = state;
            StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, state));
        }
    }

    private sealed class TrackingVideoOutput : IMediaVideoOutput
    {
        internal int FrameCount { get; private set; }

        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
            storageKind == MediaFrameStorageKind.CpuMemory &&
            pixelFormat == MediaPixelFormat.Bgra32;

        public bool TryPresent(IMediaFrameLease frame)
        {
            Assert.True(frame.TryGetCpuBuffer(out var buffer));
            Assert.NotEqual(IntPtr.Zero, buffer.Plane0);
            FrameCount++;
            frame.Dispose();
            return true;
        }
    }
}
