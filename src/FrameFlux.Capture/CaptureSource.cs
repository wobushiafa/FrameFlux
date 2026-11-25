using System.Globalization;
using System.Text;

namespace FrameFlux.Capture;

/// <summary>
/// Helper methods for building and inspecting <see cref="MediaSource"/> instances for local capture devices.
/// </summary>
public static class CaptureSource
{
    public const string Scheme = "capture";
    public const string VideoHost = "video";

    public static bool IsCaptureSource(MediaSource? source) =>
        source is not null && IsCaptureUri(source.Uri);

    public static bool IsCaptureUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase);

    public static MediaSource FromDeviceIndex(
        int index = 0,
        int? width = null,
        int? height = null,
        double? frameRate = null,
        MediaCapturePixelFormat? pixelFormat = null,
        string? audioDeviceId = null,
        bool enablePairedAudio = true)
    {
        RuntimeCompat.AtLeast(index, 0, nameof(index));
        ValidateOptionalParameters(width, height, frameRate, pixelFormat);

        var query = BuildQuery(
            deviceId: null,
            width,
            height,
            frameRate,
            pixelFormat,
            audioDeviceId,
            enablePairedAudio);
        var uriString = string.IsNullOrEmpty(query)
            ? $"{Scheme}://{VideoHost}/{index.ToString(CultureInfo.InvariantCulture)}"
            : $"{Scheme}://{VideoHost}/{index.ToString(CultureInfo.InvariantCulture)}?{query}";
        return MediaSource.FromUri(new Uri(uriString, UriKind.Absolute));
    }

    public static MediaSource FromDeviceId(
        string deviceId,
        int? width = null,
        int? height = null,
        double? frameRate = null,
        MediaCapturePixelFormat? pixelFormat = null,
        string? audioDeviceId = null,
        bool enablePairedAudio = true)
    {
        RuntimeCompat.NotBlank(deviceId, nameof(deviceId));
        ValidateOptionalParameters(width, height, frameRate, pixelFormat);

        var query = BuildQuery(
            deviceId,
            width,
            height,
            frameRate,
            pixelFormat,
            audioDeviceId,
            enablePairedAudio);
        return MediaSource.FromUri(new Uri($"{Scheme}://{VideoHost}/device?{query}", UriKind.Absolute));
    }

    public static MediaSource FromDevice(
        MediaCaptureDeviceInfo device,
        int? width = null,
        int? height = null,
        double? frameRate = null,
        MediaCapturePixelFormat? pixelFormat = null,
        string? audioDeviceId = null,
        bool enablePairedAudio = true)
    {
        RuntimeCompat.NotNull(device, nameof(device));
        return FromDeviceId(
            device.DeviceId,
            width,
            height,
            frameRate,
            pixelFormat,
            audioDeviceId,
            enablePairedAudio);
    }

    public static MediaSource FromDeviceWithAudioMode(
        MediaCaptureDeviceInfo device,
        CaptureAudioMode audioMode,
        string? audioDeviceId = null,
        int? width = null,
        int? height = null,
        double? frameRate = null,
        MediaCapturePixelFormat? pixelFormat = null)
    {
        RuntimeCompat.NotNull(device, nameof(device));
        ValidateOptionalParameters(width, height, frameRate, pixelFormat);
        if (!RuntimeCompat.IsDefined(audioMode))
        {
            throw new ArgumentOutOfRangeException(nameof(audioMode), audioMode, "Unsupported audio capture mode.");
        }

        if (audioMode == CaptureAudioMode.SpecificDevice)
        {
            RuntimeCompat.NotBlank(audioDeviceId, nameof(audioDeviceId));
        }

        var query = BuildQueryWithMode(
            device.DeviceId,
            width,
            height,
            frameRate,
            pixelFormat,
            audioMode,
            audioDeviceId);
        return MediaSource.FromUri(new Uri($"{Scheme}://{VideoHost}/device?{query}", UriKind.Absolute));
    }

    internal static ParsedCaptureUri Parse(Uri uri)
    {
        RuntimeCompat.NotNull(uri, nameof(uri));
        if (!IsCaptureUri(uri))
        {
            throw new NotSupportedException(
                $"The capture backend does not support the '{uri.Scheme}' media scheme.");
        }

        if (!uri.Host.Equals(VideoHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported capture device host '{uri.Host}'. Expected '{VideoHost}'.",
                nameof(uri));
        }

        var parameters = ParseQuery(uri.Query);
        string? deviceId = null;
        int? deviceIndex = null;

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
        {
            deviceIndex = 0;
        }
        else if (path.Equals("device", StringComparison.OrdinalIgnoreCase))
        {
            if (!parameters.TryGetValue("deviceId", out deviceId) || string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException(
                    "Capture device URI with '/device' path requires a non-empty 'deviceId' query parameter.",
                    nameof(uri));
            }
        }
        else if (int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
        {
            deviceIndex = parsedIndex;
        }
        else
        {
            throw new ArgumentException(
                $"Invalid capture device target '{uri.AbsolutePath}'. Expected a numeric index or '/device'.",
                nameof(uri));
        }

        int? width = TryParsePositiveInt(parameters, "width", uri);
        int? height = TryParsePositiveInt(parameters, "height", uri);
        double? fps = TryParsePositiveDouble(parameters, "fps", uri);
        MediaCapturePixelFormat? pixelFormat = TryParsePixelFormat(parameters, "format", uri);

        parameters.TryGetValue("audioDeviceId", out var audioDeviceId);
        if (string.IsNullOrWhiteSpace(audioDeviceId))
        {
            audioDeviceId = null;
        }

        var audioMode = ResolveAudioMode(parameters, audioDeviceId, uri);

        return new ParsedCaptureUri(
            deviceIndex,
            deviceId,
            width,
            height,
            fps,
            pixelFormat,
            audioMode,
            audioDeviceId);
    }

    private static CaptureAudioMode ResolveAudioMode(
        Dictionary<string, string> parameters,
        string? audioDeviceId,
        Uri uri)
    {
        if (audioDeviceId is not null)
        {
            return CaptureAudioMode.SpecificDevice;
        }

        if (!parameters.TryGetValue("audio", out var audioText) || string.IsNullOrWhiteSpace(audioText))
        {
            return CaptureAudioMode.AutoPaired;
        }

        return audioText.Trim().ToLowerInvariant() switch
        {
            "auto" or "paired" or "true" => CaptureAudioMode.AutoPaired,
            "none" or "disabled" or "off" or "false" => CaptureAudioMode.Disabled,
            "default" or "system" => CaptureAudioMode.DefaultSystemInput,
            _ => throw new ArgumentException($"Unsupported capture audio mode '{audioText}'.", nameof(uri))
        };
    }

    private static int? TryParsePositiveInt(Dictionary<string, string> parameters, string key, Uri uri)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"Capture URI parameter '{key}' must be a positive integer.", nameof(uri));
        }

        return parsed;
    }

    private static double? TryParsePositiveDouble(Dictionary<string, string> parameters, string key, Uri uri)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0 || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            throw new ArgumentException($"Capture URI parameter '{key}' must be a finite positive number.", nameof(uri));
        }

        return parsed;
    }

    private static MediaCapturePixelFormat? TryParsePixelFormat(Dictionary<string, string> parameters, string key, Uri uri)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "nv12" => MediaCapturePixelFormat.Nv12,
            "yuy2" => MediaCapturePixelFormat.Yuy2,
            "mjpeg" or "mjpg" => MediaCapturePixelFormat.Mjpeg,
            "h264" => MediaCapturePixelFormat.H264,
            "hevc" or "h265" => MediaCapturePixelFormat.Hevc,
            "yuv420p" or "i420" => MediaCapturePixelFormat.Yuv420P,
            "bgra32" or "bgra" or "rgb32" or "argb32" => MediaCapturePixelFormat.Bgra32,
            _ => throw new ArgumentException($"Unsupported capture pixel format '{value}'.", nameof(uri))
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        var trimmed = query[0] == '?' ? query[1..] : query;
        if (trimmed.Length == 0)
        {
            return result;
        }

        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex < 0)
            {
                var keyOnly = Uri.UnescapeDataString(segment);
                if (!string.IsNullOrEmpty(keyOnly))
                {
                    result[keyOnly] = string.Empty;
                }

                continue;
            }

            var key = Uri.UnescapeDataString(segment[..equalsIndex]);
            var value = Uri.UnescapeDataString(segment[(equalsIndex + 1)..]);
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static void ValidateOptionalParameters(
        int? width,
        int? height,
        double? frameRate,
        MediaCapturePixelFormat? pixelFormat)
    {
        if (width is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be greater than zero.");
        }

        if (height is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be greater than zero.");
        }

        if (frameRate is { } fps && (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)))
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), fps, "Frame rate must be finite and greater than zero.");
        }

        if (pixelFormat is { } format && !RuntimeCompat.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelFormat), format, "Unsupported capture pixel format.");
        }
    }

    private static string BuildQuery(
        string? deviceId,
        int? width,
        int? height,
        double? frameRate,
        MediaCapturePixelFormat? pixelFormat,
        string? audioDeviceId,
        bool enablePairedAudio)
    {
        var mode = !string.IsNullOrWhiteSpace(audioDeviceId)
            ? CaptureAudioMode.SpecificDevice
            : enablePairedAudio
                ? CaptureAudioMode.AutoPaired
                : CaptureAudioMode.Disabled;
        return BuildQueryWithMode(deviceId, width, height, frameRate, pixelFormat, mode, audioDeviceId);
    }

    private static string BuildQueryWithMode(
        string? deviceId,
        int? width,
        int? height,
        double? frameRate,
        MediaCapturePixelFormat? pixelFormat,
        CaptureAudioMode audioMode,
        string? audioDeviceId)
    {
        var builder = new StringBuilder();

        void AppendParam(string key, string value)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            AppendParam("deviceId", deviceId);
        }

        if (width is > 0)
        {
            AppendParam("width", width.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (height is > 0)
        {
            AppendParam("height", height.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (frameRate is > 0)
        {
            AppendParam("fps", frameRate.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (pixelFormat is { } format && format != MediaCapturePixelFormat.Unknown)
        {
            AppendParam("format", FormatPixelFormatToken(format));
        }

        switch (audioMode)
        {
            case CaptureAudioMode.Disabled:
                AppendParam("audio", "none");
                break;
            case CaptureAudioMode.DefaultSystemInput:
                AppendParam("audio", "default");
                break;
            case CaptureAudioMode.SpecificDevice when !string.IsNullOrWhiteSpace(audioDeviceId):
                AppendParam("audioDeviceId", audioDeviceId);
                break;
        }

        return builder.ToString();
    }

    private static string FormatPixelFormatToken(MediaCapturePixelFormat format) =>
        format switch
        {
            MediaCapturePixelFormat.Nv12 => "nv12",
            MediaCapturePixelFormat.Yuy2 => "yuy2",
            MediaCapturePixelFormat.Mjpeg => "mjpeg",
            MediaCapturePixelFormat.H264 => "h264",
            MediaCapturePixelFormat.Hevc => "hevc",
            MediaCapturePixelFormat.Yuv420P => "yuv420p",
            MediaCapturePixelFormat.Bgra32 => "bgra32",
            _ => "unknown"
        };
}

internal sealed record ParsedCaptureUri(
    int? DeviceIndex,
    string? DeviceId,
    int? Width,
    int? Height,
    double? FrameRate,
    MediaCapturePixelFormat? PixelFormat,
    CaptureAudioMode AudioMode,
    string? AudioDeviceId);
