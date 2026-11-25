#if !ANDROID
using System.Globalization;

namespace FrameFlux.Capture;

internal sealed record LinuxVideoDeviceRecord(
    string DeviceId,
    string DeviceNodePath,
    string DisplayName,
    string BusInfo,
    string? UsbDeviceSysfsPath,
    string? UsbVidPid,
    string? PairedAudioDeviceId,
    IReadOnlyList<MediaCaptureVideoFormat> Formats);

internal static unsafe class LinuxV4l2DeviceEnumerator
{
    private const string DevDirectory = "/dev";
    private const string ByIdDirectory = "/dev/v4l/by-id";
    private const string ByPathDirectory = "/dev/v4l/by-path";
    private const string VideoSysfsRoot = "/sys/class/video4linux";

    private static readonly (int Width, int Height)[] StandardProbeResolutions =
    [
        (1920, 1080),
        (1280, 720),
        (640, 480)
    ];

    internal static IReadOnlyList<MediaCaptureDeviceInfo> GetVideoDevices(bool includeFormats)
    {
        var records = EnumerateVideoDeviceRecords(includeFormats);
        if (records.Count == 0)
        {
            return [];
        }

        var raw = new List<(string DeviceId, string DisplayName, IReadOnlyList<MediaCaptureVideoFormat> Formats, string? PairedAudioDeviceId)>(records.Count);
        foreach (var record in records)
        {
            raw.Add((record.DeviceId, record.DisplayName, record.Formats, record.PairedAudioDeviceId));
        }

        return CaptureDeviceDiscovery.AssignUniqueDisplayNames(raw);
    }

    internal static IReadOnlyList<MediaCaptureVideoFormat> GetVideoFormats(string deviceId)
    {
        var nodePath = ResolveDeviceNodePath(deviceId);
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return [];
        }

        var fd = LinuxV4l2Interop.Open(nodePath, LinuxV4l2Interop.O_RDWR | LinuxV4l2Interop.O_NONBLOCK | LinuxV4l2Interop.O_CLOEXEC);
        if (fd < 0)
        {
            fd = LinuxV4l2Interop.Open(nodePath, LinuxV4l2Interop.O_RDONLY | LinuxV4l2Interop.O_NONBLOCK | LinuxV4l2Interop.O_CLOEXEC);
        }

        if (fd < 0)
        {
            return [];
        }

        try
        {
            return EnumerateFormatsOnFd(fd);
        }
        finally
        {
            _ = LinuxV4l2Interop.Close(fd);
        }
    }

    internal static IReadOnlyList<LinuxVideoDeviceRecord> EnumerateVideoDeviceRecords(bool includeFormats)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(DevDirectory))
        {
            return [];
        }

        string[] videoPaths;
        try
        {
            videoPaths = Directory.GetFiles(DevDirectory, "video*");
        }
        catch
        {
            return [];
        }

        var orderedNodes = videoPaths
            .Select(static path => (Path: path, Number: TryParseVideoNodeNumber(Path.GetFileName(path))))
            .Where(static item => item.Number.HasValue)
            .OrderBy(static item => item.Number!.Value)
            .Select(static item => item.Path)
            .ToList();

        if (orderedNodes.Count == 0)
        {
            return [];
        }

        var byIdMap = BuildSymlinkMap(ByIdDirectory);
        var byPathMap = BuildSymlinkMap(ByPathDirectory);
        var audioCandidates = LinuxAlsaAudioBridge.GetAudioCaptureCandidates();
        var results = new List<LinuxVideoDeviceRecord>(orderedNodes.Count);

        foreach (var nodePath in orderedNodes)
        {
            var fd = LinuxV4l2Interop.Open(nodePath, LinuxV4l2Interop.O_RDWR | LinuxV4l2Interop.O_NONBLOCK | LinuxV4l2Interop.O_CLOEXEC);
            if (fd < 0)
            {
                fd = LinuxV4l2Interop.Open(nodePath, LinuxV4l2Interop.O_RDONLY | LinuxV4l2Interop.O_NONBLOCK | LinuxV4l2Interop.O_CLOEXEC);
            }

            if (fd < 0)
            {
                continue;
            }

            try
            {
                LinuxV4l2Interop.V4l2Capability cap = default;
                if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_QUERYCAP, &cap, out _) < 0)
                {
                    continue;
                }

                if (!LinuxV4l2Interop.IsVideoCaptureDevice(cap.Capabilities, cap.DeviceCaps))
                {
                    continue;
                }

                var cardName = LinuxV4l2Interop.ReadNullTerminatedUtf8(cap.Card, 32);
                var busInfo = LinuxV4l2Interop.ReadNullTerminatedUtf8(cap.BusInfo, 32);
                var displayName = string.IsNullOrWhiteSpace(cardName) ? Path.GetFileName(nodePath) : cardName;

                var nodeName = Path.GetFileName(nodePath);
                var (usbSysfsPath, usbVidPid) = TryReadSysfsUsbIdentity(Path.Combine(VideoSysfsRoot, nodeName, "device"));
                var deviceId = SelectStableDeviceId(nodePath, byIdMap, byPathMap);
                var pairedAudioId = LinuxAlsaAudioBridge.MatchPairedAudioDevice(
                    usbSysfsPath,
                    usbVidPid,
                    displayName,
                    audioCandidates);

                var formats = includeFormats ? EnumerateFormatsOnFd(fd) : [];

                results.Add(new LinuxVideoDeviceRecord(
                    deviceId,
                    nodePath,
                    displayName,
                    busInfo,
                    usbSysfsPath,
                    usbVidPid,
                    pairedAudioId,
                    formats));
            }
            finally
            {
                _ = LinuxV4l2Interop.Close(fd);
            }
        }

        return results;
    }

    internal static string? ResolveDeviceNodePath(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var trimmed = deviceId.Trim();
        if (trimmed.StartsWith("/dev/", StringComparison.Ordinal))
        {
            if (File.Exists(trimmed))
            {
                return ResolveSymlinkToAbsolutePath(trimmed) ?? trimmed;
            }

            // Re-scan if a symlink path changed slightly or wasn't present directly.
        }

        var records = EnumerateVideoDeviceRecords(includeFormats: false);
        foreach (var record in records)
        {
            if (record.DeviceId.Equals(trimmed, StringComparison.Ordinal) ||
                record.DeviceNodePath.Equals(trimmed, StringComparison.Ordinal))
            {
                return record.DeviceNodePath;
            }
        }

        return null;
    }

    internal static IReadOnlyList<MediaCaptureVideoFormat> EnumerateFormatsOnFd(int fd)
    {
        var seen = new HashSet<(int Width, int Height, int FpsKey, MediaCapturePixelFormat Format)>();
        var formats = new List<MediaCaptureVideoFormat>();

        for (uint fmtIndex = 0; fmtIndex < 128; fmtIndex++)
        {
            LinuxV4l2Interop.V4l2FmtDesc fmtDesc = default;
            fmtDesc.Index = fmtIndex;
            fmtDesc.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;

            if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_ENUM_FMT, &fmtDesc, out _) < 0)
            {
                break;
            }

            var mappedFormat = LinuxV4l2Interop.MapFourCcToPixelFormat(fmtDesc.PixelFormat);
            if (mappedFormat == MediaCapturePixelFormat.Unknown)
            {
                continue;
            }

            var sizes = EnumerateFrameSizesOnFd(fd, fmtDesc.PixelFormat);
            foreach (var (width, height) in sizes)
            {
                var frameRates = EnumerateFrameRatesOnFd(fd, fmtDesc.PixelFormat, width, height);
                if (frameRates.Count == 0)
                {
                    frameRates = [30d];
                }

                foreach (var fps in frameRates)
                {
                    var roundedFps = Math.Round(fps, 2);
                    var fpsKey = (int)Math.Round(roundedFps * 100d);
                    if (seen.Add((width, height, fpsKey, mappedFormat)))
                    {
                        formats.Add(new MediaCaptureVideoFormat(width, height, roundedFps, mappedFormat));
                    }
                }
            }
        }

        return formats;
    }

    internal static IReadOnlyList<uint> EnumerateRawFourCcsOnFd(int fd)
    {
        var list = new List<uint>();
        for (uint fmtIndex = 0; fmtIndex < 128; fmtIndex++)
        {
            LinuxV4l2Interop.V4l2FmtDesc fmtDesc = default;
            fmtDesc.Index = fmtIndex;
            fmtDesc.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_ENUM_FMT, &fmtDesc, out _) < 0)
            {
                break;
            }

            list.Add(fmtDesc.PixelFormat);
        }

        return list;
    }

    private static List<(int Width, int Height)> EnumerateFrameSizesOnFd(int fd, uint pixelFormat)
    {
        var sizes = new List<(int Width, int Height)>();
        for (uint sizeIndex = 0; sizeIndex < 256; sizeIndex++)
        {
            LinuxV4l2Interop.V4l2FrmSizeEnum frmSize = default;
            frmSize.Index = sizeIndex;
            frmSize.PixelFormat = pixelFormat;

            if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_ENUM_FRAMESIZES, &frmSize, out _) < 0)
            {
                break;
            }

            if (frmSize.Type == LinuxV4l2Interop.V4L2_FRMSIZE_TYPE_DISCRETE)
            {
                var w = checked((int)frmSize.DiscreteWidth);
                var h = checked((int)frmSize.DiscreteHeight);
                if (w > 0 && h > 0)
                {
                    sizes.Add((w, h));
                }
            }
            else if (frmSize.Type is LinuxV4l2Interop.V4L2_FRMSIZE_TYPE_STEPWISE or LinuxV4l2Interop.V4L2_FRMSIZE_TYPE_CONTINUOUS)
            {
                var minW = (int)frmSize.StepwiseMinWidth;
                var maxW = (int)frmSize.StepwiseMaxWidth;
                var minH = (int)frmSize.StepwiseMinHeight;
                var maxH = (int)frmSize.StepwiseMaxHeight;
                foreach (var (probeW, probeH) in StandardProbeResolutions)
                {
                    if (probeW >= minW && probeW <= maxW && probeH >= minH && probeH <= maxH)
                    {
                        sizes.Add((probeW, probeH));
                    }
                }

                if (sizes.Count == 0 && maxW > 0 && maxH > 0)
                {
                    sizes.Add((maxW, maxH));
                }

                break;
            }
            else
            {
                break;
            }
        }

        return sizes;
    }

    private static List<double> EnumerateFrameRatesOnFd(int fd, uint pixelFormat, int width, int height)
    {
        var rates = new List<double>();
        for (uint ivalIndex = 0; ivalIndex < 64; ivalIndex++)
        {
            LinuxV4l2Interop.V4l2FrmIvalEnum frmIval = default;
            frmIval.Index = ivalIndex;
            frmIval.PixelFormat = pixelFormat;
            frmIval.Width = checked((uint)width);
            frmIval.Height = checked((uint)height);

            if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_ENUM_FRAMEINTERVALS, &frmIval, out _) < 0)
            {
                break;
            }

            if (frmIval.Type == LinuxV4l2Interop.V4L2_FRMIVAL_TYPE_DISCRETE)
            {
                var fps = FractToFps(frmIval.Discrete);
                if (fps > 0d)
                {
                    rates.Add(fps);
                }
            }
            else if (frmIval.Type is LinuxV4l2Interop.V4L2_FRMIVAL_TYPE_STEPWISE or LinuxV4l2Interop.V4L2_FRMIVAL_TYPE_CONTINUOUS)
            {
                var maxFps = FractToFps(frmIval.StepwiseMin);
                var minFps = FractToFps(frmIval.StepwiseMax);
                foreach (var standard in new[] { 60d, 30d, 15d })
                {
                    if (standard >= minFps - 0.5d && standard <= maxFps + 0.5d)
                    {
                        rates.Add(standard);
                    }
                }

                if (rates.Count == 0 && maxFps > 0d)
                {
                    rates.Add(maxFps);
                }

                break;
            }
            else
            {
                break;
            }
        }

        return rates;
    }

    internal static double FractToFps(LinuxV4l2Interop.V4l2Fract interval)
    {
        if (interval.Numerator == 0 || interval.Denominator == 0)
        {
            return 0d;
        }

        return (double)interval.Denominator / interval.Numerator;
    }

    internal static int? TryParseVideoNodeNumber(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            !fileName.StartsWith("video", StringComparison.Ordinal) ||
            fileName.Length <= 5)
        {
            return null;
        }

        return int.TryParse(fileName.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    internal static string SelectStableDeviceId(
        string nodePath,
        IReadOnlyDictionary<string, string> byIdMap,
        IReadOnlyDictionary<string, string> byPathMap)
    {
        if (byIdMap.TryGetValue(nodePath, out var byIdPath))
        {
            return byIdPath;
        }

        if (byPathMap.TryGetValue(nodePath, out var byPath))
        {
            return byPath;
        }

        return nodePath;
    }

    private static Dictionary<string, string> BuildSymlinkMap(string symlinkDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(symlinkDirectory))
        {
            return map;
        }

        try
        {
            foreach (var entry in Directory.GetFiles(symlinkDirectory).OrderBy(static p => p, StringComparer.Ordinal))
            {
                var resolved = ResolveSymlinkToAbsolutePath(entry);
                if (!string.IsNullOrWhiteSpace(resolved) && !map.ContainsKey(resolved))
                {
                    map[resolved] = entry;
                }
            }
        }
        catch
        {
            // Ignore unreadable /dev/v4l directories in restricted containers.
        }

        return map;
    }

    private static string? ResolveSymlinkToAbsolutePath(string linkPath)
    {
        var target = LinuxV4l2Interop.TryReadLink(linkPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (Path.IsPathRooted(target))
        {
            return Path.GetFullPath(target);
        }

        var parentDir = Path.GetDirectoryName(linkPath);
        return string.IsNullOrWhiteSpace(parentDir)
            ? Path.GetFullPath(target)
            : Path.GetFullPath(Path.Combine(parentDir, target));
    }

    internal static (string? UsbSysfsPath, string? VidPid) TryReadSysfsUsbIdentity(string deviceSysfsPath)
    {
        if (string.IsNullOrWhiteSpace(deviceSysfsPath))
        {
            return (null, null);
        }

        try
        {
            var resolved = ResolveSysfsRealPath(deviceSysfsPath);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return (null, null);
            }

            // Walk upward looking for idVendor + idProduct (USB device root directory).
            var current = resolved;
            for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                var vendorFile = Path.Combine(current, "idVendor");
                var productFile = Path.Combine(current, "idProduct");
                if (File.Exists(vendorFile) && File.Exists(productFile))
                {
                    var vid = File.ReadAllText(vendorFile).Trim();
                    var pid = File.ReadAllText(productFile).Trim();
                    var token = FormatVidPidToken(vid, pid);
                    return (current, token);
                }

                current = Path.GetDirectoryName(current);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    internal static string? FormatVidPidToken(string? vendorHex, string? productHex)
    {
        if (string.IsNullOrWhiteSpace(vendorHex) || string.IsNullOrWhiteSpace(productHex))
        {
            return null;
        }

        var vid = vendorHex.Trim().ToLowerInvariant();
        var pid = productHex.Trim().ToLowerInvariant();
        if (vid.Length != 4 || pid.Length != 4)
        {
            return null;
        }

        return $"vid_{vid}&pid_{pid}";
    }

    private static string? ResolveSysfsRealPath(string path)
    {
        var current = path;
        for (var hops = 0; hops < 8; hops++)
        {
            var linkTarget = LinuxV4l2Interop.TryReadLink(current);
            if (string.IsNullOrWhiteSpace(linkTarget))
            {
                return Directory.Exists(current) ? Path.GetFullPath(current) : null;
            }

            if (Path.IsPathRooted(linkTarget))
            {
                current = Path.GetFullPath(linkTarget);
            }
            else
            {
                var parent = Path.GetDirectoryName(current);
                current = string.IsNullOrWhiteSpace(parent)
                    ? Path.GetFullPath(linkTarget)
                    : Path.GetFullPath(Path.Combine(parent, linkTarget));
            }
        }

        return Directory.Exists(current) ? current : null;
    }
}
#endif
