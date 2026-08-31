namespace FrameFlux.FFmpeg;

internal sealed class MediaReconnectState
{
    private readonly object _sync = new();
    private readonly FfmpegPlaybackOptions _options;
    private readonly Func<int, int> _nextJitter;
    private int _consecutiveFailures;
    private int _totalAttempts;
    private int _recoveryCount;
    private int _nextDelayMilliseconds;

    public MediaReconnectState(FfmpegPlaybackOptions options, Func<int, int>? nextJitter = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nextJitter = nextJitter ?? Random.Shared.Next;
    }

    public MediaReconnectDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new MediaReconnectDiagnostics(
                    _consecutiveFailures,
                    _totalAttempts,
                    _recoveryCount,
                    TimeSpan.FromMilliseconds(_nextDelayMilliseconds));
            }
        }
    }

    public MediaReconnectDecision RegisterFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            var retryAllowed = _options.ReconnectEnabled &&
                (_options.MaximumReconnectAttempts is null ||
                 _consecutiveFailures <= _options.MaximumReconnectAttempts.Value);
            if (!retryAllowed)
            {
                _nextDelayMilliseconds = 0;
                return new MediaReconnectDecision(false, _consecutiveFailures, TimeSpan.Zero);
            }

            _totalAttempts++;
            _nextDelayMilliseconds = CalculateDelayMilliseconds(_consecutiveFailures);
            return new MediaReconnectDecision(
                true,
                _consecutiveFailures,
                TimeSpan.FromMilliseconds(_nextDelayMilliseconds));
        }
    }

    public bool RegisterSuccess()
    {
        lock (_sync)
        {
            if (_consecutiveFailures == 0)
            {
                return false;
            }

            _consecutiveFailures = 0;
            _nextDelayMilliseconds = 0;
            _recoveryCount++;
            return true;
        }
    }

    internal int CalculateDelayMilliseconds(int consecutiveFailureCount)
    {
        var baseDelay = Math.Max(0, _options.ReconnectInitialDelayMilliseconds);
        var maximumDelay = Math.Max(0, _options.ReconnectMaximumDelayMilliseconds);
        if (baseDelay == 0 || maximumDelay == 0)
        {
            return 0;
        }

        var exponent = Math.Clamp(Math.Max(consecutiveFailureCount, 1) - 1, 0, 5);
        var cappedDelay = (int)Math.Min((long)baseDelay * (1 << exponent), maximumDelay);
        if (cappedDelay >= maximumDelay)
        {
            return maximumDelay;
        }

        var jitterRange = Math.Max(250, cappedDelay / 5);
        var remainingBeforeMaximum = maximumDelay - cappedDelay;
        var exclusiveUpperBound = Math.Min(jitterRange, remainingBeforeMaximum + 1);
        return cappedDelay + Math.Clamp(_nextJitter(exclusiveUpperBound), 0, exclusiveUpperBound - 1);
    }
}

internal readonly record struct MediaReconnectDecision(
    bool RetryAllowed,
    int AttemptNumber,
    TimeSpan Delay);
