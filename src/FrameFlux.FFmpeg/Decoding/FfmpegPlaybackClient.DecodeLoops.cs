using System;
using System.Diagnostics;
using System.Threading;

namespace FrameFlux.FFmpeg;

internal sealed partial class FfmpegPlaybackClient
{
    private enum DecodeLoopOutcome
    {
        Reconnect,
        Terminate
    }

    private DecodeLoopOutcome RunPlatformDecodeLoop(
        IPlatformVideoDecoder platformDecoder,
        AudioPlaybackController? audioPlayback,
        FfmpegPerformanceTracker performanceTracker,
        TimeSpan frameInterval,
        CancellationTokenSource threadCancellationTokenSource)
    {
        var cancellationToken = threadCancellationTokenSource.Token;
        var lastFrameAt = 0L;
        while (_isRunning && !threadCancellationTokenSource.IsCancellationRequested)
        {
            var decodeStart = Stopwatch.GetTimestamp();
            var hasFrame = platformDecoder.TryDecodeNextFrame(out var frame);
            _playbackSynchronizer.DrainAudio(
                platformDecoder,
                audioPlayback,
                Volatile.Read(ref _playbackRate));
            var decodeTicks = Stopwatch.GetTimestamp() - decodeStart;
            if (hasFrame && frame is not null)
            {
                RegisterReconnectSuccess();
                using (frame)
                {
                    if (!_playbackSynchronizer.SynchronizeVideo(
                            frame.PresentationSeconds,
                            frame.PresentationSeconds,
                            audioPlayback,
                            cancellationToken) ||
                        !_frameDispatcher.IsEnabled ||
                        !FfmpegPlaybackPolicy.ShouldRenderFrame(
                            frameInterval,
                            ref lastFrameAt))
                    {
                        continue;
                    }

                    var dispatchStart = Stopwatch.GetTimestamp();
                    frame.Present();
                    var dispatchTicks = Stopwatch.GetTimestamp() - dispatchStart;
                    performanceTracker.Record(
                        platformDecoder.LastReadTicks,
                        platformDecoder.LastCodecTicks,
                        0,
                        decodeTicks,
                        0,
                        dispatchTicks);
                }

                continue;
            }

            if (!_isRunning || threadCancellationTokenSource.IsCancellationRequested)
            {
                return DecodeLoopOutcome.Terminate;
            }

            var reconnect = RegisterReconnectFailure();
            RaiseStreamError(new FfmpegPlaybackError(
                FfmpegPlaybackErrorKind.EndOfStream,
                "Stream ended or no frame was received.",
                WillRetry: reconnect.RetryAllowed));
            if (!reconnect.RetryAllowed)
            {
                return DecodeLoopOutcome.Terminate;
            }

            RaiseConnectionStateChanged(PlaybackConnectionState.Reconnecting);
            SleepBeforeReconnect(threadCancellationTokenSource, reconnect.Delay);
            return DecodeLoopOutcome.Reconnect;
        }

        return DecodeLoopOutcome.Terminate;
    }

    private DecodeLoopOutcome RunFfmpegDecodeLoop(
        FfmpegDecoder decoder,
        AudioPlaybackController? audioPlayback,
        FfmpegPerformanceTracker performanceTracker,
        TimeSpan frameInterval,
        CancellationTokenSource threadCancellationTokenSource)
    {
        var cancellationToken = threadCancellationTokenSource.Token;
        var lastFrameAt = 0L;
        while (_isRunning && !threadCancellationTokenSource.IsCancellationRequested)
        {
            if (!_isLive && !_playbackGate.IsSet)
            {
                if (ProcessPendingSeek(decoder))
                {
                    _playbackSynchronizer.ResetPlaybackClock(Position.TotalSeconds);
                    audioPlayback?.Reset();
                }

                _playbackGate.Wait(TimeSpan.FromMilliseconds(25), cancellationToken);
                continue;
            }

            var decodeStart = Stopwatch.GetTimestamp();
            var hasFrame = decoder.TryDecodeNextFrame(out var frame);
            var seekProcessed = ProcessPendingSeek(decoder);
            if (seekProcessed)
            {
                _playbackSynchronizer.ResetPlaybackClock(Position.TotalSeconds);
                audioPlayback?.Reset();
            }

            _playbackSynchronizer.DrainAudio(
                decoder,
                audioPlayback,
                Volatile.Read(ref _playbackRate));
            var decodeTicks = Stopwatch.GetTimestamp() - decodeStart;
            if (hasFrame && frame is not null)
            {
                RegisterReconnectSuccess();
                Interlocked.Exchange(ref _positionTicks, decoder.Position.Ticks);
                try
                {
                    if (!_playbackSynchronizer.SynchronizeVideo(
                            frame,
                            decoder.Position.TotalSeconds,
                            audioPlayback,
                            cancellationToken) ||
                        !_frameDispatcher.IsEnabled ||
                        !FfmpegPlaybackPolicy.ShouldRenderFrame(
                            frameInterval,
                            ref lastFrameAt))
                    {
                        continue;
                    }

                    var dispatchMetrics = _frameDispatcher.Dispatch(
                        frame,
                        decoder,
                        _options,
                        OnFrameReceived,
                        OnFrameLeaseReceived,
                        OnSnapshotFrameLeaseReceived);
                    performanceTracker.Record(
                        decoder.LastReadTicks,
                        decoder.LastCodecTicks,
                        decoder.LastHardwareTransferTicks,
                        decodeTicks,
                        dispatchMetrics.ConvertTicks,
                        dispatchMetrics.DispatchTicks);
                }
                finally
                {
                    frame.Dispose();
                }

                continue;
            }

            if (!_isRunning || threadCancellationTokenSource.IsCancellationRequested)
            {
                return DecodeLoopOutcome.Terminate;
            }

            if (!_isLive)
            {
                if (seekProcessed)
                {
                    _playbackSynchronizer.ResetSession();
                    continue;
                }

                while (_isRunning &&
                       !threadCancellationTokenSource.IsCancellationRequested &&
                       Volatile.Read(ref _pendingSeek) is null)
                {
                    cancellationToken.WaitHandle.WaitOne(25);
                }

                if (!_isRunning || threadCancellationTokenSource.IsCancellationRequested)
                {
                    return DecodeLoopOutcome.Terminate;
                }

                _ = ProcessPendingSeek(decoder);
                _playbackSynchronizer.ResetSession();
                continue;
            }

            var reconnect = RegisterReconnectFailure();
            RaiseStreamError(new FfmpegPlaybackError(
                FfmpegPlaybackErrorKind.EndOfStream,
                "Stream ended or no frame was received.",
                WillRetry: reconnect.RetryAllowed));
            if (!reconnect.RetryAllowed)
            {
                return DecodeLoopOutcome.Terminate;
            }

            RaiseConnectionStateChanged(PlaybackConnectionState.Reconnecting);
            SleepBeforeReconnect(threadCancellationTokenSource, reconnect.Delay);
            return DecodeLoopOutcome.Reconnect;
        }

        return DecodeLoopOutcome.Terminate;
    }
}
