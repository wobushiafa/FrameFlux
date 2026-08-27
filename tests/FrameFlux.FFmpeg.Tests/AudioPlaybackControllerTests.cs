using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class AudioPlaybackControllerTests
{
    [Fact]
    public void PositionSeconds_IsUnavailableWhenAudioBackendIsNotOperational()
    {
        using var output = new NullAudioOutput(48000, 2);
        using var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);

        controller.Write(new NativeAudioFrame(
            new byte[4],
            48000,
            2,
            48000,
            1,
            48000));

        Assert.Null(controller.PositionSeconds);
    }

    [Fact]
    public async Task Dispose_DropsQueuedFramesAfterCurrentWrite()
    {
        using var output = new BlockingAudioOutput();
        var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            bufferDuration: TimeSpan.FromMilliseconds(100),
            output: output);
        var frame = new NativeAudioFrame(new byte[4], 48000, 2, 0, 1, 48000);

        try
        {
            controller.Write(frame);
            Assert.True(
                output.WriteStarted.Wait(TimeSpan.FromSeconds(2)),
                "The background audio worker did not start.");

            for (var index = 0; index < 100; index++)
            {
                controller.Write(frame);
            }

            var dispose = Task.Run(controller.Dispose);
            var completed = await Task.WhenAny(
                dispose,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(dispose, completed);
            await dispose;
            Assert.Equal(1, output.WriteCount);
            Assert.Equal(1, output.StopRequestCount);
        }
        finally
        {
            output.ReleaseWrite();
            controller.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_DefersOutputReleaseUntilBlockedWriteCompletes()
    {
        var output = new UninterruptibleAudioOutput();
        var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            bufferDuration: TimeSpan.FromMilliseconds(100),
            output: output);

        try
        {
            controller.Write(new NativeAudioFrame(new byte[4], 48000, 2, 0, 1, 48000));
            Assert.True(
                output.WriteStarted.Wait(TimeSpan.FromSeconds(2)),
                "The background audio worker did not start.");

            await Task.Run(controller.Dispose);

            Assert.Equal(1, output.StopRequestCount);
            Assert.Equal(0, output.DisposeCount);
            Assert.False(output.WasDisposedDuringWrite);

            output.ReleaseWrite();
            Assert.True(
                output.Disposed.Wait(TimeSpan.FromSeconds(2)),
                "The deferred audio output disposal did not complete.");
            Assert.Equal(1, output.DisposeCount);
            Assert.False(output.WasDisposedDuringWrite);
        }
        finally
        {
            output.ReleaseWrite();
            controller.Dispose();
        }
    }

    private sealed class BlockingAudioOutput : IAudioOutput, IInterruptibleAudioOutput
    {
        private readonly ManualResetEventSlim _releaseWrite = new(false);
        private int _writeCount;
        private int _stopRequestCount;

        internal ManualResetEventSlim WriteStarted { get; } = new(false);

        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal int StopRequestCount => Volatile.Read(ref _stopRequestCount);

        public int SampleRate => 48000;

        public int Channels => 2;

        public long PlayedFrames => 0;

        public bool IsOperational => true;

        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;

        public void Reset()
        {
        }

        public void Write(byte[] pcm)
        {
            Interlocked.Increment(ref _writeCount);
            WriteStarted.Set();
            _releaseWrite.Wait();
        }

        internal void ReleaseWrite() => _releaseWrite.Set();

        public void RequestStop()
        {
            Interlocked.Increment(ref _stopRequestCount);
            _releaseWrite.Set();
        }

        public void Dispose()
        {
            _releaseWrite.Set();
        }
    }

    private sealed class UninterruptibleAudioOutput : IAudioOutput, IInterruptibleAudioOutput
    {
        private readonly ManualResetEventSlim _releaseWrite = new(false);
        private int _disposeCount;
        private int _stopRequestCount;
        private int _writeActive;
        private int _wasDisposedDuringWrite;

        internal ManualResetEventSlim WriteStarted { get; } = new(false);

        internal ManualResetEventSlim Disposed { get; } = new(false);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int StopRequestCount => Volatile.Read(ref _stopRequestCount);

        internal bool WasDisposedDuringWrite =>
            Volatile.Read(ref _wasDisposedDuringWrite) != 0;

        public int SampleRate => 48000;

        public int Channels => 2;

        public long PlayedFrames => 0;

        public bool IsOperational => true;

        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;

        public void Reset()
        {
        }

        public void Write(byte[] pcm)
        {
            Volatile.Write(ref _writeActive, 1);
            WriteStarted.Set();
            _releaseWrite.Wait();
            Volatile.Write(ref _writeActive, 0);
        }

        internal void ReleaseWrite() => _releaseWrite.Set();

        public void RequestStop() =>
            Interlocked.Increment(ref _stopRequestCount);

        public void Dispose()
        {
            if (Volatile.Read(ref _writeActive) != 0)
            {
                Volatile.Write(ref _wasDisposedDuringWrite, 1);
            }

            Interlocked.Increment(ref _disposeCount);
            Disposed.Set();
        }
    }
}
