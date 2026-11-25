#if !ANDROID
using System.Runtime.InteropServices;

namespace FrameFlux.Capture;

internal static unsafe class WindowsMediaFoundationEnumerator
{
    internal static IReadOnlyList<MediaCaptureDeviceInfo> GetVideoDevices(bool includeFormats)
    {
        WindowsMediaFoundationInterop.EnsureInitialized();

        var audioDevices = WindowsWasapiAudioBridge.GetAudioCaptureDevices();
        var raw = new List<(string DeviceId, string DisplayName, IReadOnlyList<MediaCaptureVideoFormat> Formats, string? PairedAudioDeviceId)>();

        IntPtr attributes = IntPtr.Zero;
        IntPtr devicesArray = IntPtr.Zero;
        uint count = 0;

        try
        {
            var hr = WindowsMediaFoundationInterop.MFCreateAttributes(out attributes, 1);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFCreateAttributes));

            hr = WindowsMediaFoundationInterop.AttributesSetGuid(
                attributes,
                WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, "AttributesSetGuid(VIDCAP)");

            hr = WindowsMediaFoundationInterop.MFEnumDeviceSources(attributes, out devicesArray, out count);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFEnumDeviceSources));

            if (devicesArray != IntPtr.Zero && count > 0)
            {
                var ptrs = (IntPtr*)devicesArray;
                for (uint i = 0; i < count; i++)
                {
                    var activate = ptrs[i];
                    if (activate == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        var friendlyName = WindowsMediaFoundationInterop.AttributesGetAllocatedString(
                            activate,
                            WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? $"Video Device {i + 1}";
                        var symbolicLink = WindowsMediaFoundationInterop.AttributesGetAllocatedString(
                            activate,
                            WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                        if (string.IsNullOrWhiteSpace(symbolicLink))
                        {
                            symbolicLink = $"index:{i}";
                        }

                        IReadOnlyList<MediaCaptureVideoFormat> formats = includeFormats
                            ? ReadFormatsFromActivate(activate)
                            : [];
                        var pairedAudio = WindowsWasapiAudioBridge.MatchPairedAudioDevice(
                            symbolicLink,
                            friendlyName,
                            audioDevices);

                        raw.Add((symbolicLink, friendlyName, formats, pairedAudio));
                    }
                    finally
                    {
                        Marshal.Release(activate);
                        ptrs[i] = IntPtr.Zero;
                    }
                }
            }
        }
        finally
        {
            if (devicesArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(devicesArray);
            }

            WindowsMediaFoundationInterop.SafeRelease(ref attributes);
        }

        return CaptureDeviceDiscovery.AssignUniqueDisplayNames(raw);
    }

    internal static IReadOnlyList<MediaCaptureVideoFormat> GetVideoFormats(string deviceId)
    {
        WindowsMediaFoundationInterop.EnsureInitialized();

        IntPtr attributes = IntPtr.Zero;
        IntPtr mediaSource = IntPtr.Zero;
        IntPtr sourceReader = IntPtr.Zero;
        try
        {
            mediaSource = CreateDeviceMediaSource(deviceId);
            var hr = WindowsMediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                mediaSource,
                IntPtr.Zero,
                out sourceReader);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFCreateSourceReaderFromMediaSource));
            return EnumerateNativeFormats(sourceReader);
        }
        finally
        {
            if (sourceReader != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.SourceReaderFlush(
                    sourceReader,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
                WindowsMediaFoundationInterop.SafeRelease(ref sourceReader);
            }

            if (mediaSource != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.MediaSourceShutdown(mediaSource);
                WindowsMediaFoundationInterop.SafeRelease(ref mediaSource);
            }

            WindowsMediaFoundationInterop.SafeRelease(ref attributes);
        }
    }

    internal static IntPtr CreateDeviceMediaSource(string deviceId)
    {
        IntPtr attributes = IntPtr.Zero;
        try
        {
            var hr = WindowsMediaFoundationInterop.MFCreateAttributes(out attributes, 2);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFCreateAttributes));

            hr = WindowsMediaFoundationInterop.AttributesSetGuid(
                attributes,
                WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, "AttributesSetGuid(VIDCAP)");

            hr = WindowsMediaFoundationInterop.AttributesSetString(
                attributes,
                WindowsMediaFoundationInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                deviceId);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, "AttributesSetString(SymbolicLink)");

            hr = WindowsMediaFoundationInterop.MFCreateDeviceSource(attributes, out var mediaSource);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFCreateDeviceSource));
            return mediaSource;
        }
        finally
        {
            WindowsMediaFoundationInterop.SafeRelease(ref attributes);
        }
    }

    internal static IReadOnlyList<MediaCaptureVideoFormat> EnumerateNativeFormats(IntPtr sourceReader)
    {
        var formats = new List<MediaCaptureVideoFormat>();
        var seen = new HashSet<MediaCaptureVideoFormat>();

        for (uint typeIndex = 0; ; typeIndex++)
        {
            var hr = WindowsMediaFoundationInterop.SourceReaderGetNativeMediaType(
                sourceReader,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                typeIndex,
                out var mediaType);
            if (hr == WindowsMediaFoundationInterop.MF_E_NO_MORE_TYPES || hr < 0 || mediaType == IntPtr.Zero)
            {
                break;
            }

            try
            {
                if (TryReadVideoFormat(mediaType, out var format) && seen.Add(format))
                {
                    formats.Add(format);
                }
            }
            finally
            {
                WindowsMediaFoundationInterop.SafeRelease(ref mediaType);
            }
        }

        return formats;
    }

    internal static bool TryReadVideoFormat(IntPtr mediaType, out MediaCaptureVideoFormat format)
    {
        format = default!;
        if (WindowsMediaFoundationInterop.AttributesGetGuid(
                mediaType,
                WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE,
                out var majorType) < 0 ||
            majorType != WindowsMediaFoundationInterop.MFMediaType_Video)
        {
            return false;
        }

        if (WindowsMediaFoundationInterop.AttributesGetUInt64(
                mediaType,
                WindowsMediaFoundationInterop.MF_MT_FRAME_SIZE,
                out var packedSize) < 0)
        {
            return false;
        }

        WindowsMediaFoundationInterop.UnpackSize(packedSize, out var width, out var height);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double fps = 0d;
        if (WindowsMediaFoundationInterop.AttributesGetUInt64(
                mediaType,
                WindowsMediaFoundationInterop.MF_MT_FRAME_RATE,
                out var packedRate) >= 0)
        {
            fps = WindowsMediaFoundationInterop.UnpackFrameRate(packedRate);
        }

        var pixelFormat = MediaCapturePixelFormat.Unknown;
        if (WindowsMediaFoundationInterop.AttributesGetGuid(
                mediaType,
                WindowsMediaFoundationInterop.MF_MT_SUBTYPE,
                out var subtype) >= 0)
        {
            pixelFormat = WindowsMediaFoundationInterop.MapSubtypeToPixelFormat(subtype);
        }

        format = new MediaCaptureVideoFormat(width, height, Math.Round(fps, 3), pixelFormat);
        return true;
    }

    private static IReadOnlyList<MediaCaptureVideoFormat> ReadFormatsFromActivate(IntPtr activate)
    {
        IntPtr mediaSource = IntPtr.Zero;
        IntPtr sourceReader = IntPtr.Zero;
        try
        {
            if (WindowsMediaFoundationInterop.ActivateObject(
                    activate,
                    WindowsMediaFoundationInterop.IID_IMFMediaSource,
                    out mediaSource) < 0 ||
                mediaSource == IntPtr.Zero)
            {
                return [];
            }

            if (WindowsMediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                    mediaSource,
                    IntPtr.Zero,
                    out sourceReader) < 0 ||
                sourceReader == IntPtr.Zero)
            {
                return [];
            }

            return EnumerateNativeFormats(sourceReader);
        }
        catch
        {
            return [];
        }
        finally
        {
            if (sourceReader != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.SourceReaderFlush(
                    sourceReader,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
                WindowsMediaFoundationInterop.SafeRelease(ref sourceReader);
            }

            if (mediaSource != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.MediaSourceShutdown(mediaSource);
                WindowsMediaFoundationInterop.SafeRelease(ref mediaSource);
            }

            _ = WindowsMediaFoundationInterop.ShutdownActivateObject(activate);
        }
    }
}
#endif
