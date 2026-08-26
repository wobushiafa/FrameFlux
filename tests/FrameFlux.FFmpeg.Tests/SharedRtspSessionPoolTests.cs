using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class SharedRtspSessionPoolTests
{
    [Fact]
    public async Task SharedMode_ReusesPhysicalSessionUntilLastLeaseStops()
    {
        var physicalFactory = new TrackingSessionFactory();
        var pool = new SharedRtspSessionPool();
        var firstFactory = new RtspSessionFactory(physicalFactory, pool);
        var secondFactory = new RtspSessionFactory(physicalFactory, pool);
        var source = RtspSource.Parse("rtsp://camera/live");
        var firstOptions = new RtspSessionOptions
        {
            StreamSharing = RtspStreamSharingMode.Shared,
            Volume = 0.25d
        };
        var secondOptions = firstOptions with
        {
            Volume = 0.75d,
            IsMuted = true
        };

        await using var first = firstFactory.Create(source, firstOptions);
        await using var second = secondFactory.Create(source, secondOptions);
        Assert.Single(physicalFactory.Sessions);

        var firstFrames = 0;
        var secondFrames = 0;
        first.FrameReceived += (_, _) => firstFrames++;
        second.FrameReceived += (_, _) => secondFrames++;

        await Task.WhenAll(
            first.StartAsync().AsTask(),
            second.StartAsync().AsTask());

        var physical = physicalFactory.Sessions[0];
        Assert.Equal(1, physical.StartCount);
        Assert.Equal(RtspSessionState.Connected, first.State);
        Assert.Equal(RtspSessionState.Connected, second.State);
        Assert.Equal(0.75d, physical.Volume);
        Assert.True(physical.IsMuted);

        physical.EmitFrame();
        Assert.Equal(1, firstFrames);
        Assert.Equal(1, secondFrames);

        await first.StopAsync();
        Assert.Equal(0, physical.StopCount);
        Assert.Equal(RtspSessionState.Stopped, first.State);
        Assert.Equal(RtspSessionState.Connected, second.State);

        physical.EmitFrame();
        Assert.Equal(1, firstFrames);
        Assert.Equal(2, secondFrames);

        second.Volume = 0.5d;
        second.IsMuted = false;
        Assert.Equal(0.5d, physical.Volume);
        Assert.False(physical.IsMuted);

        await second.StopAsync();
        Assert.Equal(1, physical.StopCount);
    }

    [Fact]
    public async Task DedicatedMode_CreatesIndependentPhysicalSessions()
    {
        var physicalFactory = new TrackingSessionFactory();
        var factory = new RtspSessionFactory(physicalFactory, new SharedRtspSessionPool());
        var source = RtspSource.Parse("rtsp://camera/live");

        await using var first = factory.Create(source);
        await using var second = factory.Create(source);

        Assert.Equal(2, physicalFactory.Sessions.Count);
    }

    [Fact]
    public async Task SharedMode_DoesNotReuseSessionsWithDifferentStreamOptions()
    {
        var physicalFactory = new TrackingSessionFactory();
        var factory = new RtspSessionFactory(physicalFactory, new SharedRtspSessionPool());
        var source = RtspSource.Parse("rtsp://camera/live");

        await using var tcp = factory.Create(source, new RtspSessionOptions
        {
            StreamSharing = RtspStreamSharingMode.Shared,
            Transport = RtspTransport.Tcp
        });
        await using var udp = factory.Create(source, new RtspSessionOptions
        {
            StreamSharing = RtspStreamSharingMode.Shared,
            Transport = RtspTransport.Udp
        });

        Assert.Equal(2, physicalFactory.Sessions.Count);
    }

    [Fact]
    public async Task SharedMode_DisposesPhysicalSessionAfterLastLeaseIsDisposed()
    {
        var physicalFactory = new TrackingSessionFactory();
        var factory = new RtspSessionFactory(physicalFactory, new SharedRtspSessionPool());
        var options = new RtspSessionOptions
        {
            StreamSharing = RtspStreamSharingMode.Shared
        };

        var first = factory.Create(RtspSource.Parse("rtsp://camera/live"), options);
        var second = factory.Create(RtspSource.Parse("rtsp://camera/live"), options);
        var physical = physicalFactory.Sessions[0];

        await first.DisposeAsync();
        Assert.False(physical.Disposed);

        await second.DisposeAsync();
        Assert.True(physical.Disposed);
    }

    private sealed class TrackingSessionFactory : IRtspSessionFactory
    {
        internal List<TrackingSession> Sessions { get; } = [];

        public IRtspSession Create(RtspSource source, RtspSessionOptions? options = null)
        {
            var session = new TrackingSession(source, options ?? new RtspSessionOptions());
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class TrackingSession(
        RtspSource source,
        RtspSessionOptions options) : IRtspSession
    {
        public RtspSource Source { get; } = source;
        public RtspSessionOptions Options { get; } = options;
        public RtspSessionState State { get; private set; } = RtspSessionState.Idle;
        public RtspSessionDiagnostics Diagnostics { get; } =
            new(false, "Test", 0, 0, 0, null);
        public double Volume { get; set; } = options.Volume;
        public bool IsMuted { get; set; } = options.IsMuted;
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
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
            cancellationToken.ThrowIfCancellationRequested();
            if (State == RtspSessionState.Connected)
            {
                return ValueTask.CompletedTask;
            }

            StartCount++;
            TransitionTo(RtspSessionState.Connecting);
            TransitionTo(RtspSessionState.Connected);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State is RtspSessionState.Idle or RtspSessionState.Stopped)
            {
                return ValueTask.CompletedTask;
            }

            StopCount++;
            TransitionTo(RtspSessionState.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask<RtspSnapshot?> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RtspSnapshot?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        internal void EmitFrame() =>
            FrameReceived?.Invoke(this, new RtspVideoFrame(
                new byte[16],
                2,
                2,
                8,
                RtspFramePixelFormat.Bgra32,
                1,
                DateTimeOffset.UtcNow));

        private void TransitionTo(RtspSessionState state)
        {
            var oldState = State;
            State = state;
            StateChanged?.Invoke(this, new RtspSessionStateChangedEventArgs(oldState, state));
        }
    }
}
