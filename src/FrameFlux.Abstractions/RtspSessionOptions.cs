namespace FrameFlux;

public sealed record RtspSessionOptions
{
    public RtspStreamSharingMode StreamSharing { get; init; } = RtspStreamSharingMode.Dedicated;

    public RtspTransport Transport { get; init; } = RtspTransport.Tcp;

    public RtspHardwareAcceleration HardwareAcceleration { get; init; } = RtspHardwareAcceleration.Auto;

    public RtspRenderPreference RenderPreference { get; init; } = RtspRenderPreference.Auto;

    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan EndpointProbeTimeout { get; init; } = TimeSpan.Zero;

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);

    public int MaxConcurrentOpenStreams { get; init; } = 8;

    public double MaxFramesPerSecond { get; init; }

    public int MaxVideoWidth { get; init; }

    public int MaxVideoHeight { get; init; }

    public bool LowLatency { get; init; }

    public bool FallbackToSoftwareDecoding { get; init; } = true;

    public bool CaptureSnapshots { get; init; } = true;

    public bool EnableAudio { get; init; } = true;

    public double Volume { get; init; } = 1d;

    public bool IsMuted { get; init; }

    public void Validate()
    {
        if (OpenTimeout < TimeSpan.Zero ||
            EndpointProbeTimeout < TimeSpan.Zero ||
            ReadTimeout < TimeSpan.Zero ||
            ReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OpenTimeout), "Timeout values cannot be negative.");
        }

        if (MaxConcurrentOpenStreams < 0 ||
            MaxVideoWidth < 0 ||
            MaxVideoHeight < 0 ||
            MaxFramesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentOpenStreams), "Playback limits cannot be negative.");
        }

        if (Volume is < 0d or > 1d || double.IsNaN(Volume))
        {
            throw new ArgumentOutOfRangeException(nameof(Volume), "Volume must be between 0.0 and 1.0.");
        }
    }
}

public enum RtspStreamSharingMode
{
    Dedicated,
    Shared
}

public enum RtspTransport
{
    Tcp,
    Udp,
    Http,
    Https
}

public enum RtspHardwareAcceleration
{
    Auto,
    Disabled,
    Enabled
}

public enum RtspRenderPreference
{
    Auto,
    Software,
    NativeSurface
}
