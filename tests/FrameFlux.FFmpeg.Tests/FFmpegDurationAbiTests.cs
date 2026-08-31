using System.Runtime.InteropServices;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FFmpegDurationAbiTests
{
    [Fact]
    public void GetMediaDuration_UsesLongestTrackAndSelectedStreamTimeBase()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var formatContext = Marshal.AllocHGlobal(64);
        var streams = Marshal.AllocHGlobal(IntPtr.Size * 2);
        var videoStream = Marshal.AllocHGlobal(64);
        var audioStream = Marshal.AllocHGlobal(64);
        try
        {
            Clear(formatContext, 64);
            Clear(streams, IntPtr.Size * 2);
            Clear(videoStream, 64);
            Clear(audioStream, 64);

            Marshal.WriteInt32(formatContext, 44, 2);
            Marshal.WriteIntPtr(formatContext, 48, streams);
            Marshal.WriteIntPtr(streams, 0, videoStream);
            Marshal.WriteIntPtr(streams, IntPtr.Size, audioStream);

            WriteTimeline(videoStream, numerator: 1, denominator: 1_000, duration: 5_000);
            WriteTimeline(audioStream, numerator: 1, denominator: 48_000, duration: 480_000);

            Assert.Equal(10_000, FFmpegAbi.GetMediaDuration(formatContext, videoStream));
        }
        finally
        {
            Marshal.FreeHGlobal(audioStream);
            Marshal.FreeHGlobal(videoStream);
            Marshal.FreeHGlobal(streams);
            Marshal.FreeHGlobal(formatContext);
        }
    }

    [Fact]
    public void GetMediaDuration_FallsBackToSelectedStreamWhenContextIsUnavailable()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var videoStream = Marshal.AllocHGlobal(64);
        try
        {
            Clear(videoStream, 64);
            WriteTimeline(videoStream, numerator: 1, denominator: 1_000, duration: 5_000);

            Assert.Equal(5_000, FFmpegAbi.GetMediaDuration(IntPtr.Zero, videoStream));
        }
        finally
        {
            Marshal.FreeHGlobal(videoStream);
        }
    }

    private static void WriteTimeline(
        IntPtr stream,
        int numerator,
        int denominator,
        long duration)
    {
        Marshal.WriteInt32(stream, 32, numerator);
        Marshal.WriteInt32(stream, 36, denominator);
        Marshal.WriteInt64(stream, 48, duration);
    }

    private static void Clear(IntPtr pointer, int size) =>
        Marshal.Copy(new byte[size], 0, pointer, size);
}
