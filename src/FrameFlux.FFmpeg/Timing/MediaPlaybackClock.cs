using System.Diagnostics;

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
            var wallElapsed = Stopwatch.GetElapsedTime(_wallOriginTimestamp).TotalSeconds;
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

        return delay <= TimeSpan.Zero || !cancellationToken.WaitHandle.WaitOne(delay);
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
