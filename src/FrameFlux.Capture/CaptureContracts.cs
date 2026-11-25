namespace FrameFlux.Capture;

/// <summary>
/// Native video pixel or compressed stream formats reported by capture devices.
/// </summary>
public enum MediaCapturePixelFormat
{
    Unknown = 0,
    Nv12 = 1,
    Yuy2 = 2,
    Mjpeg = 3,
    H264 = 4,
    Hevc = 5,
    Yuv420P = 6,
    Bgra32 = 7
}

/// <summary>
/// Represents a supported video capture stream format.
/// </summary>
public sealed record MediaCaptureVideoFormat(
    int Width,
    int Height,
    double FrameRate,
    MediaCapturePixelFormat PixelFormat);

/// <summary>
/// Describes a local video capture device (such as a USB UVC webcam or HDMI capture card).
/// </summary>
public sealed record MediaCaptureDeviceInfo(
    string DeviceId,
    string DisplayName,
    string UniqueDisplayName,
    IReadOnlyList<MediaCaptureVideoFormat> Formats,
    string? PairedAudioDeviceId = null);

/// <summary>
/// Describes a local audio capture endpoint.
/// </summary>
public sealed record MediaCaptureAudioDeviceInfo(
    string DeviceId,
    string DisplayName);

/// <summary>
/// Controls how audio capture is selected for a video capture session.
/// </summary>
public enum CaptureAudioMode
{
    AutoPaired = 0,
    Disabled = 1,
    SpecificDevice = 2,
    DefaultSystemInput = 3
}

/// <summary>
/// Configuration options for <see cref="CaptureMediaPlayer"/> and <see cref="CaptureMediaPlayerFactory"/>.
/// </summary>
public sealed record CapturePlayerOptions
{
    public int? PreferredWidth { get; init; }

    public int? PreferredHeight { get; init; }

    public double? PreferredFrameRate { get; init; }

    public MediaCapturePixelFormat? PreferredPixelFormat { get; init; }

    public int MaxPoolBufferCount { get; init; } = 8;

    public long MaxPoolRetainedBytes { get; init; } = 64L * 1024 * 1024;

    public bool EnableHardwareVideoOutput { get; init; } = true;

    public void Validate()
    {
        if (PreferredWidth is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreferredWidth),
                PreferredWidth,
                "Preferred width must be greater than zero.");
        }

        if (PreferredHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreferredHeight),
                PreferredHeight,
                "Preferred height must be greater than zero.");
        }

        if (PreferredFrameRate is { } fps &&
            (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreferredFrameRate),
                fps,
                "Preferred frame rate must be finite and greater than zero.");
        }

        if (PreferredPixelFormat is { } format && !RuntimeCompat.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreferredPixelFormat),
                format,
                "Unsupported capture pixel format.");
        }

        RuntimeCompat.AtLeast(MaxPoolBufferCount, 1, nameof(MaxPoolBufferCount));
        RuntimeCompat.AtLeast(MaxPoolRetainedBytes, 1024, nameof(MaxPoolRetainedBytes));
    }
}
