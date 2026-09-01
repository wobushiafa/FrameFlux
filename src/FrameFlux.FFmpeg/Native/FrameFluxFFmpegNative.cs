using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameFlux.FFmpeg;

internal enum NativeReadResult
{
    Error = -1,
    End = 0,
    Ok = 1,
    Again = 2
}

internal enum NativePixelFormat
{
    Unknown = 0,
    Bgra32 = 1,
    Rgba32 = 2,
    Yuv420P = 3,
    Nv12 = 4,
    Nv21 = 5,
    D3D11Texture = 6,
    DmaBuf = 7
}

internal enum NativeVideoCodec
{
    Unknown = 0,
    H264 = 1,
    Hevc = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRtspOptions
{
    public IntPtr Url;
    public IntPtr Transport;
    public int OpenTimeoutMilliseconds;
    public int ReadTimeoutMilliseconds;
    public int LowLatency;
    public int UseHardwareAcceleration;
    public int FallbackToSoftware;
    public int PreserveHardwareFrames;
    public int EnableAudio;
    public double MaxFramesPerSecond;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStreamInfo
{
    public int Width;
    public int Height;
    public NativeVideoCodec Codec;
    public IntPtr CodecExtraData;
    public int CodecExtraDataSize;
    public int TimeBaseNumerator;
    public int TimeBaseDenominator;
    public long StartTimestamp;
    public long DurationTimestamp;
    public int FrameRateNumerator;
    public int FrameRateDenominator;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeRational
{
    public readonly int Numerator;
    public readonly int Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrameInfo
{
    public int Width;
    public int Height;
    public NativePixelFormat PixelFormat;
    public IntPtr Plane0;
    public IntPtr Plane1;
    public IntPtr Plane2;
    public int Stride0;
    public int Stride1;
    public int Stride2;
    public IntPtr DmaBufDescriptor;
    public long PresentationTimestamp;
    public int TimeBaseNumerator;
    public int TimeBaseDenominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePacketInfo
{
    public IntPtr Data;
    public int Size;
    public long PresentationTimestamp;
    public long DecodeTimestamp;
    public int Flags;
}

internal sealed record NativeAudioFrame(
    byte[] Data,
    int SampleRate,
    int Channels,
    long PresentationTimestamp,
    int TimeBaseNumerator,
    int TimeBaseDenominator)
{
    internal double? PresentationSeconds => PresentationTimestamp == long.MinValue
        ? null
        : PresentationTimestamp * (double)TimeBaseNumerator / TimeBaseDenominator;
}

internal sealed class NativeRtspSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeRtspSessionHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        FrameFluxFFmpegNative.Close(handle);
        return true;
    }
}

internal sealed class NativeVideoFrameHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeVideoFrameHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        FrameFluxFFmpegNative.ReleaseFrame(handle);
        return true;
    }
}

internal sealed class NativeVideoPacketHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeVideoPacketHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        FrameFluxFFmpegNative.ReleasePacket(handle);
        return true;
    }
}

internal ref struct NativeUtf8String
{
    private IntPtr _pointer;

    public NativeUtf8String(string? value)
    {
        _pointer = string.IsNullOrEmpty(value) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);
    }

    public readonly IntPtr Pointer => _pointer;

    public void Dispose()
    {
        if (_pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeCoTaskMem(_pointer);
        _pointer = IntPtr.Zero;
    }
}
