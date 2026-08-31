using System;
using System.Diagnostics;

namespace FrameFlux.FFmpeg;

internal static class FfmpegPlaybackPolicy
{
    internal static bool ShouldFallbackToSoftware(
        FfmpegPlaybackOptions options,
        Exception exception) =>
        FfmpegPlaybackConfiguration.UsesHardwareDecoding(options.VideoDecodingMode) &&
        FfmpegPlaybackConfiguration.AllowsSoftwareFallback(options.VideoDecodingMode) &&
        exception is FfmpegDecoderRuntimeException { IsHardwareVideoDecodingActive: true };

    internal static string FormatExceptionMessage(Exception exception)
    {
        var message = exception.Message;
        var inner = exception.InnerException;
        while (inner != null)
        {
            message = $"{message} Inner: {inner.Message}";
            inner = inner.InnerException;
        }

        return message;
    }

    internal static FfmpegPlaybackOptions CreateSoftwareFallbackOptions(
        FfmpegPlaybackOptions options) =>
        new()
        {
            VideoDecodingMode = FfmpegVideoDecodingMode.SoftwareOnly,
            FrameDeliveryMode = options.FrameDeliveryMode,
            Transport = options.Transport,
            OpenTimeoutMilliseconds = options.OpenTimeoutMilliseconds,
            EndpointProbeTimeoutMilliseconds = options.EndpointProbeTimeoutMilliseconds,
            ReadTimeoutMilliseconds = options.ReadTimeoutMilliseconds,
            ReconnectEnabled = options.ReconnectEnabled,
            ReconnectInitialDelayMilliseconds = options.ReconnectInitialDelayMilliseconds,
            ReconnectMaximumDelayMilliseconds = options.ReconnectMaximumDelayMilliseconds,
            MaximumReconnectAttempts = options.MaximumReconnectAttempts,
            OpenOperationSemaphore = options.OpenOperationSemaphore,
            MaxFramesPerSecond = options.MaxFramesPerSecond,
            MaxVideoWidth = options.MaxVideoWidth,
            MaxVideoHeight = options.MaxVideoHeight,
            LowLatency = options.LowLatency,
            EnableAudio = options.EnableAudio,
            CreateSnapshotFrames = options.CreateSnapshotFrames,
            AudioGainDecibels = options.AudioGainDecibels,
            AudioOutputDeviceId = options.AudioOutputDeviceId,
            AudioBufferDurationMilliseconds = options.AudioBufferDurationMilliseconds,
            Volume = options.Volume,
            IsMuted = options.IsMuted,
            ForceOpaqueAlpha = options.ForceOpaqueAlpha,
            ScaleQuality = options.ScaleQuality
        };

    internal static bool NativeFrameMatchesDeliveryMode(
        FfmpegFrameDeliveryMode deliveryMode,
        FfmpegNativePixelFormat pixelFormat) =>
        (deliveryMode == FfmpegFrameDeliveryMode.D3D11Texture &&
         pixelFormat == FfmpegNativePixelFormat.D3D11Texture) ||
        (deliveryMode == FfmpegFrameDeliveryMode.DmaBuf &&
         pixelFormat == FfmpegNativePixelFormat.DmaBuf);

    internal static bool ShouldRenderFrame(TimeSpan frameInterval, ref long lastFrameAt)
    {
        if (frameInterval <= TimeSpan.Zero)
        {
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        if (lastFrameAt == 0)
        {
            lastFrameAt = now;
            return true;
        }

        var elapsed = Stopwatch.GetElapsedTime(lastFrameAt, now);
        if (elapsed < frameInterval)
        {
            return false;
        }

        lastFrameAt = now;
        return true;
    }

    internal static (int Width, int Height) CalculateOutputSize(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));
        }

        if (maxWidth <= 0 && maxHeight <= 0)
        {
            return (sourceWidth, sourceHeight);
        }

        var widthScale = maxWidth > 0
            ? (double)maxWidth / sourceWidth
            : double.PositiveInfinity;
        var heightScale = maxHeight > 0
            ? (double)maxHeight / sourceHeight
            : double.PositiveInfinity;
        var scale = Math.Min(1d, Math.Min(widthScale, heightScale));

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }
}
