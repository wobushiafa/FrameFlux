namespace FrameFlux.FFmpeg;

internal enum MediaVideoSynchronizationAction
{
    Render,
    Drop,
    Delay
}

internal readonly record struct MediaVideoSynchronizationDecision(
    MediaVideoSynchronizationAction Action,
    TimeSpan Delay);

internal sealed class MediaClockSynchronizer
{
    private const double LateFrameThresholdSeconds = 0.100d;
    private const double EarlyFrameThresholdSeconds = 0.005d;
    private const double MaximumDelaySeconds = 0.500d;
    private const double TimestampDiscontinuitySeconds = 1d;
    private double? _videoOffsetSeconds;
    private double? _lastRawVideoSeconds;
    private double? _audioSeconds;
    private double? _videoSeconds;
    private double? _audioVideoOffsetSeconds;
    private int _droppedVideoFrames;
    private int _delayedVideoFrames;
    private int _clockResetCount;

    internal MediaVideoSynchronizationDecision EvaluateVideo(
        double rawVideoSeconds,
        double? audioSeconds)
    {
        if (audioSeconds is null)
        {
            _lastRawVideoSeconds = rawVideoSeconds;
            _videoSeconds = rawVideoSeconds;
            _audioSeconds = null;
            _audioVideoOffsetSeconds = null;
            return new MediaVideoSynchronizationDecision(
                MediaVideoSynchronizationAction.Render,
                TimeSpan.Zero);
        }

        if (_lastRawVideoSeconds is { } previousVideo &&
            Math.Abs(rawVideoSeconds - previousVideo) > TimestampDiscontinuitySeconds)
        {
            _videoOffsetSeconds = audioSeconds.Value - rawVideoSeconds;
            _clockResetCount++;
        }
        _lastRawVideoSeconds = rawVideoSeconds;

        if (_videoOffsetSeconds is null)
        {
            var initialDifference = rawVideoSeconds - audioSeconds.Value;
            if (Math.Abs(initialDifference) > TimestampDiscontinuitySeconds)
            {
                _videoOffsetSeconds = -initialDifference;
                _clockResetCount++;
            }
            else
            {
                _videoOffsetSeconds = 0d;
            }
        }

        var synchronizedVideoSeconds = rawVideoSeconds + _videoOffsetSeconds.Value;
        var difference = synchronizedVideoSeconds - audioSeconds.Value;
        if (Math.Abs(difference) > TimestampDiscontinuitySeconds)
        {
            _videoOffsetSeconds -= difference;
            synchronizedVideoSeconds = rawVideoSeconds + _videoOffsetSeconds.Value;
            difference = synchronizedVideoSeconds - audioSeconds.Value;
            _clockResetCount++;
        }

        _audioSeconds = audioSeconds;
        _videoSeconds = rawVideoSeconds;
        _audioVideoOffsetSeconds = difference;

        if (difference < -LateFrameThresholdSeconds)
        {
            _droppedVideoFrames++;
            return new MediaVideoSynchronizationDecision(
                MediaVideoSynchronizationAction.Drop,
                TimeSpan.Zero);
        }

        if (difference > EarlyFrameThresholdSeconds)
        {
            _delayedVideoFrames++;
            return new MediaVideoSynchronizationDecision(
                MediaVideoSynchronizationAction.Delay,
                TimeSpan.FromSeconds(Math.Min(difference, MaximumDelaySeconds)));
        }

        return new MediaVideoSynchronizationDecision(
            MediaVideoSynchronizationAction.Render,
            TimeSpan.Zero);
    }

    internal MediaSynchronizationDiagnostics GetDiagnostics(int audioClockResetCount) =>
        new(
            _audioSeconds is { } audio
                ? TimeSpan.FromSeconds(audio)
                : null,
            _videoSeconds is { } video
                ? TimeSpan.FromSeconds(video)
                : null,
            _audioVideoOffsetSeconds is { } offset
                ? TimeSpan.FromSeconds(offset)
                : null,
            _droppedVideoFrames,
            _delayedVideoFrames,
            _clockResetCount + audioClockResetCount);
}
