using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace FrameFlux.FFmpeg;

internal static class RtspRuntimeDiagnostics
{
    private static int _activeStreamClients;

    public static void OnStreamClientCreated()
    {
        Interlocked.Increment(ref _activeStreamClients);
    }

    public static void OnStreamClientDisposed()
    {
        Interlocked.Decrement(ref _activeStreamClients);
    }

    public static string CreateSummary()
    {
        using var process = Process.GetCurrentProcess();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"rss {FormatMegabytes(process.WorkingSet64)} | private {FormatMegabytes(process.PrivateMemorySize64)} | gc {FormatMegabytes(GC.GetTotalMemory(false))} | threads {process.Threads.Count} | clients {Volatile.Read(ref _activeStreamClients)}");
    }

    private static string FormatMegabytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:F0}MB";
    }
}
