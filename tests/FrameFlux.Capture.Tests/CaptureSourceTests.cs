using FrameFlux.Capture;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed class CaptureSourceTests
{
    [Fact]
    public void FromDeviceIndex_CreatesAndParsesCaptureUri()
    {
        var source = CaptureSource.FromDeviceIndex(
            index: 1,
            width: 1920,
            height: 1080,
            frameRate: 60,
            pixelFormat: MediaCapturePixelFormat.Mjpeg,
            enablePairedAudio: false);

        Assert.True(CaptureSource.IsCaptureSource(source));
        var parsed = CaptureSource.Parse(source.Uri);

        Assert.Equal(1, parsed.DeviceIndex);
        Assert.Null(parsed.DeviceId);
        Assert.Equal(1920, parsed.Width);
        Assert.Equal(1080, parsed.Height);
        Assert.Equal(60d, parsed.FrameRate);
        Assert.Equal(MediaCapturePixelFormat.Mjpeg, parsed.PixelFormat);
        Assert.Equal(CaptureAudioMode.Disabled, parsed.AudioMode);
    }

    [Fact]
    public void FromDeviceId_PreservesWindowsSymbolicLinkWithHashAndQuestionMark()
    {
        const string symbolicLink =
            @"\\?\usb#vid_0bda&pid_5846&mi_00#7&169eee4a&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
        const string audioId = "{0.0.1.00000000}.{1734c0e8-7c09-4ff7-99da-582d39f12717}";

        var source = CaptureSource.FromDeviceId(
            symbolicLink,
            width: 1280,
            height: 720,
            frameRate: 30,
            pixelFormat: MediaCapturePixelFormat.Nv12,
            audioDeviceId: audioId);

        var parsed = CaptureSource.Parse(source.Uri);

        Assert.Null(parsed.DeviceIndex);
        Assert.Equal(symbolicLink, parsed.DeviceId);
        Assert.Equal(1280, parsed.Width);
        Assert.Equal(720, parsed.Height);
        Assert.Equal(30d, parsed.FrameRate);
        Assert.Equal(MediaCapturePixelFormat.Nv12, parsed.PixelFormat);
        Assert.Equal(CaptureAudioMode.SpecificDevice, parsed.AudioMode);
        Assert.Equal(audioId, parsed.AudioDeviceId);
    }

    [Fact]
    public void Parse_RejectsNonCaptureSchemeAndMissingDeviceId()
    {
        Assert.Throws<NotSupportedException>(() =>
            CaptureSource.Parse(new Uri("rtsp://example.com/stream")));
        Assert.Throws<ArgumentException>(() =>
            CaptureSource.Parse(new Uri("capture://audio/0")));
        Assert.Throws<ArgumentException>(() =>
            CaptureSource.Parse(new Uri("capture://video/device")));
        Assert.Throws<ArgumentException>(() =>
            CaptureSource.Parse(new Uri("capture://video/0?width=0")));
    }

    [Fact]
    public void AssignUniqueDisplayNames_DisambiguatesIdenticalDeviceNames()
    {
        var devices = CaptureDeviceDiscovery.AssignUniqueDisplayNames(
        [
            ("id-1", "USB Camera", [], null),
            ("id-2", "USB Camera", [], null),
            ("id-3", "Capture Card", [], "audio-1")
        ]);

        Assert.Equal(3, devices.Count);
        Assert.Equal("USB Camera (#1)", devices[0].UniqueDisplayName);
        Assert.Equal("USB Camera (#2)", devices[1].UniqueDisplayName);
        Assert.Equal("Capture Card", devices[2].UniqueDisplayName);
    }
}
