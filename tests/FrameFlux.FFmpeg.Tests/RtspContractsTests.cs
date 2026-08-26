using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class RtspContractsTests
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
    public void MediaSource_RejectsRelativeValues()
    {
        Assert.Throws<ArgumentException>(() => MediaSource.Parse("videos/sample.mp4"));
    }

    [Fact]
    public void EngineAssembly_DoesNotReferenceFFmpegAutoGen()
    {
        var references = typeof(FfmpegRtspSession).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            string.Equals(reference.Name, "FFmpeg.AutoGen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("rtsp://camera/live")]
    [InlineData("rtsps://camera/live")]
    public void Source_AcceptsSupportedSchemes(string value)
    {
        var source = RtspSource.Parse(value);

        Assert.Equal(value, source.ToString());
    }

    [Theory]
    [InlineData("http://camera/live")]
    [InlineData("not-a-uri")]
    public void Source_RejectsUnsupportedSchemes(string value)
    {
        Assert.Throws<ArgumentException>(() => RtspSource.Parse(value));
    }

    [Fact]
    public void Options_RejectNegativeLimitsAndTimeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RtspSessionOptions
        {
            ReadTimeout = TimeSpan.FromMilliseconds(-1)
        }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new RtspSessionOptions
        {
            MaxFramesPerSecond = -1
        }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new RtspSessionOptions
        {
            Volume = 1.01d
        }.Validate());
    }

    [Fact]
    public async Task Session_VolumeAndMute_CanChangeBeforePlayback()
    {
        await using IRtspSession session = new FfmpegRtspSession(
            RtspSource.Parse("rtsp://camera/audio-contract"),
            new RtspSessionOptions { Volume = 0.75d, IsMuted = true });

        Assert.Equal(0.75d, session.Volume);
        Assert.True(session.IsMuted);

        session.Volume = 0.25d;
        session.IsMuted = false;

        Assert.Equal(0.25d, session.Volume);
        Assert.False(session.IsMuted);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Volume = -0.1d);
    }

    [Fact]
    public void RendererRegistry_UsesRequestedBackendAndPriority()
    {
        var registry = new RtspRendererBackendRegistry();
        registry.Register(new TestBackend("software-low", RtspRenderPreference.Software, 1));
        registry.Register(new TestBackend("software-high", RtspRenderPreference.Software, 10));
        registry.Register(new TestBackend("native", RtspRenderPreference.NativeSurface, 100));
        var capabilities = new RtspPlatformCapabilities(
            "Test",
            true,
            new HashSet<RtspRenderPreference>
            {
                RtspRenderPreference.Software,
                RtspRenderPreference.NativeSurface
            });

        Assert.Equal("software-high", registry.Select(RtspRenderPreference.Software, capabilities)?.Id);
        Assert.Equal("native", registry.Select(RtspRenderPreference.Auto, capabilities)?.Id);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    public void VideoTransform_NormalizesRotation(int rotation, int expected)
    {
        Assert.Equal(expected, new RtspVideoTransform(rotation).NormalizedRotationDegrees);
    }

    [Fact]
    public async Task Session_BeforeStart_HasNoSnapshotAndStopsSafely()
    {
        await using var session = new FfmpegRtspSession(
            RtspSource.Parse("rtsp://camera/contract-test"),
            new RtspSessionOptions());

        Assert.Null(await session.CaptureSnapshotAsync());
        await session.StopAsync();
        Assert.Equal(RtspSessionState.Idle, session.State);
    }

    private sealed class TestBackend(string id, RtspRenderPreference preference, int priority) : IRtspRendererBackend
    {
        public string Id => id;

        public RtspRenderPreference Preference => preference;

        public int Priority => priority;

        public bool IsSupported(RtspPlatformCapabilities capabilities) =>
            capabilities.SupportedRenderPreferences.Contains(Preference);
    }
}
