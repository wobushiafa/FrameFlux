using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class MediaPlaybackClock
{
    private readonly object _sync = new();
    private double _rate = 1d;
    private double? _mediaOriginSeconds;
    private long _wallOriginTimestamp;

    internal void SetRate(double rate, double mediaPositionSeconds)
    {
        ValidateRate(rate);
        lock (_sync)
        {
            _rate = rate;
            ResetLocked(mediaPositionSeconds);
        }
    }

    internal void Reset(double mediaPositionSeconds)
    {
        lock (_sync)
        {
            ResetLocked(mediaPositionSeconds);
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _mediaOriginSeconds = null;
            _wallOriginTimestamp = 0;
        }
    }

    internal bool WaitUntil(
        double mediaPositionSeconds,
        CancellationToken cancellationToken,
        TimeSpan? lateFrameRebaseThreshold = null,
        TimeSpan? forwardJumpRebaseThreshold = null)
    {
        TimeSpan delay;
        lock (_sync)
        {
            if (_mediaOriginSeconds is null || mediaPositionSeconds < _mediaOriginSeconds.Value)
            {
                ResetLocked(mediaPositionSeconds);
                return true;
            }

            var mediaElapsed = mediaPositionSeconds - _mediaOriginSeconds.Value;
            var wallElapsed = RuntimeCompat.Elapsed(_wallOriginTimestamp).TotalSeconds;
            if (lateFrameRebaseThreshold is { } threshold &&
                wallElapsed - mediaElapsed / _rate > threshold.TotalSeconds)
            {
                ResetLocked(mediaPositionSeconds);
                return true;
            }

            var delaySeconds = mediaElapsed / _rate - wallElapsed;
            if (forwardJumpRebaseThreshold is { } forwardThreshold &&
                delaySeconds > forwardThreshold.TotalSeconds)
            {
                ResetLocked(mediaPositionSeconds);
                return true;
            }

            delay = TimeSpan.FromSeconds(Math.Max(0d, delaySeconds));
        }

        return !HighResolutionWait.Wait(delay, cancellationToken);
    }

    private void ResetLocked(double mediaPositionSeconds)
    {
        _mediaOriginSeconds = Math.Max(0d, mediaPositionSeconds);
        _wallOriginTimestamp = Stopwatch.GetTimestamp();
    }

    internal static void ValidateRate(double rate)
    {
        if (!double.IsFinite(rate) || rate is < 0.25d or > 4d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rate),
                rate,
                "Playback rate must be between 0.25 and 4.0.");
        }
    }
}

/// <summary>
/// High-precision cancellation-aware wait avoiding Windows 15.625ms WaitOne timer quantization judder.
/// Uses CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION) on Windows 10 1803+.
/// </summary>
internal static unsafe class HighResolutionWait
{
    private const uint CreateWaitableTimerManualReset = 0x00000001;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0x00000000;
    private const uint Infinite = 0xFFFFFFFF;

    [ThreadStatic]
    private static ThreadWaitableTimer? _threadTimer;

    /// <summary>
    /// Waits for <paramref name="delay"/> unless <paramref name="cancellationToken"/> is signaled.
    /// Returns true if cancelled; false if the full delay elapsed.
    /// </summary>
    internal static bool Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        if (delay <= TimeSpan.Zero)
        {
            return false;
        }

        // Skip kernel transition for sub-millisecond waits to avoid zero-timeout syscalls.
        if (delay.Ticks < TimeSpan.TicksPerMillisecond / 2)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var timer = _threadTimer ??= ThreadWaitableTimer.TryCreate();
            if (timer is not null && timer.Wait(delay, cancellationToken, out var cancelled))
            {
                return cancelled;
            }
        }

        // Cross-platform or pre-Win10-1803 fallback: sleep bulk duration, then refine the final 1.5ms.
        var targetTicks = Stopwatch.GetTimestamp() +
            (long)(delay.TotalSeconds * Stopwatch.Frequency);
        var coarseDelay = delay - TimeSpan.FromMilliseconds(2);
        if (coarseDelay > TimeSpan.Zero)
        {
            if (cancellationToken.WaitHandle.WaitOne(coarseDelay))
            {
                return true;
            }
        }

        while (Stopwatch.GetTimestamp() < targetTicks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            Thread.Yield();
        }

        return false;
    }

    private sealed class ThreadWaitableTimer
    {
        private readonly IntPtr _handle;

        private ThreadWaitableTimer(IntPtr handle) => _handle = handle;

        internal static ThreadWaitableTimer? TryCreate()
        {
            try
            {
                var handle = CreateWaitableTimerExW(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CreateWaitableTimerManualReset | CreateWaitableTimerHighResolution,
                    TimerAllAccess);
                return handle != IntPtr.Zero ? new ThreadWaitableTimer(handle) : null;
            }
            catch
            {
                return null;
            }
        }

        ~ThreadWaitableTimer()
        {
            if (_handle != IntPtr.Zero)
            {
                _ = CloseHandle(_handle);
            }
        }

        internal bool Wait(TimeSpan delay, CancellationToken cancellationToken, out bool cancelled)
        {
            cancelled = false;
            var dueTime = -delay.Ticks; // Relative 100-ns intervals
            if (!SetWaitableTimerEx(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                return false;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                _ = WaitForSingleObject(_handle, Infinite);
                return true;
            }

            var waitHandle = cancellationToken.WaitHandle;
            var addedRef = false;
            var cancelSafeHandle = waitHandle.SafeWaitHandle;
            try
            {
                cancelSafeHandle.DangerousAddRef(ref addedRef);
                IntPtr* handles = stackalloc IntPtr[2];
                handles[0] = _handle;
                handles[1] = cancelSafeHandle.DangerousGetHandle();
                var result = WaitForMultipleObjects(2, handles, waitAll: false, Infinite);
                cancelled = result == WaitObject0 + 1 || cancellationToken.IsCancellationRequested;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (addedRef)
                {
                    cancelSafeHandle.DangerousRelease();
                }
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        IntPtr lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        IntPtr hTimer,
        ref long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        IntPtr reason,
        uint tolerableDelay);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForMultipleObjects(
        uint nCount,
        IntPtr* lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint dwMilliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
