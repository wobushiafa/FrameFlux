namespace FrameFlux.FFmpeg;

public sealed class RtspStreamOptions
{
    public static int DefaultMaxConcurrentOpenStreams { get; set; } = 8;

    public bool UseHardwareAcceleration { get; init; } = true;

    public RtspHardwareAccelerationMode HardwareAccelerationMode { get; init; } = RtspHardwareAccelerationMode.Auto;

    public RtspRenderMode RenderMode { get; init; } = RtspRenderMode.Auto;

    public bool FallbackToSoftwareDecoding { get; init; } = true;
    
    public System.IntPtr ExternalHwDevice { get; init; }

    public string Transport { get; init; } = "tcp";

    public int OpenTimeoutMilliseconds { get; init; } = 5000;

    public int EndpointProbeTimeoutMilliseconds { get; init; }

    public int ReadTimeoutMilliseconds { get; init; } = 5000;

    public int ReconnectDelayMilliseconds { get; init; } = 3000;

    public int MaxConcurrentOpenStreams { get; init; } = DefaultMaxConcurrentOpenStreams;

    public double MaxFramesPerSecond { get; init; }

    public int MaxVideoWidth { get; init; }

    public int MaxVideoHeight { get; init; }

    public bool LowLatency { get; init; }

    public bool EnableAudio { get; init; } = true;

    public double Volume { get; init; } = 1d;

    public bool IsMuted { get; init; }

    public bool ForceOpaqueAlpha { get; init; } = true;

    public bool EnableLinuxVaapiDmaBufInterop { get; init; }

    public RtspScaleQuality ScaleQuality { get; init; } = RtspScaleQuality.Bilinear;
}
