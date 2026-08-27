using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaContractsTests
{
    [Fact]
    public void MediaSource_AcceptsUrisAndAbsoluteFilePaths()
    {
        var stream = MediaSource.Parse("rtsp://camera/live");
        var filePath = Path.Combine(Path.GetTempPath(), "frameflux-source.mp4");
        var file = MediaSource.FromFile(filePath);

        Assert.Equal("rtsp", stream.Uri.Scheme);
        Assert.True(file.Uri.IsFile);
        Assert.Equal(Path.GetFullPath(filePath), file.Uri.LocalPath);
    }

    [Fact]
    public void MediaSource_RejectsRelativeValues() =>
        Assert.Throws<ArgumentException>(() => MediaSource.Parse("videos/sample.mp4"));

    [Fact]
    public void EngineAssembly_DoesNotReferenceFFmpegAutoGen()
    {
        var references = typeof(FfmpegMediaPlayer).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            string.Equals(reference.Name, "FFmpeg.AutoGen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_RuntimeControlsCanChangeBeforePlayback()
    {
        await using IFfmpegMediaSession session = new FfmpegMediaSession(
            MediaSource.Parse("rtsp://camera/audio-contract"),
            new MediaOpenOptions(),
            0.75d,
            true,
            videoOutput: null);

        Assert.Equal(0.75d, session.Volume);
        Assert.True(session.IsMuted);
        session.Volume = 0.25d;
        session.IsMuted = false;
        Assert.Equal(0.25d, session.Volume);
        Assert.False(session.IsMuted);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Volume = -0.1d);
        Assert.Null(await session.CaptureSnapshotAsync());
        await session.StopAsync();
        Assert.Equal(MediaPlaybackState.Idle, session.State);
    }
}
