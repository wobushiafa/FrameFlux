using FrameFlux.WebRtc;
using System.Runtime.InteropServices;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class JpegDecodingTests
{
    private const string RedJpeg = "/9j/4AAQSkZJRgABAgAAAQABAAD//gAQTGF2YzYxLjE5LjEwMQD/2wBDAAgEBAQEBAUFBQUFBQYGBgYGBgYGBgYGBgYHBwcICAgHBwcGBgcHCAgICAkJCQgICAgJCQoKCgwMCwsODg4RERT/xABMAAEBAAAAAAAAAAAAAAAAAAAABgEBAQAAAAAAAAAAAAAAAAAABgcQAQAAAAAAAAAAAAAAAAAAAAARAQAAAAAAAAAAAAAAAAAAAAD/wAARCAAQABADASIAAhEAAxEA/9oADAMBAAIRAxEAPwCLAFF/f//Z";

    [NativeDecoderFact]
    public void NativeDecoder_DecodesJpegPixels()
    {
        using var decoder = new FfmpegWebRtcVideoDecoder
        {
            DecodingPolicy = FrameFlux.MediaVideoDecodingPolicy.SoftwareOnly
        };
        using var pool = new WebRtcFrameBufferPool();
        var format = new VideoFormat(VideoCodecsEnum.JPEG, 26);

        Assert.True(decoder.CanDecode(format));
        Assert.True(decoder.TryDecode(Convert.FromBase64String(RedJpeg), format, pool, out var frame));
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.Equal(16, frame.Width);
            Assert.Equal(16, frame.Height);
            Assert.Equal(FrameFlux.MediaPixelFormat.Yuv420P, frame.PixelFormat);
            Assert.True(frame.TryGetCpuBuffer(out var buffer));
            Assert.InRange(Marshal.ReadByte(buffer.Plane0), 60, 110);
            Assert.InRange(Marshal.ReadByte(buffer.Plane1), 70, 130);
            Assert.InRange(Marshal.ReadByte(buffer.Plane2), 190, 255);
        }
    }

    private sealed class NativeDecoderFactAttribute : FactAttribute
    {
        public NativeDecoderFactAttribute()
        {
            if (!FfmpegWebRtcVideoDecoder.IsSupported)
                Skip = "Native FFmpeg decoder libraries are unavailable in the test runtime directory.";
        }
    }
}
