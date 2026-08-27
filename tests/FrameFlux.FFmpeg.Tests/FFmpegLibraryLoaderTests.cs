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
            Assert.True(output.TrySetVolume(0.25d, muted: false));
            Assert.True(output.TrySetVolume(1d, muted: true));
            Assert.True(output.TrySetVolume(1d, muted: false));
            output.Write(new byte[48000 * 2 * sizeof(short) / 10]);
            Assert.True(
                SpinWait.SpinUntil(() => output.PlayedFrames > 0, TimeSpan.FromSeconds(2)),
                "The waveOut device did not start consuming the queued PCM buffer.");
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
}
