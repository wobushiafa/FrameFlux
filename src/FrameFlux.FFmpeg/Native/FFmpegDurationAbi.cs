using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static class FFmpegDurationAbi
{
    private const double ContainerTimeBasePerSecond = 1_000_000d;
    private const int MaxStreamsToInspect = 1024;

    internal static long GetMediaDurationTimestamp(
        IntPtr formatContext,
        IntPtr selectedStream,
        int formatMajorVersion)
    {
        if (selectedStream == IntPtr.Zero)
        {
            return 0;
        }

        var selectedTimeBase = FFmpegAbi.GetTimeBase(selectedStream);
        if (formatContext == IntPtr.Zero || selectedTimeBase.Numerator <= 0)
        {
            return GetStreamDurationTimestamp(selectedStream);
        }

        var formatDurationMicroseconds = GetFormatDurationMicroseconds(
            formatContext,
            formatMajorVersion);
        if (formatDurationMicroseconds > 0)
        {
            var selectedTimestamp = formatDurationMicroseconds *
                selectedTimeBase.Denominator /
                (ContainerTimeBasePerSecond * selectedTimeBase.Numerator);
            if (double.IsFinite(selectedTimestamp) &&
                selectedTimestamp <= long.MaxValue)
            {
                return checked((long)Math.Round(selectedTimestamp));
            }
        }

        var longestDurationSeconds = 0d;
        for (var index = 0; index < MaxStreamsToInspect; index++)
        {
            var stream = FFmpegAbi.GetStream(formatContext, index);
            if (stream == IntPtr.Zero)
            {
                break;
            }

            var durationTimestamp = GetStreamDurationTimestamp(stream);
            var timeBase = FFmpegAbi.GetTimeBase(stream);
            if (durationTimestamp <= 0 || timeBase.Numerator <= 0)
            {
                continue;
            }

            longestDurationSeconds = Math.Max(
                longestDurationSeconds,
                durationTimestamp * (double)timeBase.Numerator / timeBase.Denominator);
        }

        return longestDurationSeconds > 0
            ? checked((long)Math.Round(
                longestDurationSeconds *
                selectedTimeBase.Denominator /
                selectedTimeBase.Numerator))
            : GetStreamDurationTimestamp(selectedStream);
    }

    internal static long GetStreamDurationTimestamp(IntPtr stream)
    {
        if (stream == IntPtr.Zero)
        {
            return 0;
        }

        var codecParametersOffset = Align(IntPtr.Size + sizeof(int) * 2, IntPtr.Size);
        var timeBaseOffset = codecParametersOffset + IntPtr.Size * 2;
        return Marshal.ReadInt64(
            stream,
            Align(timeBaseOffset + sizeof(int) * 2, sizeof(long)) + sizeof(long));
    }

    internal static long GetFormatDurationMicroseconds(
        IntPtr formatContext,
        int formatMajorVersion)
    {
        if (formatContext == IntPtr.Zero || IntPtr.Size != 8)
        {
            return long.MinValue;
        }

        return formatMajorVersion switch
        {
            60 => Marshal.PtrToStructure<FFmpeg6FormatContextPrefix>(
                formatContext).Duration,
            61 or 62 => Marshal.PtrToStructure<FFmpeg7FormatContextPrefix>(
                formatContext).Duration,
            _ => long.MinValue
        };
    }

    private static int Align(int value, int alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFmpeg6FormatContextPrefix
{
    internal IntPtr AvClass;
    internal IntPtr InputFormat;
    internal IntPtr OutputFormat;
    internal IntPtr PrivateData;
    internal IntPtr IoContext;
    internal int ContextFlags;
    internal uint StreamCount;
    internal IntPtr Streams;
    internal IntPtr Url;
    internal long StartTime;
    internal long Duration;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFmpeg7FormatContextPrefix
{
    internal IntPtr AvClass;
    internal IntPtr InputFormat;
    internal IntPtr OutputFormat;
    internal IntPtr PrivateData;
    internal IntPtr IoContext;
    internal int ContextFlags;
    internal uint StreamCount;
    internal IntPtr Streams;
    internal uint StreamGroupCount;
    internal IntPtr StreamGroups;
    internal uint ChapterCount;
    internal IntPtr Chapters;
    internal IntPtr Url;
    internal long StartTime;
    internal long Duration;
}
