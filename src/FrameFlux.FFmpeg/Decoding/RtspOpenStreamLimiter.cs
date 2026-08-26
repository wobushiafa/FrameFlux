using System;
using System.Collections.Generic;
using System.Threading;

namespace FrameFlux.FFmpeg;

internal static class RtspOpenStreamLimiter
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<int, SemaphoreSlim> Semaphores = [];

    internal static SemaphoreSlim? GetSemaphore(int maxConcurrentOpenStreams)
    {
        if (maxConcurrentOpenStreams <= 0)
        {
            return null;
        }

        var limit = Math.Max(1, maxConcurrentOpenStreams);
        lock (SyncRoot)
        {
            if (Semaphores.TryGetValue(limit, out var semaphore))
            {
                return semaphore;
            }

            semaphore = new SemaphoreSlim(limit, limit);
            Semaphores.Add(limit, semaphore);
            return semaphore;
        }
    }
}
