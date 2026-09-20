using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class PlaybackRateAudioTests
{
    [Fact]
    public void SupportedPlaybackRateRange_IsQuarterToFourTimes()
    {
        MediaPlaybackClock.ValidateRate(0.25d);
        MediaPlaybackClock.ValidateRate(4d);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaPlaybackClock.ValidateRate(0.249d));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaPlaybackClock.ValidateRate(4.001d));
    }

    [Fact]
    public void TempoFactors_KeepEveryFilterWithinFfmpegLimits()
    {
        Assert.Equal([0.5d, 0.5d], FFmpegAudioTempoFilter.CreateTempoFactors(0.25d));
        Assert.Equal([0.5d], FFmpegAudioTempoFilter.CreateTempoFactors(0.5d));
        Assert.Empty(FFmpegAudioTempoFilter.CreateTempoFactors(1d));
        Assert.Equal([2d], FFmpegAudioTempoFilter.CreateTempoFactors(2d));
        Assert.Equal([2d, 2d], FFmpegAudioTempoFilter.CreateTempoFactors(4d));
    }

    [Fact]
    public void AudioClock_ConvertsPlayedPcmTimeToMediaTime()
    {
        using var output = new TrackingAudioOutput();
        using var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);
        controller.SetPlaybackRate(2d);

        controller.Write(new NativeAudioFrame(
            new byte[4800 * 2 * sizeof(short)],
            48000,
            2,
            48000,
            1,
            48000));

        Assert.True(
            SpinWait.SpinUntil(() => output.WriteCount == 1, TimeSpan.FromSeconds(2)));
        var position = controller.PositionSeconds;
        Assert.NotNull(position);
        Assert.Equal(1.2d, position.Value, precision: 6);
    }

    [Fact]
    public void SeekablePlayback_UsesPlaybackClockWithoutAudioFeedback()
    {
        using var output = new TrackingAudioOutput();
        using var audioPlayback = new AudioPlaybackController(
            volume: 1d,
            muted: true,
            output: output);
        audioPlayback.SetPlaybackRate(2d);
        audioPlayback.Write(new NativeAudioFrame(
            new byte[2 * sizeof(short)],
            48000,
            2,
            0,
            1,
            48000));
        Assert.True(
            SpinWait.SpinUntil(() => output.WriteCount == 1, TimeSpan.FromSeconds(2)));

        var synchronizer = new FfmpegPlaybackSynchronizer(usesPlaybackClock: true);
        synchronizer.SetPlaybackRate(4d, positionSeconds: 0d);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(500));

        var shouldRender = synchronizer.SynchronizeVideo(
            videoPosition: 0.1d,
            playbackPosition: 0.1d,
            audioPlayback,
            cancellation.Token);

        Assert.True(shouldRender);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, synchronizer.Diagnostics.DelayedVideoFrames);
    }

    [Fact]
    public void HlsPlaybackClock_RebasesAfterInputStall()
    {
        var synchronizer = new FfmpegPlaybackSynchronizer(
            usesPlaybackClock: true,
            lateFrameRebaseThreshold: TimeSpan.FromMilliseconds(20),
            maximumAudioDrift: TimeSpan.FromMilliseconds(300));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 0d,
            playbackPosition: 0d,
            audioPlayback: null,
            cancellation.Token));
        Thread.Sleep(100);
        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 0.01d,
            playbackPosition: 0.01d,
            audioPlayback: null,
            cancellation.Token));

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 0.21d,
            playbackPosition: 0.21d,
            audioPlayback: null,
            cancellation.Token));

        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >=
            TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public void HlsPlaybackClock_RebasesAcrossMissingSegment()
    {
        var synchronizer = new FfmpegPlaybackSynchronizer(
            usesPlaybackClock: true,
            forwardJumpRebaseThreshold: TimeSpan.FromSeconds(1),
            maximumAudioDrift: TimeSpan.FromMilliseconds(300));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 0d,
            playbackPosition: 0d,
            audioPlayback: null,
            cancellation.Token));
        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 10d,
            playbackPosition: 10d,
            audioPlayback: null,
            cancellation.Token));
        Assert.False(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void HlsPlayback_ResetsAudioWhenDriftExceedsLimit()
    {
        using var output = new TrackingAudioOutput();
        using var audioPlayback = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);
        audioPlayback.Write(new NativeAudioFrame(
            new byte[9600 * 2 * sizeof(short)],
            48000,
            2,
            0,
            1,
            48000));
        Assert.True(
            SpinWait.SpinUntil(() => output.WriteCount == 1, TimeSpan.FromSeconds(2)));

        var synchronizer = new FfmpegPlaybackSynchronizer(
            usesPlaybackClock: true,
            maximumAudioDrift: TimeSpan.FromMilliseconds(300));

        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 1d,
            playbackPosition: 1d,
            audioPlayback,
            CancellationToken.None));
        Assert.Equal(1, output.ResetCount);
        Assert.Equal(1, synchronizer.Diagnostics.ClockResetCount);
    }

    [Fact]
    public void HlsPlayback_KeepsExpectedStartupBuffer()
    {
        using var output = new TrackingAudioOutput();
        using var audioPlayback = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);
        audioPlayback.Write(new NativeAudioFrame(
            new byte[9600 * 2 * sizeof(short)],
            48000,
            2,
            0,
            1,
            48000));
        Assert.True(
            SpinWait.SpinUntil(() => output.WriteCount == 1, TimeSpan.FromSeconds(2)));

        var synchronizer = new FfmpegPlaybackSynchronizer(
            usesPlaybackClock: true,
            maximumAudioDrift: TimeSpan.FromMilliseconds(800));

        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 0.7d,
            playbackPosition: 0.7d,
            audioPlayback,
            CancellationToken.None));
        Assert.Equal(0, output.ResetCount);
        Assert.Equal(0, synchronizer.Diagnostics.ClockResetCount);
    }

    [Fact]
    public void PlaybackClock_NewDecodeSessionAcceptsNewTimelineImmediately()
    {
        var synchronizer = new FfmpegPlaybackSynchronizer(usesPlaybackClock: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 0d,
            playbackPosition: 0d,
            audioPlayback: null,
            cancellation.Token));

        synchronizer.ResetSession();

        Assert.True(synchronizer.SynchronizeVideo(
            videoPosition: 10d,
            playbackPosition: 10d,
            audioPlayback: null,
            cancellation.Token));
        Assert.False(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void DrainAudio_WritesFramesAtNonDefaultPlaybackRate()
    {
        using var decoder = new QueuedPlatformDecoder(
            new NativeAudioFrame(new byte[4], 48000, 2, 0, 1, 48000));
        using var output = new TrackingAudioOutput();
        using var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);
        var synchronizer = new FfmpegPlaybackSynchronizer(usesPlaybackClock: true);

        synchronizer.DrainAudio(decoder, controller, 3d);

        Assert.True(
            SpinWait.SpinUntil(() => output.WriteCount == 1, TimeSpan.FromSeconds(2)));
    }

    private sealed class TrackingAudioOutput : IAudioOutput
    {
        private long _playedFrames;
        private int _writeCount;
        private int _resetCount;

        public int SampleRate => 48000;
        public int Channels => 2;
        public long PlayedFrames => Interlocked.Read(ref _playedFrames);
        public bool IsOperational => true;
        public int WriteCount => Volatile.Read(ref _writeCount);
        public int ResetCount => Volatile.Read(ref _resetCount);
        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;

        public void Reset()
        {
            Interlocked.Exchange(ref _playedFrames, 0);
            Interlocked.Increment(ref _resetCount);
        }

        public void Write(byte[] pcm)
        {
            Interlocked.Add(
                ref _playedFrames,
                pcm.Length / (Channels * sizeof(short)));
            Interlocked.Increment(ref _writeCount);
        }

        public void Dispose()
        {
        }
    }

    private sealed class QueuedPlatformDecoder(NativeAudioFrame frame)
        : IPlatformVideoDecoder
    {
        private NativeAudioFrame? _frame = frame;

        public bool HasAudio => true;
        public bool IsHardwareVideoDecodingActive => false;
        public string VideoDecoderDiagnostics => "Test";
        public long LastReadTicks => 0;
        public long LastCodecTicks => 0;

        public bool TryDecodeNextFrame(out IPlatformDecodedVideoFrame? frame)
        {
            frame = null;
            return false;
        }

        public bool TryDequeueAudioFrame(out NativeAudioFrame? frame)
        {
            frame = Interlocked.Exchange(ref _frame, null);
            return frame is not null;
        }

        public void Dispose()
        {
        }
    }
}
