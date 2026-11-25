using System;
using System.Threading;

namespace FrameFlux.FFmpeg;

internal sealed class FfmpegPlaybackSynchronizer
{
    private readonly object _diagnosticsSync = new();
    private readonly bool _usesPlaybackClock;
    private readonly TimeSpan? _lateFrameRebaseThreshold;
    private readonly TimeSpan? _forwardJumpRebaseThreshold;
    private readonly TimeSpan? _maximumAudioDrift;
    private readonly MediaPlaybackClock _playbackClock = new();
    private MediaClockSynchronizer _clockSynchronizer = new();
    private int _audioClockResetCount;

    internal FfmpegPlaybackSynchronizer(
        bool usesPlaybackClock,
        TimeSpan? lateFrameRebaseThreshold = null,
        TimeSpan? forwardJumpRebaseThreshold = null,
        TimeSpan? maximumAudioDrift = null)
    {
        _usesPlaybackClock = usesPlaybackClock;
        _lateFrameRebaseThreshold = lateFrameRebaseThreshold;
        _forwardJumpRebaseThreshold = forwardJumpRebaseThreshold;
        _maximumAudioDrift = maximumAudioDrift;
    }

    internal MediaSynchronizationDiagnostics Diagnostics
    {
        get
        {
            lock (_diagnosticsSync)
            {
                return _clockSynchronizer.GetDiagnostics(_audioClockResetCount);
            }
        }
    }

    internal void ResetPlaybackClock(double positionSeconds) =>
        _playbackClock.Reset(positionSeconds);

    internal void SetPlaybackRate(double rate, double positionSeconds) =>
        _playbackClock.SetRate(rate, positionSeconds);

    internal void ResetSession()
    {
        _playbackClock.Reset();
        lock (_diagnosticsSync)
        {
            _clockSynchronizer = new MediaClockSynchronizer();
            _audioClockResetCount = 0;
        }
    }

    internal void DrainAudio(
        FfmpegDecoder decoder,
        AudioPlaybackController? audioPlayback,
        double playbackRate)
    {
        if (audioPlayback is null)
        {
            while (decoder.TryDequeueAudioFrame(out var dropped))
            {
                dropped?.Dispose();
            }
        }
        else
        {
            audioPlayback.SetPlaybackRate(playbackRate);
            while (decoder.TryDequeueAudioFrame(out var audioFrame) && audioFrame is not null)
            {
                audioPlayback.Write(audioFrame);
            }
        }

        RefreshDiagnostics(audioPlayback);
    }

    internal void DrainAudio(
        IPlatformVideoDecoder decoder,
        AudioPlaybackController? audioPlayback,
        double playbackRate)
    {
        if (audioPlayback is null)
        {
            while (decoder.TryDequeueAudioFrame(out var dropped))
            {
                dropped?.Dispose();
            }
        }
        else
        {
            audioPlayback.SetPlaybackRate(playbackRate);
            while (decoder.TryDequeueAudioFrame(out var audioFrame) && audioFrame is not null)
            {
                audioPlayback.Write(audioFrame);
            }
        }

        RefreshDiagnostics(audioPlayback);
    }

    internal bool SynchronizeVideo(
        NativeDecodedFrame frame,
        double playbackPosition,
        AudioPlaybackController? audioPlayback,
        CancellationToken cancellationToken)
    {
        if (frame.Info.PresentationTimestamp == long.MinValue ||
            frame.Info.TimeBaseDenominator <= 0)
        {
            return !_usesPlaybackClock || _playbackClock.WaitUntil(
                playbackPosition,
                cancellationToken,
                _lateFrameRebaseThreshold,
                _forwardJumpRebaseThreshold);
        }

        var videoPosition = frame.Info.PresentationTimestamp *
            (double)frame.Info.TimeBaseNumerator / frame.Info.TimeBaseDenominator;
        return SynchronizeVideo(
            videoPosition,
            playbackPosition,
            audioPlayback,
            cancellationToken);
    }

    internal bool SynchronizeVideo(
        double? videoPosition,
        double? playbackPosition,
        AudioPlaybackController? audioPlayback,
        CancellationToken cancellationToken)
    {
        if (videoPosition is null)
        {
            return true;
        }

        if (_usesPlaybackClock)
        {
            var shouldRender = _playbackClock.WaitUntil(
                playbackPosition ?? videoPosition.Value,
                cancellationToken,
                _lateFrameRebaseThreshold,
                _forwardJumpRebaseThreshold);
            if (shouldRender)
            {
                CorrectAudioDrift(videoPosition.Value, audioPlayback);
                RefreshDiagnostics(audioPlayback);
            }

            return shouldRender;
        }

        MediaVideoSynchronizationDecision decision;
        lock (_diagnosticsSync)
        {
            decision = _clockSynchronizer.EvaluateVideo(
                videoPosition.Value,
                audioPlayback?.PositionSeconds);
            _audioClockResetCount = audioPlayback?.ClockResetCount ?? 0;
        }

        if (decision.Action == MediaVideoSynchronizationAction.Drop)
        {
            return false;
        }

        if (decision.Action == MediaVideoSynchronizationAction.Delay &&
            HighResolutionWait.Wait(decision.Delay, cancellationToken))
        {
            return false;
        }

        return true;
    }

    private void CorrectAudioDrift(
        double videoPosition,
        AudioPlaybackController? audioPlayback)
    {
        if (_maximumAudioDrift is not { } maximumDrift ||
            audioPlayback?.PositionSeconds is not { } audioPosition ||
            Math.Abs(videoPosition - audioPosition) <= maximumDrift.TotalSeconds)
        {
            return;
        }

        audioPlayback.Reset();
    }

    internal void RefreshDiagnostics(AudioPlaybackController? audioPlayback)
    {
        Volatile.Write(ref _audioClockResetCount, audioPlayback?.ClockResetCount ?? 0);
    }
}
