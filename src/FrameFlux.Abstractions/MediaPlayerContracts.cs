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
    public MediaSessionSharingMode SessionSharing { get; init; } = MediaSessionSharingMode.Dedicated;

    public MediaNetworkOptions Network { get; init; } = new();

    public MediaVideoOptions Video { get; init; } = new();

    public MediaAudioOptions Audio { get; init; } = new();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Network);
        ArgumentNullException.ThrowIfNull(Video);
        ArgumentNullException.ThrowIfNull(Audio);
        if (!Enum.IsDefined(SessionSharing))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SessionSharing),
                SessionSharing,
                "Unsupported session sharing mode.");
        }

        Network.Validate();
        Video.Validate();
        Audio.Validate();
    }
}

public sealed record MediaNetworkOptions
{
    public MediaTransport Transport { get; init; } = MediaTransport.Automatic;
    public TimeSpan? OpenTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan? EndpointProbeTimeout { get; init; }
    public TimeSpan? ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public MediaReconnectOptions Reconnect { get; init; } = new();
    public MediaLatencyMode LatencyMode { get; init; } = MediaLatencyMode.Default;

    internal void Validate()
    {
        ValidateEnum(Transport, nameof(Transport));
        ValidateEnum(LatencyMode, nameof(LatencyMode));
        ValidatePositiveTimeout(OpenTimeout, nameof(OpenTimeout));
        ValidatePositiveTimeout(EndpointProbeTimeout, nameof(EndpointProbeTimeout));
        ValidatePositiveTimeout(ReadTimeout, nameof(ReadTimeout));
        ArgumentNullException.ThrowIfNull(Reconnect);
        Reconnect.Validate();
    }

    private static void ValidatePositiveTimeout(TimeSpan? value, string parameterName)
    {
        if (value is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName, timeout, "Timeout must be greater than zero.");
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Unsupported enum value.");
    }
}

public sealed record MediaReconnectOptions
{
    public bool IsEnabled { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMinutes(1);
    public int? MaximumAttempts { get; init; }

    internal void Validate()
    {
        if (InitialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay, "Delay cannot be negative.");
        if (MaximumDelay < InitialDelay)
            throw new ArgumentOutOfRangeException(nameof(MaximumDelay), MaximumDelay, "Maximum delay cannot be less than the initial delay.");
        if (MaximumAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts), MaximumAttempts, "Maximum attempts cannot be negative.");
    }
}

public sealed record MediaVideoOptions
{
    public MediaVideoDecodingPolicy DecodingPolicy { get; init; } = MediaVideoDecodingPolicy.Automatic;
    public int? MaximumWidth { get; init; }
    public int? MaximumHeight { get; init; }
    public double? MaximumFrameRate { get; init; }
    public MediaSnapshotPolicy SnapshotPolicy { get; init; } = MediaSnapshotPolicy.Disabled;

    internal void Validate()
    {
        ValidateEnum(DecodingPolicy, nameof(DecodingPolicy));
        ValidateEnum(SnapshotPolicy, nameof(SnapshotPolicy));
        ValidatePositive(MaximumWidth, nameof(MaximumWidth));
        ValidatePositive(MaximumHeight, nameof(MaximumHeight));
        if (MaximumFrameRate is { } frameRate &&
            (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate)))
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameRate), frameRate, "Maximum frame rate must be finite and greater than zero.");
    }

    private static void ValidatePositive(int? value, string parameterName)
    {
        if (value is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be greater than zero.");
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Unsupported enum value.");
    }
}

public sealed record MediaAudioOptions
{
    public bool IsEnabled { get; init; } = true;

    public double GainDecibels { get; init; }

    public string? OutputDeviceId { get; init; }

    public TimeSpan BufferDuration { get; init; } = TimeSpan.FromMilliseconds(100);

    internal void Validate()
    {
        if (!double.IsFinite(GainDecibels) ||
            GainDecibels is < -60d or > 24d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GainDecibels),
                GainDecibels,
                "Audio gain must be finite and between -60 dB and +24 dB.");
        }

        if (OutputDeviceId is not null && string.IsNullOrWhiteSpace(OutputDeviceId))
        {
            throw new ArgumentException(
                "Audio output device ID cannot be empty or whitespace.",
                nameof(OutputDeviceId));
        }

        if (BufferDuration < TimeSpan.FromMilliseconds(10) ||
            BufferDuration > TimeSpan.FromSeconds(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BufferDuration),
                BufferDuration,
                "Audio buffer duration must be between 10 milliseconds and 2 seconds.");
        }
    }
}

public enum MediaSessionSharingMode
{
    Dedicated,
    Shared
}

public enum MediaTransport
{
    Automatic,
    Tcp,
    Udp,
    HttpTunnel,
    HttpsTunnel
}

public enum MediaLatencyMode
{
    Default,
    Low
}

public enum MediaVideoDecodingPolicy
{
    Automatic,
    SoftwareOnly,
    HardwarePreferred,
    HardwareRequired
}

public enum MediaSnapshotPolicy
{
    Disabled,
    KeepLatestFrame
}

public enum MediaVideoPresentationMode
{
    Automatic,
    SoftwareBitmap,
    NativeSurface,
    GpuComposition
}

public sealed record MediaCapabilities(
    bool IsLive,
    bool CanPause,
    bool CanSeek,
    bool CanCaptureSnapshots)
{
    public static MediaCapabilities None { get; } = new(false, false, false, false);
}

public enum MediaPixelFormat
{
    Unknown,
    Bgra32,
    Yuv420P,
    Nv12,
    Nv21
}

public enum MediaFrameStorageKind
{
    CpuMemory,
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

    MediaFrameStorageKind StorageKind { get; }

    MediaPixelFormat PixelFormat { get; }

    bool TryGetCpuBuffer(out MediaCpuFrameBuffer buffer);

    bool TryGetD3D11Texture(out MediaD3D11TextureBuffer texture);
}

public interface IMediaVideoOutput
{
    MediaFrameStorageKind PreferredFrameStorage { get; }

    bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat);

    // Return true only after accepting ownership. On false or exception, the caller retains ownership.
    bool TryPresent(IMediaFrameLease frame);
}

public sealed record MediaVideoFrame(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Stride,
    MediaPixelFormat PixelFormat,
    long Sequence,
    DateTimeOffset CapturedAt);

public sealed record MediaSnapshot(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Stride,
    MediaPixelFormat PixelFormat,
    DateTimeOffset CapturedAt);

public sealed record MediaDiagnostics(
    bool IsHardwareVideoDecodingActive,
    string VideoDecoderDiagnostics,
    double ReadMilliseconds,
    double DecodeMilliseconds,
    int PerformanceSampleCount,
    string? LastError)
{
    public MediaAudioDiagnostics Audio { get; init; } = MediaAudioDiagnostics.Empty;

    public MediaSynchronizationDiagnostics Synchronization { get; init; } =
        MediaSynchronizationDiagnostics.Empty;

    public static MediaDiagnostics Empty { get; } = new(
        false,
        "N/A",
        0,
        0,
        0,
        null);
}

public sealed record MediaSynchronizationDiagnostics(
    TimeSpan? AudioPosition,
    TimeSpan? VideoPosition,
    TimeSpan? AudioVideoOffset,
    int DroppedVideoFrames,
    int DelayedVideoFrames,
    int ClockResetCount)
{
    public static MediaSynchronizationDiagnostics Empty { get; } = new(
        null,
        null,
        null,
        0,
        0,
        0);
}

public sealed record MediaAudioDiagnostics(
    string Backend,
    string? OutputDeviceId,
    string? OutputDeviceName,
    int SampleRate,
    int Channels,
    TimeSpan ConfiguredBufferDuration,
    TimeSpan QueuedDuration,
    bool IsOperational,
    int RecoveryCount,
    string? LastError)
{
    public static MediaAudioDiagnostics Empty { get; } = new(
        "None",
        null,
        null,
        0,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        false,
        0,
        null);
}
