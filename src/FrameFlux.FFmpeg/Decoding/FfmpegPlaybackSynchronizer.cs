using System;
using System.Threading;

namespace FrameFlux.FFmpeg;

internal sealed class FfmpegPlaybackSynchronizer
{
    private readonly bool _isLive;
    private readonly MediaPlaybackClock _playbackClock = new();
    private MediaClockSynchronizer _clockSynchronizer = new();
    private MediaSynchronizationDiagnostics _diagnostics =
        MediaSynchronizationDiagnostics.Empty;

    internal FfmpegPlaybackSynchronizer(bool isLive)
    {
        _isLive = isLive;
    }

    internal MediaSynchronizationDiagnostics Diagnostics => _diagnostics;

    internal void ResetPlaybackClock(double positionSeconds) =>
        _playbackClock.Reset(positionSeconds);

    internal void SetPlaybackRate(double rate, double positionSeconds) =>
        _playbackClock.SetRate(rate, positionSeconds);

    internal void ResetSession()
    {
        _clockSynchronizer = new MediaClockSynchronizer();
        _diagnostics = MediaSynchronizationDiagnostics.Empty;
    }

    internal void DrainAudio(
        FfmpegDecoder decoder,
        AudioPlaybackController? audioPlayback,
        double playbackRate)
    {
        if (audioPlayback is null)
        {
            while (decoder.TryDequeueAudioFrame(out _)) { }
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
            while (decoder.TryDequeueAudioFrame(out _)) { }
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
            return true;
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

        if (!_isLive)
        {
            return _playbackClock.WaitUntil(
                playbackPosition ?? videoPosition.Value,
                cancellationToken);
        }

        var decision = _clockSynchronizer.EvaluateVideo(
            videoPosition.Value,
            audioPlayback?.PositionSeconds);
        RefreshDiagnostics(audioPlayback);
        if (decision.Action == MediaVideoSynchronizationAction.Drop)
        {
            return false;
        }

        if (decision.Action == MediaVideoSynchronizationAction.Delay &&
            cancellationToken.WaitHandle.WaitOne(decision.Delay))
        {
            return false;
        }

        return true;
    }

    internal void RefreshDiagnostics(AudioPlaybackController? audioPlayback)
    {
        _diagnostics = _clockSynchronizer.GetDiagnostics(
            audioPlayback?.ClockResetCount ?? 0);
    }
}
