#if !ANDROID
using System.Runtime.InteropServices;
using System.Text;

namespace FrameFlux.Capture;

internal static unsafe class LinuxV4l2Interop
{
    private const string LibC = "libc";

    // File open flags (identical on linux-x64 and linux-arm64)
    internal const int O_RDONLY = 0;
    internal const int O_RDWR = 2;
    internal const int O_NONBLOCK = 2048;
    internal const int O_CLOEXEC = 524288;

    // mmap flags
    internal const int PROT_READ = 1;
    internal const int PROT_WRITE = 2;
    internal const int MAP_SHARED = 1;
    internal static readonly IntPtr MapFailed = new(-1);

    // poll events
    internal const short POLLIN = 1;
    internal const short POLLERR = 8;
    internal const short POLLHUP = 16;
    internal const short POLLNVAL = 32;

    // eventfd flags
    internal const int EFD_NONBLOCK = 2048;
    internal const int EFD_CLOEXEC = 524288;

    // Common errno values
    internal const int EINTR = 4;
    internal const int EIO = 5;
    internal const int EAGAIN = 11;
    internal const int EACCES = 13;
    internal const int EBUSY = 16;
    internal const int ENODEV = 19;
    internal const int EINVAL = 22;

    // V4L2 ioctl request codes (verified against linux/videodev2.h on LP64 x86_64/arm64)
    internal const nuint VIDIOC_QUERYCAP = 0x80685600;
    internal const nuint VIDIOC_ENUM_FMT = 0xC0405602;
    internal const nuint VIDIOC_G_FMT = 0xC0D05604;
    internal const nuint VIDIOC_S_FMT = 0xC0D05605;
    internal const nuint VIDIOC_REQBUFS = 0xC0145608;
    internal const nuint VIDIOC_QUERYBUF = 0xC0585609;
    internal const nuint VIDIOC_QBUF = 0xC058560F;
    internal const nuint VIDIOC_EXPBUF = 0xC0405610;
    internal const nuint VIDIOC_DQBUF = 0xC0585611;
    internal const nuint VIDIOC_STREAMON = 0x40045612;
    internal const nuint VIDIOC_STREAMOFF = 0x40045613;
    internal const nuint VIDIOC_G_PARM = 0xC0CC5615;
    internal const nuint VIDIOC_S_PARM = 0xC0CC5616;
    internal const nuint VIDIOC_ENUM_FRAMESIZES = 0xC02C564A;
    internal const nuint VIDIOC_ENUM_FRAMEINTERVALS = 0xC034564B;

    // V4L2 capabilities
    internal const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    internal const uint V4L2_CAP_VIDEO_CAPTURE_MPLANE = 0x00001000;
    internal const uint V4L2_CAP_VIDEO_M2M_MPLANE = 0x00004000;
    internal const uint V4L2_CAP_VIDEO_M2M = 0x00008000;
    internal const uint V4L2_CAP_META_CAPTURE = 0x00800000;
    internal const uint V4L2_CAP_STREAMING = 0x04000000;
    internal const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;

    // V4L2 buffer/memory/field constants
    internal const uint V4L2_BUF_TYPE_VIDEO_CAPTURE = 1;
    internal const uint V4L2_MEMORY_MMAP = 1;
    internal const uint V4L2_FIELD_ANY = 0;
    internal const uint V4L2_FIELD_NONE = 1;
    internal const uint V4L2_BUF_FLAG_ERROR = 0x00000040;
    internal const uint V4L2_CAP_TIMEPERFRAME = 0x00001000;

    // Frame size/interval enum types
    internal const uint V4L2_FRMSIZE_TYPE_DISCRETE = 1;
    internal const uint V4L2_FRMSIZE_TYPE_CONTINUOUS = 2;
    internal const uint V4L2_FRMSIZE_TYPE_STEPWISE = 3;
    internal const uint V4L2_FRMIVAL_TYPE_DISCRETE = 1;
    internal const uint V4L2_FRMIVAL_TYPE_CONTINUOUS = 2;
    internal const uint V4L2_FRMIVAL_TYPE_STEPWISE = 3;

    // Colorspace / encoding / quantization
    internal const uint V4L2_COLORSPACE_SMPTE170M = 1;
    internal const uint V4L2_COLORSPACE_REC709 = 3;
    internal const uint V4L2_COLORSPACE_JPEG = 7;
    internal const uint V4L2_COLORSPACE_SRGB = 8;
    internal const uint V4L2_YCBCR_ENC_DEFAULT = 0;
    internal const uint V4L2_YCBCR_ENC_601 = 1;
    internal const uint V4L2_YCBCR_ENC_709 = 2;
    internal const uint V4L2_QUANTIZATION_DEFAULT = 0;
    internal const uint V4L2_QUANTIZATION_FULL_RANGE = 1;
    internal const uint V4L2_QUANTIZATION_LIM_RANGE = 2;

    // V4L2 pixel format FourCCs
    internal const uint V4L2_PIX_FMT_NV12 = 0x3231564E;   // 'NV12'
    internal const uint V4L2_PIX_FMT_YUYV = 0x56595559;   // 'YUYV'
    internal const uint V4L2_PIX_FMT_MJPEG = 0x47504A4D;  // 'MJPG'
    internal const uint V4L2_PIX_FMT_JPEG = 0x4745504A;   // 'JPEG'
    internal const uint V4L2_PIX_FMT_YUV420 = 0x32315559; // 'YU12'
    internal const uint V4L2_PIX_FMT_H264 = 0x34363248;   // 'H264'
    internal const uint V4L2_PIX_FMT_HEVC = 0x43564548;   // 'HEVC'
    internal const uint V4L2_PIX_FMT_BGR32 = 0x34524742;  // 'BGR4'
    internal const uint V4L2_PIX_FMT_XBGR32 = 0x34325258; // 'XR24'
    internal const uint V4L2_PIX_FMT_ABGR32 = 0x34325241; // 'AR24'
    internal const uint V4L2_PIX_FMT_RGB24 = 0x33424752;  // 'RGB3'
    internal const uint V4L2_PIX_FMT_BGR24 = 0x33524742;  // 'BGR3'

    // DRM FourCC for DMA-BUF export
    internal const uint DRM_FORMAT_NV12 = 0x3231564E;

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct V4l2Capability
    {
        [FieldOffset(0)] internal fixed byte Driver[16];
        [FieldOffset(16)] internal fixed byte Card[32];
        [FieldOffset(48)] internal fixed byte BusInfo[32];
        [FieldOffset(80)] internal uint Version;
        [FieldOffset(84)] internal uint Capabilities;
        [FieldOffset(88)] internal uint DeviceCaps;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct V4l2FmtDesc
    {
        [FieldOffset(0)] internal uint Index;
        [FieldOffset(4)] internal uint Type;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(12)] internal fixed byte Description[32];
        [FieldOffset(44)] internal uint PixelFormat;
    }

    [StructLayout(LayoutKind.Explicit, Size = 44)]
    internal struct V4l2FrmSizeEnum
    {
        [FieldOffset(0)] internal uint Index;
        [FieldOffset(4)] internal uint PixelFormat;
        [FieldOffset(8)] internal uint Type;
        [FieldOffset(12)] internal uint DiscreteWidth;
        [FieldOffset(16)] internal uint DiscreteHeight;
        [FieldOffset(12)] internal uint StepwiseMinWidth;
        [FieldOffset(16)] internal uint StepwiseMaxWidth;
        [FieldOffset(20)] internal uint StepwiseStepWidth;
        [FieldOffset(24)] internal uint StepwiseMinHeight;
        [FieldOffset(28)] internal uint StepwiseMaxHeight;
        [FieldOffset(32)] internal uint StepwiseStepHeight;
    }

    [StructLayout(LayoutKind.Sequential, Size = 8)]
    internal struct V4l2Fract
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    internal struct V4l2FrmIvalEnum
    {
        [FieldOffset(0)] internal uint Index;
        [FieldOffset(4)] internal uint PixelFormat;
        [FieldOffset(8)] internal uint Width;
        [FieldOffset(12)] internal uint Height;
        [FieldOffset(16)] internal uint Type;
        [FieldOffset(20)] internal V4l2Fract Discrete;
        [FieldOffset(20)] internal V4l2Fract StepwiseMin;
        [FieldOffset(28)] internal V4l2Fract StepwiseMax;
        [FieldOffset(36)] internal V4l2Fract StepwiseStep;
    }

    [StructLayout(LayoutKind.Explicit, Size = 208)]
    internal struct V4l2Format
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal uint Width;
        [FieldOffset(12)] internal uint Height;
        [FieldOffset(16)] internal uint PixelFormat;
        [FieldOffset(20)] internal uint Field;
        [FieldOffset(24)] internal uint BytesPerLine;
        [FieldOffset(28)] internal uint SizeImage;
        [FieldOffset(32)] internal uint ColorSpace;
        [FieldOffset(40)] internal uint Flags;
        [FieldOffset(44)] internal uint YCbCrEnc;
        [FieldOffset(48)] internal uint Quantization;
        [FieldOffset(52)] internal uint XferFunc;
    }

    [StructLayout(LayoutKind.Explicit, Size = 204)]
    internal struct V4l2StreamParm
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal uint CaptureCapability;
        [FieldOffset(8)] internal uint CaptureMode;
        [FieldOffset(12)] internal V4l2Fract TimePerFrame;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct V4l2RequestBuffers
    {
        [FieldOffset(0)] internal uint Count;
        [FieldOffset(4)] internal uint Type;
        [FieldOffset(8)] internal uint Memory;
    }

    [StructLayout(LayoutKind.Explicit, Size = 88)]
    internal struct V4l2Buffer
    {
        [FieldOffset(0)] internal uint Index;
        [FieldOffset(4)] internal uint Type;
        [FieldOffset(8)] internal uint BytesUsed;
        [FieldOffset(12)] internal uint Flags;
        [FieldOffset(16)] internal uint Field;
        [FieldOffset(56)] internal uint Sequence;
        [FieldOffset(60)] internal uint Memory;
        [FieldOffset(64)] internal uint Offset;
        [FieldOffset(72)] internal uint Length;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct V4l2ExportBuffer
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal uint Index;
        [FieldOffset(8)] internal uint Plane;
        [FieldOffset(12)] internal uint Flags;
        [FieldOffset(16)] internal int Fd;
    }

    [StructLayout(LayoutKind.Sequential, Size = 8)]
    internal struct PollFd
    {
        internal int Fd;
        internal short Events;
        internal short Revents;
    }

    [DllImport(LibC, EntryPoint = "open", SetLastError = true)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    internal static extern int Close(int fd);

    [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlRaw(int fd, nuint request, void* arg);

    [DllImport(LibC, EntryPoint = "poll", SetLastError = true)]
    internal static extern int Poll(PollFd* fds, nuint nfds, int timeoutMilliseconds);

    [DllImport(LibC, EntryPoint = "mmap", SetLastError = true)]
    internal static extern IntPtr Mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport(LibC, EntryPoint = "munmap", SetLastError = true)]
    internal static extern int Munmap(IntPtr addr, nuint length);

    [DllImport(LibC, EntryPoint = "eventfd", SetLastError = true)]
    internal static extern int EventFd(uint initval, int flags);

    [DllImport(LibC, EntryPoint = "read", SetLastError = true)]
    internal static extern nint Read(int fd, void* buf, nuint count);

    [DllImport(LibC, EntryPoint = "write", SetLastError = true)]
    internal static extern nint Write(int fd, void* buf, nuint count);

    [DllImport(LibC, EntryPoint = "readlink", SetLastError = true)]
    private static extern nint ReadLinkRaw(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
        byte* buf,
        nuint bufSize);

    internal static int Ioctl(int fd, nuint request, void* arg, out int error)
    {
        while (true)
        {
            var result = IoctlRaw(fd, request, arg);
            if (result != -1)
            {
                error = 0;
                return result;
            }

            error = Marshal.GetLastWin32Error();
            if (error != EINTR)
            {
                return -1;
            }
        }
    }

    internal static int Ioctl(int fd, nuint request, ref uint value, out int error)
    {
        fixed (uint* ptr = &value)
        {
            return Ioctl(fd, request, ptr, out error);
        }
    }

    internal static string? TryReadLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var buffer = stackalloc byte[1024];
        var length = ReadLinkRaw(path, buffer, 1024);
        if (length <= 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer, checked((int)length));
    }

    internal static string ReadNullTerminatedUtf8(byte* buffer, int maxLength)
    {
        var length = 0;
        while (length < maxLength && buffer[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, length).Trim();
    }

    internal static bool IsVideoCaptureDevice(uint capabilities, uint deviceCaps)
    {
        var effective = (capabilities & V4L2_CAP_DEVICE_CAPS) != 0 ? deviceCaps : capabilities;
        if ((effective & V4L2_CAP_VIDEO_CAPTURE) == 0 ||
            (effective & V4L2_CAP_STREAMING) == 0)
        {
            return false;
        }

        if ((effective & V4L2_CAP_META_CAPTURE) != 0 ||
            (effective & V4L2_CAP_VIDEO_M2M) != 0 ||
            (effective & V4L2_CAP_VIDEO_M2M_MPLANE) != 0)
        {
            return false;
        }

        return true;
    }

    internal static MediaCapturePixelFormat MapFourCcToPixelFormat(uint fourCc) =>
        fourCc switch
        {
            V4L2_PIX_FMT_NV12 => MediaCapturePixelFormat.Nv12,
            V4L2_PIX_FMT_YUYV => MediaCapturePixelFormat.Yuy2,
            V4L2_PIX_FMT_MJPEG or V4L2_PIX_FMT_JPEG => MediaCapturePixelFormat.Mjpeg,
            V4L2_PIX_FMT_YUV420 => MediaCapturePixelFormat.Yuv420P,
            V4L2_PIX_FMT_H264 => MediaCapturePixelFormat.H264,
            V4L2_PIX_FMT_HEVC => MediaCapturePixelFormat.Hevc,
            V4L2_PIX_FMT_BGR32 or V4L2_PIX_FMT_XBGR32 or V4L2_PIX_FMT_ABGR32 or V4L2_PIX_FMT_RGB24 or V4L2_PIX_FMT_BGR24 => MediaCapturePixelFormat.Bgra32,
            _ => MediaCapturePixelFormat.Unknown
        };

    internal static uint MapPixelFormatToPreferredFourCc(MediaCapturePixelFormat format) =>
        format switch
        {
            MediaCapturePixelFormat.Nv12 => V4L2_PIX_FMT_NV12,
            MediaCapturePixelFormat.Yuy2 => V4L2_PIX_FMT_YUYV,
            MediaCapturePixelFormat.Mjpeg => V4L2_PIX_FMT_MJPEG,
            MediaCapturePixelFormat.Yuv420P => V4L2_PIX_FMT_YUV420,
            MediaCapturePixelFormat.H264 => V4L2_PIX_FMT_H264,
            MediaCapturePixelFormat.Hevc => V4L2_PIX_FMT_HEVC,
            MediaCapturePixelFormat.Bgra32 => V4L2_PIX_FMT_XBGR32,
            _ => 0
        };

    internal static bool IsSupportedLinuxPixelFormat(MediaCapturePixelFormat format, bool turboJpegAvailable) =>
        format switch
        {
            MediaCapturePixelFormat.Nv12 => true,
            MediaCapturePixelFormat.Yuy2 => true,
            MediaCapturePixelFormat.Yuv420P => true,
            MediaCapturePixelFormat.Bgra32 => true,
            MediaCapturePixelFormat.Mjpeg => turboJpegAvailable,
            _ => false
        };

    internal static string FormatFourCc(uint fourCc) =>
        new(
        [
            (char)(fourCc & 0xFF),
            (char)((fourCc >> 8) & 0xFF),
            (char)((fourCc >> 16) & 0xFF),
            (char)((fourCc >> 24) & 0xFF)
        ]);

    internal static CaptureYuvColorSpace ResolveColorSpace(
        uint colorspace,
        uint ycbcrEnc,
        uint quantization,
        int width,
        int height,
        MediaCapturePixelFormat format)
    {
        var isBt709 = ycbcrEnc switch
        {
            V4L2_YCBCR_ENC_709 => true,
            V4L2_YCBCR_ENC_601 => false,
            _ => colorspace switch
            {
                V4L2_COLORSPACE_REC709 => true,
                V4L2_COLORSPACE_SMPTE170M or V4L2_COLORSPACE_JPEG => false,
                _ => width >= 1280 || height >= 720
            }
        };

        var isFullRange = quantization switch
        {
            V4L2_QUANTIZATION_FULL_RANGE => true,
            V4L2_QUANTIZATION_LIM_RANGE => false,
            _ => colorspace == V4L2_COLORSPACE_JPEG || format == MediaCapturePixelFormat.Mjpeg
        };

        return (isBt709, isFullRange) switch
        {
            (true, true) => CaptureYuvColorSpace.Bt709Full,
            (true, false) => CaptureYuvColorSpace.Bt709Limited,
            (false, true) => CaptureYuvColorSpace.Bt601Full,
            _ => CaptureYuvColorSpace.Bt601Limited
        };
    }

    internal static Exception CreateOpenDeviceException(string devicePath, int errno) =>
        errno switch
        {
            EACCES => new UnauthorizedAccessException(
                $"Permission denied opening V4L2 capture device '{devicePath}' (errno={errno}). " +
                "Ensure the current user belongs to the 'video' group (e.g. 'sudo usermod -aG video $USER') and re-login."),
            EBUSY => new IOException(
                $"V4L2 capture device '{devicePath}' is currently in use by another process (errno={errno})."),
            ENODEV => new IOException(
                $"V4L2 capture device '{devicePath}' is not available or was disconnected (errno={errno})."),
            _ => new IOException(
                $"Unable to open V4L2 capture device '{devicePath}' (errno={errno}).")
        };
}
#endif
