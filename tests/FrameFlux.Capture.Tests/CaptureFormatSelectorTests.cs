using FrameFlux.Capture;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed class CaptureFormatSelectorTests
{
    [Fact]
    public void SelectBestFormat_PrefersSmooth1080pMjpegOverLowFpsYuy2()
    {
        var formats = new[]
        {
            new MediaCaptureVideoFormat(2592, 1944, 5d, MediaCapturePixelFormat.Yuy2),
            new MediaCaptureVideoFormat(1920, 1080, 5d, MediaCapturePixelFormat.Yuy2),
            new MediaCaptureVideoFormat(1920, 1080, 30d, MediaCapturePixelFormat.Mjpeg),
            new MediaCaptureVideoFormat(1280, 720, 30d, MediaCapturePixelFormat.Mjpeg)
        };

        var selected = CaptureFormatSelector.SelectBestFormat(
            formats,
            requestedWidth: null,
            requestedHeight: null,
            requestedFps: null,
            requestedFormat: null,
            videoOptions: null);

        Assert.NotNull(selected);
        Assert.Equal(1920, selected!.Width);
        Assert.Equal(1080, selected.Height);
        Assert.Equal(30d, selected.FrameRate);
        Assert.Equal(MediaCapturePixelFormat.Mjpeg, selected.PixelFormat);
    }

    [Fact]
    public void SelectBestFormat_PrefersNv12OverMjpegAtSameResolutionAndRate()
    {
        var formats = new[]
        {
            new MediaCaptureVideoFormat(1920, 1080, 60d, MediaCapturePixelFormat.Mjpeg),
            new MediaCaptureVideoFormat(1920, 1080, 60d, MediaCapturePixelFormat.Nv12),
            new MediaCaptureVideoFormat(1920, 1080, 60d, MediaCapturePixelFormat.Yuy2)
        };

        var selected = CaptureFormatSelector.SelectBestFormat(
            formats,
            requestedWidth: 1920,
            requestedHeight: 1080,
            requestedFps: 60d,
            requestedFormat: null,
            videoOptions: null);

        Assert.NotNull(selected);
        Assert.Equal(MediaCapturePixelFormat.Nv12, selected!.PixelFormat);
    }

    [Fact]
    public void SelectBestFormat_RespectsMediaVideoOptionsMaxConstraints()
    {
        var formats = new[]
        {
            new MediaCaptureVideoFormat(1920, 1080, 60d, MediaCapturePixelFormat.Nv12),
            new MediaCaptureVideoFormat(1280, 720, 30d, MediaCapturePixelFormat.Nv12),
            new MediaCaptureVideoFormat(640, 480, 30d, MediaCapturePixelFormat.Yuy2)
        };

        var selected = CaptureFormatSelector.SelectBestFormat(
            formats,
            requestedWidth: null,
            requestedHeight: null,
            requestedFps: null,
            requestedFormat: null,
            videoOptions: new MediaVideoOptions
            {
                MaximumWidth = 1280,
                MaximumHeight = 720,
                MaximumFrameRate = 30d
            });

        Assert.NotNull(selected);
        Assert.Equal(1280, selected!.Width);
        Assert.Equal(720, selected.Height);
        Assert.Equal(30d, selected.FrameRate);
    }
}
