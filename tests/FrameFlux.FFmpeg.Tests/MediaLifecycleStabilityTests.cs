using FrameFlux.FFmpeg;
using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaLifecycleStabilityTests
{
    [Fact]
    public async Task ConcurrentStartsAndStops_DisposeEveryCreatedPlayerExactlyOnce()
    {
        var factory = new TrackingPlayerFactory();
        await using var controller = new MediaPlaybackController();
        var source = MediaSource.Parse("rtsp://localhost/live");
        var options = new MediaOpenOptions();
        var output = new RejectingVideoOutput();

        var operations = Enumerable.Range(0, 16)
            .SelectMany(_ => new[]
            {
                Task.Run(async () => await controller.StartAsync(factory, source, options, output)),
                Task.Run(async () => await controller.StopAsync())
            });
        await Task.WhenAll(operations);
        await controller.StopAsync();

        Assert.Equal(16, factory.Players.Count);
        Assert.All(factory.Players, player =>
        {
            Assert.Equal(1, player.OpenCount);
            Assert.Equal(1, player.PlayCount);
            Assert.Equal(1, player.StopCount);
            Assert.Equal(1, player.DisposeCount);
        });
        Assert.Equal(MediaPlaybackState.Stopped, controller.State);
    }

    [Fact]
    public async Task ConcurrentDispose_IsIdempotent()
    {
        var factory = new TrackingPlayerFactory();
        var controller = new MediaPlaybackController();
        await controller.StartAsync(
            factory,
            MediaSource.Parse("rtsp://localhost/live"),
            new MediaOpenOptions(),
            new RejectingVideoOutput());

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await controller.DisposeAsync())));

        var player = Assert.Single(factory.Players);
        Assert.Equal(1, player.StopCount);
        Assert.Equal(1, player.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentSharedLeases_StartAndStopPhysicalSessionOnce()
    {
        var pool = new SharedMediaSessionPool();
        var source = MediaSource.Parse("rtsp://camera/live");
        var options = new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared };
        var physical = new TrackingSession(source, options);
        var leases = Enumerable.Range(0, 64)
            .Select(_ => pool.Acquire(source, options, 1d, false, () => physical))
            .ToArray();

        await Task.WhenAll(leases.Select(lease =>
            Task.Run(async () => await lease.StartAsync())));
        await Task.WhenAll(leases.Select(lease =>
            Task.Run(async () => await lease.StopAsync())));
        await Task.WhenAll(leases.Select(lease =>
            Task.Run(async () => await lease.DisposeAsync())));

        Assert.Equal(1, physical.StartCount);
        Assert.Equal(1, physical.StopCount);
        Assert.Equal(1, physical.DisposeCount);
    }

    [Fact]
    public async Task ConfigurableLifecycleAndFrameOwnershipLoop()
    {
        var iterations = GetStabilityIterations();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var factory = new TrackingPlayerFactory();
            await using (var controller = new MediaPlaybackController())
            {
                await controller.StartAsync(
                    factory,
                    MediaSource.Parse("rtsp://localhost/live"),
                    new MediaOpenOptions(),
                    new RejectingVideoOutput());
                await controller.StopAsync();
            }

            var player = Assert.Single(factory.Players);
            Assert.Equal(1, player.StopCount);
            Assert.Equal(1, player.DisposeCount);

            using var slot = new LatestMediaFrameSlot();
            var frames = Enumerable.Range(0, 8).Select(_ => new TrackingFrameLease()).ToArray();
            foreach (var frame in frames)
            {
                Assert.True(slot.TrySubmit(frame, out _));
            }

            slot.Take()!.Dispose();
            Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
        }
    }

    private static int GetStabilityIterations()
    {
        const int defaultIterations = 100;
        var configured = Environment.GetEnvironmentVariable("FRAMEFLUX_STABILITY_ITERATIONS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultIterations;
        }

        return int.TryParse(configured, out var iterations) && iterations > 0
            ? iterations
            : throw new InvalidOperationException(
                "FRAMEFLUX_STABILITY_ITERATIONS must be a positive integer.");
    }

    private sealed class TrackingPlayerFactory : IMediaPlayerFactory
    {
        private readonly object _sync = new();
        private readonly List<TrackingPlayer> _players = [];

        internal IReadOnlyList<TrackingPlayer> Players
        {
            get
            {
                lock (_sync)
                {
                    return [.. _players];
                }
            }
        }

        public IMediaPlayer Create()
        {
            var player = new TrackingPlayer();
            lock (_sync)
            {
                _players.Add(player);
            }

            return player;
        }
    }

    private sealed class TrackingPlayer : IMediaPlayer
    {
        private int _stopCount;
        private int _disposeCount;

        public MediaSource? Source { get; private set; }
        public MediaOpenOptions Options { get; private set; } = new();
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public MediaCapabilities Capabilities => MediaCapabilities.None;
        public MediaDiagnostics Diagnostics => MediaDiagnostics.Empty;
        public double Volume { get; set; }
        public bool IsMuted { get; set; }
        public IMediaVideoOutput? VideoOutput { get; set; }
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan? Duration => null;
        internal int OpenCount { get; private set; }
        internal int PlayCount { get; private set; }
        internal int StopCount => Volatile.Read(ref _stopCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaPlaybackErrorEventArgs>? Error { add { } remove { } }
        public event EventHandler<MediaVideoFrame>? FrameReceived { add { } remove { } }

        public ValueTask OpenAsync(
            MediaSource source,
            MediaOpenOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Source = source;
            Options = options ?? new MediaOpenOptions();
            OpenCount++;
            SetState(MediaPlaybackState.Ready);
            return ValueTask.CompletedTask;
        }

        public ValueTask PlayAsync(CancellationToken cancellationToken = default)
        {
            PlayCount++;
            SetState(MediaPlaybackState.Playing);
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            if (State is MediaPlaybackState.Idle or MediaPlaybackState.Stopped)
            {
                return ValueTask.CompletedTask;
            }

            Interlocked.Increment(ref _stopCount);
            SetState(MediaPlaybackState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MediaSnapshot?>(null);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        private void SetState(MediaPlaybackState state)
        {
            var previous = State;
            State = state;
            StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(previous, state));
        }
    }

    private sealed class TrackingSession(MediaSource source, MediaOpenOptions options) : IFfmpegMediaSession
    {
        private int _startCount;
        private int _stopCount;
        private int _disposeCount;

        public MediaSource Source { get; } = source;
        public MediaOpenOptions Options { get; } = options;
        public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Idle;
        public MediaDiagnostics Diagnostics => MediaDiagnostics.Empty;
        public double Volume { get; set; } = 1d;
        public bool IsMuted { get; set; }
        internal int StartCount => Volatile.Read(ref _startCount);
        internal int StopCount => Volatile.Read(ref _stopCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaPlaybackErrorEventArgs>? Error { add { } remove { } }
        public event EventHandler<MediaVideoFrame>? FrameReceived { add { } remove { } }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            SetState(MediaPlaybackState.Playing);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            if (State is MediaPlaybackState.Idle or MediaPlaybackState.Stopped)
            {
                return ValueTask.CompletedTask;
            }

            Interlocked.Increment(ref _stopCount);
            SetState(MediaPlaybackState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MediaSnapshot?>(null);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        private void SetState(MediaPlaybackState state)
        {
            var previous = State;
            State = state;
            StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(previous, state));
        }
    }

    private sealed class RejectingVideoOutput : IMediaVideoOutput
    {
        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;
        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => true;
        public bool TryPresent(IMediaFrameLease frame) => false;
    }

    private sealed class TrackingFrameLease : IMediaFrameLease
    {
        private int _disposeCount;

        public int Width => 1;
        public int Height => 1;
        public MediaFrameStorageKind StorageKind => MediaFrameStorageKind.CpuMemory;
        public MediaPixelFormat PixelFormat => MediaPixelFormat.Bgra32;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer)
        {
            buffer = default;
            return false;
        }

        public bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture)
        {
            texture = default;
            return false;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
