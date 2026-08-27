using System.Runtime.InteropServices;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FFmpegHardwareAbiTests
{
    [Fact]
    public void ConfigureD3D11vaCodecContext_WritesValidatedFfmpeg7Offsets()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var codecContext = Marshal.AllocHGlobal(864);
        try
        {
            Span<byte> cleared = new byte[864];
            Marshal.Copy(cleared.ToArray(), 0, codecContext, cleared.Length);
            var deviceContext = new IntPtr(0x1234);
            var callback = new IntPtr(0x5678);

            FFmpegAbi.ConfigureD3D11vaCodecContext(
                codecContext,
                deviceContext,
                callback,
                codecMajorVersion: 61);

            Assert.Equal(callback, Marshal.ReadIntPtr(codecContext, 192));
            Assert.Equal(deviceContext, Marshal.ReadIntPtr(codecContext, 560));
        }
        finally
        {
            Marshal.FreeHGlobal(codecContext);
        }
    }

    [Fact]
    public void ConfigureD3D11vaCodecContext_RejectsUnvalidatedCodecMajor()
    {
        var codecContext = Marshal.AllocHGlobal(864);
        try
        {
            Assert.Throws<NotSupportedException>(() =>
                FFmpegAbi.ConfigureD3D11vaCodecContext(
                    codecContext,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    codecMajorVersion: 62));
        }
        finally
        {
            Marshal.FreeHGlobal(codecContext);
        }
    }

    [Fact]
    public void ReadFrameFormat_ReadsFfmpeg7FormatField()
    {
        var frame = Marshal.AllocHGlobal(440);
        try
        {
            Marshal.WriteInt32(frame, 116, 172);
            Assert.Equal(172, FFmpegAbi.ReadFrameFormat(frame));
        }
        finally
        {
            Marshal.FreeHGlobal(frame);
        }
    }

    [Fact]
    public void D3D11FrameLease_HoldsTextureAndArraySliceUntilDisposed()
    {
        var released = false;
        var lease = new FfmpegMediaFrameLease(IntPtr.Zero, 0, _ => released = true);

        lease.ResetD3D11(1920, 1080, new IntPtr(0x1234), 7);

        Assert.Equal(RtspNativePixelFormat.D3D11Texture, lease.PixelFormat);
        Assert.Equal(new IntPtr(0x1234), lease.D3D11Texture);
        Assert.Equal(7, lease.D3D11ArraySlice);
        Assert.True(((IMediaFrameLease)lease).TryGetD3D11Texture(out var texture));
        Assert.Equal(new IntPtr(0x1234), texture.Texture);
        Assert.Equal(7, texture.ArraySlice);
        Assert.False(((IMediaFrameLease)lease).TryGetCpuBuffer(out _));
        Assert.False(released);

        lease.Dispose();

        Assert.True(released);
    }
}
