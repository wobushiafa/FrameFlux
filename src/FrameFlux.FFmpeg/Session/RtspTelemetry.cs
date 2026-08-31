using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FrameFlux.FFmpeg;

internal static class RtspTelemetry
{
    public static ActivitySource Activities { get; } = new("FrameFlux.FFmpeg");

    public static Meter Metrics { get; } = new("FrameFlux.FFmpeg");

    internal static Counter<long> SessionsStarted { get; } =
        Metrics.CreateCounter<long>("rtsp.sessions.started");

    internal static Counter<long> SessionsStopped { get; } =
        Metrics.CreateCounter<long>("rtsp.sessions.stopped");

    internal static Counter<long> SessionErrors { get; } =
        Metrics.CreateCounter<long>("rtsp.sessions.errors");

    internal static Counter<long> FramesDelivered { get; } =
        Metrics.CreateCounter<long>("rtsp.frames.delivered");

    internal static Counter<long> ReconnectAttempts { get; } =
        Metrics.CreateCounter<long>("rtsp.reconnect.attempts");

    internal static Counter<long> ReconnectRecoveries { get; } =
        Metrics.CreateCounter<long>("rtsp.reconnect.recoveries");

    internal static Histogram<double> ReconnectDelay { get; } =
        Metrics.CreateHistogram<double>("rtsp.reconnect.delay", unit: "ms");

    internal static Histogram<double> FrameReadDuration { get; } =
        Metrics.CreateHistogram<double>("rtsp.frame.read.duration", unit: "ms");

    internal static Histogram<double> FrameDecodeDuration { get; } =
        Metrics.CreateHistogram<double>("rtsp.frame.decode.duration", unit: "ms");
}
