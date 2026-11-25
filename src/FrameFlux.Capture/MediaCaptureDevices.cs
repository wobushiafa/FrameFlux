namespace FrameFlux.Capture;

/// <summary>
/// Discovers local video and audio capture devices on the host machine.
/// </summary>
public static class MediaCaptureDevices
{
    public static IReadOnlyList<MediaCaptureDeviceInfo> GetVideoDevices(bool includeFormats = false) =>
        CaptureDeviceDiscovery.GetVideoDevices(includeFormats);

    public static ValueTask<IReadOnlyList<MediaCaptureDeviceInfo>> GetVideoDevicesAsync(
        bool includeFormats = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IReadOnlyList<MediaCaptureDeviceInfo>>(
            Task.Run(() => CaptureDeviceDiscovery.GetVideoDevices(includeFormats), cancellationToken));
    }

    public static IReadOnlyList<MediaCaptureVideoFormat> GetVideoFormats(string deviceId)
    {
        RuntimeCompat.NotBlank(deviceId, nameof(deviceId));
        return CaptureDeviceDiscovery.GetVideoFormats(deviceId);
    }

    public static IReadOnlyList<MediaCaptureAudioDeviceInfo> GetAudioDevices() =>
        CaptureDeviceDiscovery.GetAudioDevices();
}

internal static class CaptureDeviceDiscovery
{
    internal static IReadOnlyList<MediaCaptureDeviceInfo> GetVideoDevices(bool includeFormats)
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            return WindowsMediaFoundationEnumerator.GetVideoDevices(includeFormats);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxV4l2DeviceEnumerator.GetVideoDevices(includeFormats);
        }
#endif
        return [];
    }

    internal static IReadOnlyList<MediaCaptureVideoFormat> GetVideoFormats(string deviceId)
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            return WindowsMediaFoundationEnumerator.GetVideoFormats(deviceId);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxV4l2DeviceEnumerator.GetVideoFormats(deviceId);
        }
#endif
        return [];
    }

    internal static IReadOnlyList<MediaCaptureAudioDeviceInfo> GetAudioDevices()
    {
#if !ANDROID
        if (OperatingSystem.IsWindows())
        {
            return WindowsWasapiAudioBridge.GetAudioCaptureDevices();
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxAlsaAudioBridge.GetAudioCaptureDevices();
        }
#endif
        return [];
    }

    internal static IReadOnlyList<MediaCaptureDeviceInfo> AssignUniqueDisplayNames(
        IReadOnlyList<(string DeviceId, string DisplayName, IReadOnlyList<MediaCaptureVideoFormat> Formats, string? PairedAudioDeviceId)> rawDevices)
    {
        if (rawDevices.Count == 0)
        {
            return [];
        }

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in rawDevices)
        {
            nameCounts.TryGetValue(device.DisplayName, out var count);
            nameCounts[device.DisplayName] = count + 1;
        }

        var seenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MediaCaptureDeviceInfo>(rawDevices.Count);
        foreach (var device in rawDevices)
        {
            seenCounts.TryGetValue(device.DisplayName, out var seen);
            seen++;
            seenCounts[device.DisplayName] = seen;

            var uniqueName = nameCounts[device.DisplayName] > 1
                ? $"{device.DisplayName} (#{seen})"
                : device.DisplayName;
            result.Add(new MediaCaptureDeviceInfo(
                device.DeviceId,
                device.DisplayName,
                uniqueName,
                device.Formats,
                device.PairedAudioDeviceId));
        }

        return result;
    }
}
