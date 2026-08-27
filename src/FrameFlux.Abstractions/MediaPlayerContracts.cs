namespace FrameFlux;

public interface IMediaPlayer : IAsyncDisposable
{
    MediaSource? Source { get; }

    MediaOpenOptions Options { get; }

    MediaPlaybackState State { get; }

    MediaCapabilities Capabilities { get; }

    MediaDiagnostics Diagnostics { get; }

    double Volume { get; set; }

    bool IsMuted { get; set; }

    IMediaVideoOutput? VideoOutput { get; set; }

    TimeSpan Position { get; }

    TimeSpan? Duration { get; }

    event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    event EventHandler<MediaVideoFrame>? FrameReceived;

    ValueTask OpenAsync(
        MediaSource source,
        MediaOpenOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask PlayAsync(CancellationToken cancellationToken = default);

    ValueTask PauseAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IMediaPlayerFactory
{
    IMediaPlayer Create();
}

public sealed record MediaOpenOptions
{
    public MediaStreamSharingMode StreamSharing { get; init; } = MediaStreamSharingMode.Dedicated;

    public MediaTransport Transport { get; init; } = MediaTransport.Auto;

    public MediaHardwareAcceleration HardwareAcceleration { get; init; } = MediaHardwareAcceleration.Auto;

    public MediaRenderPreference RenderPreference { get; init; } = MediaRenderPreference.Auto;

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
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentOpenStreams),
                "Playback limits cannot be negative.");
        }
    }
}

public enum MediaStreamSharingMode
{
    Dedicated,
    Shared
}

public enum MediaTransport
{
    Auto,
    Tcp,
    Udp,
    Http,
    Https
}

public enum MediaHardwareAcceleration
{
    Auto,
    Disabled,
    Enabled
}

public enum MediaRenderPreference
{
    Auto,
    Software,
    NativeSurface,
    CompositedGpu
}

public sealed record MediaCapabilities(
    bool IsLive,
    bool CanPause,
    bool CanSeek,
    bool CanCaptureSnapshots)
{
    public static MediaCapabilities None { get; } = new(false, false, false, false);
}

public enum MediaFramePixelFormat
{
    Bgra32,
    Yuv420P,
    Nv12,
    Nv21,
    D3D11Texture
}

public readonly record struct MediaCpuFrameBuffer(
    IntPtr Buffer,
    int Size,
    IntPtr Plane0,
    IntPtr Plane1,
    IntPtr Plane2,
    int Plane0Stride,
    int Plane1Stride,
    int Plane2Stride);

public readonly record struct MediaD3D11TextureBuffer(
    IntPtr Texture,
    int ArraySlice);

public interface IMediaFrameLease : IDisposable
{
    int Width { get; }

    int Height { get; }

    MediaFramePixelFormat PixelFormat { get; }

    bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer);

    bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture);
}

public interface IMediaVideoOutput
{
    MediaRenderPreference Preference { get; }

    bool Supports(MediaFramePixelFormat pixelFormat);

    // Return true only after accepting ownership. On false or exception, the caller retains ownership.
    bool TryPresent(IMediaFrameLease frame);
}

public sealed record MediaVideoFrame(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Stride,
    MediaFramePixelFormat PixelFormat,
    long Sequence,
    DateTimeOffset CapturedAt);

public sealed record MediaSnapshot(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Stride,
    MediaFramePixelFormat PixelFormat,
    DateTimeOffset CapturedAt);

public sealed record MediaDiagnostics(
    bool IsHardwareAccelerationActive,
    string HardwareDiagnostics,
    double ReadMilliseconds,
    double DecodeMilliseconds,
    int PerformanceSampleCount,
    string? LastError)
{
    public static MediaDiagnostics Empty { get; } = new(
        false,
        "N/A",
        0,
        0,
        0,
        null);
}
