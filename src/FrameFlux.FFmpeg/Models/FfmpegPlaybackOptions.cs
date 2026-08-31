namespace FrameFlux.FFmpeg;

internal sealed class FfmpegPlaybackOptions
{
    public FfmpegVideoDecodingMode VideoDecodingMode { get; init; } = FfmpegVideoDecodingMode.HardwarePreferred;

    public FfmpegFrameDeliveryMode FrameDeliveryMode { get; init; } = FfmpegFrameDeliveryMode.CpuMemory;
    
    public System.IntPtr ExternalHwDevice { get; init; }

    public string Transport { get; init; } = "tcp";

    public int OpenTimeoutMilliseconds { get; init; } = 5000;

    public int EndpointProbeTimeoutMilliseconds { get; init; }

    public int ReadTimeoutMilliseconds { get; init; } = 5000;

    public bool ReconnectEnabled { get; init; } = true;

    public int ReconnectInitialDelayMilliseconds { get; init; } = 3000;

    public int ReconnectMaximumDelayMilliseconds { get; init; } = 60000;

    public int? MaximumReconnectAttempts { get; init; }

    public SemaphoreSlim? OpenOperationSemaphore { get; init; }

    public double MaxFramesPerSecond { get; init; }

    public int MaxVideoWidth { get; init; }

    public int MaxVideoHeight { get; init; }

    public bool LowLatency { get; init; }

    public bool EnableAudio { get; init; } = true;

    public bool CreateSnapshotFrames { get; init; }

    public double AudioGainDecibels { get; init; }

    public string? AudioOutputDeviceId { get; init; }

    public int AudioBufferDurationMilliseconds { get; init; } = 100;

    public double Volume { get; init; } = 1d;

    public bool IsMuted { get; init; }

    public bool ForceOpaqueAlpha { get; init; } = true;

    public FfmpegScaleQuality ScaleQuality { get; init; } = FfmpegScaleQuality.Bilinear;
}
