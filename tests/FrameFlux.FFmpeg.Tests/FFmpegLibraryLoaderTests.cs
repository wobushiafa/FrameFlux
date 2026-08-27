using FrameFlux.FFmpeg;
using System.Runtime.InteropServices;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FFmpegLibraryLoaderTests
{
    [Fact]
    public void PcmVolumeProcessor_ScalesAndMutesSigned16BitSamples()
    {
        short[] source = [10000, -10000, short.MaxValue, short.MinValue];
        var pcm = new byte[source.Length * sizeof(short)];
        Buffer.BlockCopy(source, 0, pcm, 0, pcm.Length);

        AudioPlaybackController.ApplyVolume(pcm, 0.5d, muted: false);

        var scaled = new short[source.Length];
        Buffer.BlockCopy(pcm, 0, scaled, 0, pcm.Length);
        Assert.Equal([5000, -5000, 16384, -16384], scaled);

        AudioPlaybackController.ApplyVolume(pcm, 1d, muted: true);
        Assert.All(pcm, value => Assert.Equal(0, value));
    }

    [Fact]
    public void PcmGainProcessor_AppliesDecibelsAndSaturates()
    {
        short[] source = [10000, -10000, 20000, -20000];
        var pcm = new byte[source.Length * sizeof(short)];
        Buffer.BlockCopy(source, 0, pcm, 0, pcm.Length);

        AudioPlaybackController.ApplyGain(pcm, 6.020599913279624d);

        var amplified = new short[source.Length];
        Buffer.BlockCopy(pcm, 0, amplified, 0, pcm.Length);
        Assert.Equal([20000, -20000, short.MaxValue, short.MinValue], amplified);

        AudioPlaybackController.ApplyGain(pcm, -6.020599913279624d);
        var attenuated = new short[source.Length];
        Buffer.BlockCopy(pcm, 0, attenuated, 0, pcm.Length);
        Assert.Equal([10000, -10000, 16384, -16384], attenuated);
    }

    [Fact]
    public void WindowsAudioOutput_QueuedSilenceAdvancesDeviceClock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsWaveOutAudioOutput? output;
        try
        {
            output = new WindowsWaveOutAudioOutput(48000, 2);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using (output)
        {
            output.Write(new byte[48000 * 2 * sizeof(short) / 10]);
            Assert.True(
                SpinWait.SpinUntil(() => output.PlayedFrames > 0, TimeSpan.FromSeconds(2)),
                "The waveOut device did not start consuming the queued PCM buffer.");
        }
    }

    [Fact]
    public void AudioPlaybackController_ExposesBackendDiagnostics()
    {
        using var output = new TestAudioOutput();
        using var controller = new AudioPlaybackController(
            volume: 0.5d,
            muted: false,
            output: output);

        Assert.True(controller.IsOperational);
        Assert.Equal("Test", controller.Diagnostics.Backend);
        Assert.Equal("test-device", controller.Diagnostics.OutputDeviceId);
    }

    [Fact]
    public void AudioPlaybackController_AppliesVolumeAndMuteBeforeOutput()
    {
        using var output = new TestAudioOutput();
        using var controller = new AudioPlaybackController(
            volume: 0.5d,
            muted: false,
            output: output);

        controller.Write(CreateAudioFrame([10000, -10000]));
        WaitForWriteCount(output, 1);
        Assert.Equal([5000, -5000], ReadSamples(output.LastPcm));

        controller.SetVolume(0.25d);
        controller.Write(CreateAudioFrame([10000, -10000]));
        WaitForWriteCount(output, 2);
        Assert.Equal([2500, -2500], ReadSamples(output.LastPcm));

        controller.SetMuted(true);
        controller.Write(CreateAudioFrame([10000, -10000]));
        WaitForWriteCount(output, 3);
        Assert.Equal([0, 0], ReadSamples(output.LastPcm));
    }

    [Fact]
    public void AudioPlaybackController_AllowsFaultedBackendToRecoverOnWrite()
    {
        using var output = new TestAudioOutput { IsOperational = false };
        using var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);
        var frame = new NativeAudioFrame(
            new byte[4],
            48000,
            2,
            0,
            1,
            48000);

        controller.Write(frame);

        WaitForWriteCount(output, 1);
        Assert.Equal(1, output.WriteCount);
    }

    [Fact]
    public void AudioPlaybackController_ResetsOutputOnTimestampJump()
    {
        using var output = new TestAudioOutput();
        using var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            output: output);

        controller.Write(new NativeAudioFrame(
            new byte[4800 * 2 * sizeof(short)],
            48000,
            2,
            0,
            1,
            48000));
        controller.Write(new NativeAudioFrame(
            new byte[4800 * 2 * sizeof(short)],
            48000,
            2,
            96000,
            1,
            48000));

        WaitForWriteCount(output, 2);
        Assert.Equal(1, output.ResetCount);
        Assert.Equal(1, controller.ClockResetCount);
    }

    [Fact]
    public async Task AudioPlaybackController_DoesNotBlockProducerWhenOutputIsSlow()
    {
        using var output = new BlockingAudioOutput();
        var controller = new AudioPlaybackController(
            volume: 1d,
            muted: false,
            bufferDuration: TimeSpan.FromMilliseconds(40),
            output: output);
        var frame = new NativeAudioFrame(new byte[4], 48000, 2, 0, 1, 48000);

        try
        {
            controller.Write(frame);
            Assert.True(
                await Task.Run(
                    () => output.WriteStarted.Wait(TimeSpan.FromSeconds(2))),
                "The background audio worker did not start.");

            var producer = Task.Run(() =>
            {
                for (var index = 0; index < 1000; index++)
                {
                    controller.Write(frame);
                }
            });

            var completed = await Task.WhenAny(
                producer,
                Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(producer, completed);
            await producer;
        }
        finally
        {
            output.ReleaseWrites();
            controller.Dispose();
        }
    }

    [Fact]
    public void WasapiAudioOutput_ReportsSelectedEndpointWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsWasapiAudioOutput? output;
        try
        {
            output = new WindowsWasapiAudioOutput(
                48000,
                2,
                new AudioOutputConfiguration(null, TimeSpan.FromMilliseconds(100)));
        }
        catch (Exception)
        {
            return;
        }

        using (output)
        {
            Assert.Equal("WASAPI", output.Diagnostics.Backend);
            Assert.False(string.IsNullOrWhiteSpace(output.Diagnostics.OutputDeviceId));
            Assert.False(string.IsNullOrWhiteSpace(output.Diagnostics.OutputDeviceName));
            Assert.True(output.IsOperational);
        }
    }

    [Fact]
    public void BundledWindowsLibraries_LoadDirectlyWithoutFrameFluxAdapter()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var libraryDirectory = Path.Combine(
            repositoryRoot,
            "native",
            "artifacts",
            "runtimes",
            "win-x64",
            "native");

        Assert.True(File.Exists(Path.Combine(libraryDirectory, "avcodec-61.dll")));
        Assert.False(File.Exists(Path.Combine(libraryDirectory, "frameflux_ffmpeg.dll")));

        FFmpegHelper.RegisterFFmpeg(libraryDirectory);
    }

    [Fact]
    public void CandidateDirectories_RecognizeRuntimeAndNativeLayouts()
    {
        using var directory = new TemporaryDirectory();
        var runtimeDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "runtimes", "win-x64", "native"));

        var candidates = FFmpegLibraryLoader.GetCandidateDirectories(directory.Path, "win-x64");

        Assert.Contains(directory.Path, candidates);
        Assert.Contains(runtimeDirectory.FullName, candidates);
    }

    [Fact]
    public void WindowsResolver_SelectsHighestNumericMajor()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "avcodec-9.dll"), string.Empty);
        var expected = Path.Combine(directory.Path, "avcodec-61.dll");
        File.WriteAllText(expected, string.Empty);

        var actual = FFmpegLibraryLoader.FindBestLibraryFile(
            [directory.Path],
            "avcodec",
            FFmpegLibraryPlatform.Windows);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LinuxResolver_RecognizesFullyVersionedAndNeonLibraries()
    {
        using var directory = new TemporaryDirectory();
        var versioned = Path.Combine(directory.Path, "libavformat.so.62.13.102");
        var neon = Path.Combine(directory.Path, "libavcodec_neon.so");
        File.WriteAllText(versioned, string.Empty);
        File.WriteAllText(neon, string.Empty);

        Assert.Equal(versioned, FFmpegLibraryLoader.FindBestLibraryFile(
            [directory.Path], "avformat", FFmpegLibraryPlatform.Linux));
        Assert.Equal(neon, FFmpegLibraryLoader.FindBestLibraryFile(
            [directory.Path], "avcodec", FFmpegLibraryPlatform.Linux));
    }

    [Fact]
    public void LibrarySetValidation_AcceptsMatchingVersionedFiles()
    {
        var files = new Dictionary<string, string>
        {
            ["avcodec"] = "avcodec-61.dll",
            ["avformat"] = "avformat-61.dll",
            ["avutil"] = "avutil-59.dll",
            ["swscale"] = "swscale-8.dll",
            ["swresample"] = "swresample-5.dll"
        };

        FFmpegLibraryLoader.ValidateSelectedLibraryVersions(
            files,
            FFmpegLibraryPlatform.Windows);
    }

    [Fact]
    public void LibrarySetValidation_RejectsMixedHighestFilesBeforeLoading()
    {
        var files = new Dictionary<string, string>
        {
            ["avcodec"] = "avcodec-61.dll",
            ["avformat"] = "avformat-62.dll",
            ["avutil"] = "avutil-59.dll",
            ["swscale"] = "swscale-8.dll",
            ["swresample"] = "swresample-5.dll"
        };

        var exception = Assert.Throws<NotSupportedException>(
            () => FFmpegLibraryLoader.ValidateSelectedLibraryVersions(
                files,
                FFmpegLibraryPlatform.Windows));

        Assert.Contains("mix incompatible ABI families", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(60, 60, 58, 7, 4)]
    [InlineData(61, 61, 59, 8, 5)]
    [InlineData(62, 62, 60, 9, 6)]
    public void VersionValidation_AcceptsCompleteSupportedFamilies(
        int codec,
        int format,
        int util,
        int scale,
        int resample)
    {
        FFmpegApi.ValidateVersionFamily(codec, format, util, scale, resample);
    }

    [Theory]
    [InlineData(61, 62, 59, 8, 5)]
    [InlineData(61, 61, 60, 8, 5)]
    [InlineData(61, 61, 59, 9, 5)]
    [InlineData(61, 61, 59, 8, 6)]
    public void VersionValidation_RejectsMixedComponentFamilies(
        int codec,
        int format,
        int util,
        int scale,
        int resample)
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => FFmpegApi.ValidateVersionFamily(codec, format, util, scale, resample));

        Assert.Contains("one FFmpeg build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionValidation_RejectsUnsupportedCodecMajor()
    {
        Assert.Throws<NotSupportedException>(
            () => FFmpegApi.ValidateVersionFamily(63, 63, 61, 10, 7));
    }

    private static NativeAudioFrame CreateAudioFrame(short[] samples)
    {
        var pcm = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return new NativeAudioFrame(pcm, 48000, 2, 0, 1, 48000);
    }

    private static short[] ReadSamples(byte[]? pcm)
    {
        Assert.NotNull(pcm);
        var samples = new short[pcm.Length / sizeof(short)];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        return samples;
    }

    private static void WaitForWriteCount(TestAudioOutput output, int expected)
    {
        Assert.True(
            SpinWait.SpinUntil(
                () => output.WriteCount >= expected,
                TimeSpan.FromSeconds(2)),
            $"Expected {expected} audio writes, observed {output.WriteCount}.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FrameFlux.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class TestAudioOutput : IAudioOutput
    {
        private int _writeCount;
        private int _resetCount;
        private byte[]? _lastPcm;

        public int SampleRate => 48000;
        public int Channels => 2;
        public long PlayedFrames => 0;
        public bool IsOperational { get; set; } = true;
        public int WriteCount => Volatile.Read(ref _writeCount);
        public int ResetCount => Volatile.Read(ref _resetCount);
        public byte[]? LastPcm => Volatile.Read(ref _lastPcm);
        public MediaAudioDiagnostics Diagnostics { get; } = new(
            "Test",
            "test-device",
            "Test device",
            48000,
            2,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero,
            true,
            0,
            null);

        public void Write(byte[] pcm)
        {
            Volatile.Write(ref _lastPcm, pcm.ToArray());
            Interlocked.Increment(ref _writeCount);
        }

        public void Reset() => Interlocked.Increment(ref _resetCount);

        public void Dispose()
        {
        }
    }

    private sealed class BlockingAudioOutput : IAudioOutput
    {
        private readonly ManualResetEventSlim _releaseWrites = new(false);
        private int _disposed;

        internal ManualResetEventSlim WriteStarted { get; } = new(false);

        public int SampleRate => 48000;
        public int Channels => 2;
        public long PlayedFrames => 0;
        public bool IsOperational => true;
        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;

        public void Write(byte[] pcm)
        {
            WriteStarted.Set();
            _releaseWrites.Wait();
        }

        public void Reset()
        {
        }

        internal void ReleaseWrites() => _releaseWrites.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _releaseWrites.Set();
            _releaseWrites.Dispose();
            WriteStarted.Dispose();
        }
    }
}
