using System.Diagnostics;

namespace FrameFlux.WebRtc;

/// <summary>Serializes decoding without blocking the RTP receive loop.</summary>
internal sealed class WebRtcVideoDecodeQueue : IAsyncDisposable
{
    private readonly int _maximumFrames;
    private readonly int _maximumBytes;
    private readonly TimeSpan _maximumAge;
    private readonly object _sync = new();
    private readonly Queue<(WebRtcEncodedFrame Frame, bool Refresh, long Arrival)> _frames = new();
    private readonly Action<WebRtcEncodedFrame, bool> _decode;
    private readonly Action _requestKeyFrame;
    private readonly Action? _drainOutput;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _bytes;
    private bool _needsKeyFrame = true;
    private bool _stopped;

    public WebRtcVideoDecodeQueue(Action<WebRtcEncodedFrame, bool> decode, Action requestKeyFrame,
        WebRtcPlayerOptions? options = null, Action? drainOutput = null)
    {
        options ??= new WebRtcPlayerOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPendingVideoFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPendingVideoBytes);
        if (options.MaxPendingVideoAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPendingVideoAge));
        _maximumFrames = options.MaxPendingVideoFrames;
        _maximumBytes = options.MaxPendingVideoBytes;
        _maximumAge = options.MaxPendingVideoAge;
        _decode = decode;
        _requestKeyFrame = requestKeyFrame;
        _drainOutput = drainOutput;
        new Thread(Run) { IsBackground = true, Name = "FrameFlux WebRTC video decode" }.Start();
    }

    public void RequestRecovery()
    {
        lock (_sync)
        {
            _frames.Clear();
            _bytes = 0;
            _needsKeyFrame = true;
        }
    }

    public void Enqueue(WebRtcEncodedFrame frame)
    {
        var requestKeyFrame = false;
        lock (_sync)
        {
            if (_stopped) return;
            if (_frames.Count >= _maximumFrames || _bytes + (long)frame.Payload.Length > _maximumBytes ||
                _frames.TryPeek(out var oldest) && Stopwatch.GetElapsedTime(oldest.Arrival) > _maximumAge)
            {
                _frames.Clear();
                _bytes = 0;
                _needsKeyFrame = true;
            }

            // Dropping an arbitrary inter frame invalidates the rest of its reference chain.
            if (frame.Payload.Length > _maximumBytes ||
                _needsKeyFrame && !WebRtcMediaPlayer.IsKeyFrame(frame.Payload, frame.Format.Codec))
            {
                requestKeyFrame = true;
            }
            else
            {
                _frames.Enqueue((frame, _needsKeyFrame, Stopwatch.GetTimestamp()));
                _bytes += frame.Payload.Length;
                _needsKeyFrame = false;
                Monitor.Pulse(_sync);
            }
        }
        if (requestKeyFrame) _requestKeyFrame();
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _stopped = true;
            _frames.Clear();
            _bytes = 0;
            Monitor.Pulse(_sync);
        }
        return new ValueTask(_completion.Task);
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                (WebRtcEncodedFrame Frame, bool Refresh, long Arrival) next;
                bool expired;
                bool hasFrame;
                lock (_sync)
                {
                    while (!_stopped && _frames.Count == 0)
                    {
                        if (_drainOutput is null) Monitor.Wait(_sync);
                        else { Monitor.Wait(_sync, 10); break; }
                    }
                    if (_stopped) return;
                    hasFrame = _frames.Count != 0;
                    if (!hasFrame)
                    {
                        // MediaCodec output can become ready after the last input
                        // frame. Keep draining it even when network input pauses.
                        next = default;
                        expired = false;
                    }
                    else
                    {
                        next = _frames.Dequeue();
                        _bytes -= next.Frame.Payload.Length;
                        expired = Stopwatch.GetElapsedTime(next.Arrival) > _maximumAge;
                        if (expired)
                        {
                            _frames.Clear();
                            _bytes = 0;
                            _needsKeyFrame = true;
                        }
                    }
                }
                if (expired)
                {
                    _requestKeyFrame();
                    continue;
                }
                try
                {
                    if (!hasFrame) _drainOutput?.Invoke();
                    else _decode(next.Frame, next.Refresh);
                }
                catch (Exception exception)
                {
                    // A consumer must not terminate the receive/decode worker with an unhandled exception.
                    RequestRecovery();
                    System.Diagnostics.Trace.TraceWarning("WebRTC decode consumer failed: {0}", exception);
                    _requestKeyFrame();
                }
            }
        }
        finally
        {
            _completion.TrySetResult();
        }
    }
}

internal sealed class WebRtcDecoderInputDroppedException : Exception
{
    public WebRtcDecoderInputDroppedException() : base("The video decoder could not accept an encoded frame.") { }
}
