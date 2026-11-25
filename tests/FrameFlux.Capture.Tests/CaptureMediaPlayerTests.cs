using System.Runtime.InteropServices;
using FrameFlux.Capture;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed class CaptureMediaPlayerTests
{
    [Fact]
    public async Task Lifecycle_DeliversFramesSnapshotsAndDisallowsSeekPause()
    {
        using var pool = new CaptureSessionPool();
        FakeCaptureBackend? createdBackend = null;
        var player = new CaptureMediaPlayer(
            new CapturePlayerOptions(),
            pool,
            bufferPool => createdBackend = new FakeCaptureBackend(bufferPool),
            (_, _, _, _) => null);

        var output = new FakeVideoOutput(MediaFrameStorageKind.CpuMemory);
        player.VideoOutput = output;

        var receivedFrames = new List<MediaVideoFrame>();
        player.FrameReceived += (_, f) => receivedFrames.Add(f);

        var states = new List<MediaPlaybackState>();
        player.StateChanged += (_, e) => states.Add(e.NewState);

        await player.OpenAsync(
            CaptureSource.FromDeviceIndex(0),
            new MediaOpenOptions
            {
                Video = new MediaVideoOptions { SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame }
            });

        Assert.Equal(MediaPlaybackState.Ready, player.State);
        Assert.True(player.Capabilities.IsLive);
        Assert.False(player.Capabilities.CanSeek);
        Assert.False(player.Capabilities.CanPause);

        await player.PlayAsync();
        Assert.Equal(MediaPlaybackState.Playing, player.State);

        createdBackend!.EmitFrame(64, 48, emitD3D11: false, emitCpu: true);

        Assert.Equal(1, output.PresentedCount);
        Assert.Single(receivedFrames);
        Assert.Equal(64, receivedFrames[0].Width);
        Assert.Equal(48, receivedFrames[0].Height);

        var snapshot = await player.CaptureSnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.Equal(64, snapshot!.Width);
        Assert.Equal(48, snapshot.Height);

        await Assert.ThrowsAsync<NotSupportedException>(() => player.PauseAsync().AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => player.SeekAsync(TimeSpan.FromSeconds(1)).AsTask());
        Assert.Throws<NotSupportedException>(() => player.PlaybackRate = 1.5d);

        await player.StopAsync();
        Assert.Equal(MediaPlaybackState.Stopped, player.State);
        Assert.Contains(MediaPlaybackState.Opening, states);
        Assert.Contains(MediaPlaybackState.Ready, states);
        Assert.Contains(MediaPlaybackState.Playing, states);
        Assert.Contains(MediaPlaybackState.Stopped, states);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task AudioLoopback_OnlyStartsWhenPairedOrRequested_AndTracksVolumeMute()
    {
        using var pool = new CaptureSessionPool();
        var audioInstances = new List<FakeAudioLoopback>();

        // 1. Silent camera (PairedAudioDeviceId == null) -> AudioLoopback NOT created
        var silentPlayer = new CaptureMediaPlayer(
            new CapturePlayerOptions(),
            pool,
            bufferPool => new FakeCaptureBackend(bufferPool, pairedAudioId: null),
            (audioId, _, vol, muted) =>
            {
                var loopback = new FakeAudioLoopback(audioId, vol, muted);
                audioInstances.Add(loopback);
                return loopback;
            });

        await silentPlayer.OpenAsync(CaptureSource.FromDeviceIndex(0));
        await silentPlayer.PlayAsync();
        Assert.Empty(audioInstances);
        await silentPlayer.DisposeAsync();

        // 2. Capture card with paired audio -> AudioLoopback created and tracks volume/mute
        var audioPlayer = new CaptureMediaPlayer(
            new CapturePlayerOptions(),
            pool,
            bufferPool => new FakeCaptureBackend(bufferPool, pairedAudioId: "usb-audio-1"),
            (audioId, _, vol, muted) =>
            {
                var loopback = new FakeAudioLoopback(audioId, vol, muted);
                audioInstances.Add(loopback);
                return loopback;
            });

        audioPlayer.Volume = 0.6d;
        await audioPlayer.OpenAsync(
            CaptureSource.FromDeviceIndex(0),
            new MediaOpenOptions { Audio = new MediaAudioOptions { GainDecibels = 3d } });
        await audioPlayer.PlayAsync();

        Assert.Single(audioInstances);
        Assert.Equal("usb-audio-1", audioInstances[0].DeviceId);
        Assert.True(audioInstances[0].Started);
        Assert.Equal(0.6d, audioInstances[0].LastVolume);

        audioPlayer.IsMuted = true;
        Assert.True(audioInstances[0].LastMuted);

        await audioPlayer.DisposeAsync();
        Assert.True(audioInstances[0].Disposed);
    }

    [Fact]
    public async Task SharedSession_SharesSinglePhysicalBackendAcrossTwoPlayers()
    {
        using var pool = new CaptureSessionPool();
        var openCount = 0;
        FakeCaptureBackend? sharedBackend = null;

        Func<CaptureFrameBufferPool, ICaptureBackend> factory = bufferPool =>
        {
            Interlocked.Increment(ref openCount);
            sharedBackend = new FakeCaptureBackend(bufferPool);
            return sharedBackend;
        };

        var p1 = new CaptureMediaPlayer(new CapturePlayerOptions(), pool, factory, (_, _, _, _) => null);
        var p2 = new CaptureMediaPlayer(new CapturePlayerOptions(), pool, factory, (_, _, _, _) => null);

        var out1 = new FakeVideoOutput(MediaFrameStorageKind.CpuMemory);
        var out2 = new FakeVideoOutput(MediaFrameStorageKind.CpuMemory);
        p1.VideoOutput = out1;
        p2.VideoOutput = out2;

        var sharedOpts = new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared };
        var source = CaptureSource.FromDeviceIndex(0);

        await p1.OpenAsync(source, sharedOpts);
        await p2.OpenAsync(source, sharedOpts);
        await p1.PlayAsync();
        await p2.PlayAsync();

        Assert.Equal(1, openCount);

        sharedBackend!.EmitFrame(32, 32, emitD3D11: false, emitCpu: true);
        Assert.Equal(1, out1.PresentedCount);
        Assert.Equal(1, out2.PresentedCount);

        await p1.DisposeAsync();
        sharedBackend.EmitFrame(32, 32, emitD3D11: false, emitCpu: true);
        Assert.Equal(2, out2.PresentedCount);

        await p2.DisposeAsync();
    }

    [Fact]
    public async Task SharedSession_NormalizesIndexAndDeviceIdToSamePhysicalBackend()
    {
        using var pool = new CaptureSessionPool();
        var openCount = 0;
        const string canonicalId = @"\\?\USB#VID_046D&PID_0825#12345#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global";

        ICaptureBackend Factory(CaptureFrameBufferPool bufferPool)
        {
            openCount++;
            return new FakeCaptureBackend(bufferPool, hardwareActive: true);
        }

        string? Resolver(int index) => index == 0 ? canonicalId : null;

        var p1 = new CaptureMediaPlayer(new CapturePlayerOptions(), pool, Factory, (_, _, _, _) => null, Resolver);
        var p2 = new CaptureMediaPlayer(new CapturePlayerOptions(), pool, Factory, (_, _, _, _) => null, Resolver);

        var sharedOpts = new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared };
        await p1.OpenAsync(CaptureSource.FromDeviceIndex(0), sharedOpts);
        await p2.OpenAsync(CaptureSource.FromDeviceId(canonicalId.ToLowerInvariant()), sharedOpts);

        Assert.Equal(1, openCount);
        Assert.True(p1.Diagnostics.IsHardwareVideoDecodingActive);
        Assert.True(p2.Diagnostics.IsHardwareVideoDecodingActive);

        await p1.DisposeAsync();
        await p2.DisposeAsync();
    }

    [Fact]
    public async Task Diagnostics_ReportsActualHardwareVideoActiveFromOpenResult()
    {
        using var pool = new CaptureSessionPool();
        await using var swPlayer = new CaptureMediaPlayer(
            new CapturePlayerOptions { EnableHardwareVideoOutput = true },
            pool,
            bufferPool => new FakeCaptureBackend(bufferPool, hardwareActive: false),
            (_, _, _, _) => null);

        await swPlayer.OpenAsync(CaptureSource.FromDeviceIndex(0));
        Assert.False(swPlayer.Diagnostics.IsHardwareVideoDecodingActive);
    }

    [Fact]
    public async Task Reconnect_RecoversAfterTransientDeviceDisconnect()
    {
        using var pool = new CaptureSessionPool();
        var backends = new List<FakeCaptureBackend>();

        var player = new CaptureMediaPlayer(
            new CapturePlayerOptions(),
            pool,
            bufferPool =>
            {
                var backend = new FakeCaptureBackend(bufferPool);
                backends.Add(backend);
                return backend;
            },
            (_, _, _, _) => null);

        var reconnectingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawReconnecting = false;

        player.StateChanged += (_, e) =>
        {
            if (e.NewState == MediaPlaybackState.Reconnecting)
            {
                sawReconnecting = true;
                reconnectingTcs.TrySetResult();
            }
            else if (sawReconnecting && e.NewState == MediaPlaybackState.Playing)
            {
                recoveredTcs.TrySetResult();
            }
        };

        var options = new MediaOpenOptions
        {
            Network = new MediaNetworkOptions
            {
                Reconnect = new MediaReconnectOptions
                {
                    IsEnabled = true,
                    InitialDelay = TimeSpan.FromMilliseconds(20),
                    MaximumDelay = TimeSpan.FromMilliseconds(50),
                    MaximumAttempts = 3
                }
            }
        };

        await player.OpenAsync(CaptureSource.FromDeviceIndex(0), options);
        await player.PlayAsync();

        backends[0].TriggerError(new IOException("USB unplugged"), recoverable: true);

        await reconnectingTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await recoveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, backends.Count);
        Assert.Equal(MediaPlaybackState.Playing, player.State);
        Assert.Equal(1, player.Diagnostics.Reconnect.RecoveryCount);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task RoutingMediaPlayerFactory_RoutesCaptureSourceToCaptureFactory()
    {
        var captureFactory = new FakePlayerFactory();
        var fallbackFactory = new FakePlayerFactory();
        var router = new RoutingMediaPlayerFactory(
            fallbackFactory,
            new MediaPlayerRoute(CaptureSource.IsCaptureSource, captureFactory));

        await using var player = router.Create();
        await player.OpenAsync(CaptureSource.FromDeviceIndex(0));
        Assert.Equal(1, captureFactory.CreateCount);
        Assert.Equal(0, fallbackFactory.CreateCount);

        await player.OpenAsync(MediaSource.Parse("rtsp://example.com/stream"));
        Assert.Equal(1, captureFactory.CreateCount);
        Assert.Equal(1, fallbackFactory.CreateCount);
    }

    private sealed class FakeVideoOutput(MediaFrameStorageKind preferred) : IMediaVideoOutput
    {
        public int PresentedCount { get; private set; }
        public MediaFrameStorageKind PreferredFrameStorage => preferred;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
            storageKind == preferred && pixelFormat == MediaPixelFormat.Bgra32;

        public bool TryPresent(IMediaFrameLease frame)
        {
            PresentedCount++;
            frame.Dispose();
            return true;
        }
    }

    private sealed class FakeCaptureBackend(
        CaptureFrameBufferPool pool,
        string? pairedAudioId = null,
        bool hardwareActive = false) : ICaptureBackend
    {
        private Action<CaptureDeliveredFrame>? _onFrame;
        private Action<Exception, bool>? _onError;

        public CaptureOpenResult Open(
            ParsedCaptureUri parsedUri,
            MediaOpenOptions openOptions,
            CapturePlayerOptions playerOptions) =>
            new(
                "fake-device-0",
                "Fake USB Camera",
                pairedAudioId,
                new MediaCaptureVideoFormat(640, 480, 30d, MediaCapturePixelFormat.Nv12),
                MediaCapturePixelFormat.Nv12,
                "FakeBackend",
                IsHardwareVideoActive: hardwareActive);

        public void Start(
            Func<bool> prefersD3D11,
            Func<bool> needsCpuFrame,
            Action<CaptureDeliveredFrame> onFrame,
            Action<Exception, bool> onError)
        {
            _onFrame = onFrame;
            _onError = onError;
        }

        public void EmitFrame(int width, int height, bool emitD3D11, bool emitCpu)
        {
            CaptureMediaFrameLease? cpuLease = null;
            if (emitCpu)
            {
                var size = width * height * 4;
                var ptr = pool.Rent(size);
                cpuLease = CaptureMediaFrameLease.CreateBgraCpu(width, height, width * 4, ptr, size, pool);
            }

            _onFrame?.Invoke(new CaptureDeliveredFrame(null, cpuLease, width, height, 1.0, 0.5));
        }

        public void TriggerError(Exception ex, bool recoverable) => _onError?.Invoke(ex, recoverable);

        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakeAudioLoopback(string? deviceId, double initialVolume, bool initialMuted) : ICaptureAudioLoopback
    {
        public string? DeviceId { get; } = deviceId;
        public double LastVolume { get; private set; } = initialVolume;
        public bool LastMuted { get; private set; } = initialMuted;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }

        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;

        public void SetVolume(double volume, bool isMuted, double gainDecibels)
        {
            LastVolume = volume;
            LastMuted = isMuted;
        }

        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakePlayerFactory : IMediaPlayerFactory
    {
        public int CreateCount { get; private set; }

        public IMediaPlayer Create()
        {
            CreateCount++;
            return new StubPlayer();
        }

        private sealed class StubPlayer : IMediaPlayer
        {
            public MediaSource? Source { get; private set; }
            public MediaOpenOptions Options { get; private set; } = new();
            public MediaPlaybackState State => MediaPlaybackState.Ready;
            public MediaCapabilities Capabilities => MediaCapabilities.None;
            public MediaDiagnostics Diagnostics => MediaDiagnostics.Empty;
            public double Volume { get; set; }
            public bool IsMuted { get; set; }
            public IMediaVideoOutput? VideoOutput { get; set; }
            public TimeSpan Position => TimeSpan.Zero;
            public TimeSpan? Duration => null;
            public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged { add { } remove { } }
            public event EventHandler<MediaPlaybackErrorEventArgs>? Error { add { } remove { } }
            public event EventHandler<MediaVideoFrame>? FrameReceived { add { } remove { } }

            public ValueTask OpenAsync(MediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
            {
                Source = source;
                Options = options ?? new MediaOpenOptions();
                return default;
            }

            public ValueTask PlayAsync(CancellationToken cancellationToken = default) => default;
            public ValueTask PauseAsync(CancellationToken cancellationToken = default) => default;
            public ValueTask StopAsync(CancellationToken cancellationToken = default) => default;
            public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => default;
            public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
                new((MediaSnapshot?)null);
            public ValueTask DisposeAsync() => default;
        }
    }
}
