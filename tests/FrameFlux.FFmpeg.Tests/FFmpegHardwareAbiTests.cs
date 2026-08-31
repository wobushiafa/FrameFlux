using System.Runtime.InteropServices;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class FFmpegHardwareAbiTests
{
    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    public void ConfigureHardwareDecoderCodecContext_WritesValidatedOffsets(
        int codecMajorVersion)
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var codecContext = Marshal.AllocHGlobal(864);
        try
        {
            var cleared = new byte[864];
            Marshal.Copy(cleared, 0, codecContext, cleared.Length);
            var deviceContext = new IntPtr(0x1234);
            var callback = new IntPtr(0x5678);

            FFmpegAbi.ConfigureHardwareDecoderCodecContext(
                codecContext,
                deviceContext,
                callback,
                codecMajorVersion);

            Assert.Equal(callback, Marshal.ReadIntPtr(codecContext, 192));
            Assert.Equal(deviceContext, Marshal.ReadIntPtr(codecContext, 560));
        }
        finally
        {
            Marshal.FreeHGlobal(codecContext);
        }
    }

    [Fact]
    public void HardwareDecoderLayout_RejectsUnvalidatedCodecMajor()
    {
        var codecContext = Marshal.AllocHGlobal(864);
        try
        {
            Assert.Throws<NotSupportedException>(() =>
                FFmpegAbi.ConfigureHardwareDecoderCodecContext(
                    codecContext,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    codecMajorVersion: 60));
        }
        finally
        {
            Marshal.FreeHGlobal(codecContext);
        }
    }

    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    public void ReadFrameFormat_ReadsValidatedFormatField(int codecMajorVersion)
    {
        var frame = Marshal.AllocHGlobal(440);
        try
        {
            Marshal.WriteInt32(frame, 116, 172);
            Assert.Equal(172, FFmpegAbi.ReadFrameFormat(frame, codecMajorVersion));
        }
        finally
        {
            Marshal.FreeHGlobal(frame);
        }
    }

    [Fact]
    public void ReadHardwareConfig_UsesPublicHeaderLayout()
    {
        var config = Marshal.AllocHGlobal(sizeof(int) * 3);
        try
        {
            Marshal.WriteInt32(config, 0, 172);
            Marshal.WriteInt32(config, sizeof(int), 1);
            Marshal.WriteInt32(config, sizeof(int) * 2, 3);

            var actual = FFmpegAbi.ReadHardwareConfig(config);

            Assert.Equal(172, actual.PixelFormat);
            Assert.Equal(1, actual.Methods);
            Assert.Equal(3, actual.DeviceType);
        }
        finally
        {
            Marshal.FreeHGlobal(config);
        }
    }

    [Fact]
    public void GetStreamStartTimestamp_ReadsPublicHeaderLayout()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var stream = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.Copy(new byte[64], 0, stream, 64);
            Marshal.WriteInt64(stream, 40, 90_000);

            Assert.Equal(90_000, FFmpegAbi.GetStreamStartTimestamp(stream));
        }
        finally
        {
            Marshal.FreeHGlobal(stream);
        }
    }

    [Fact]
    public void D3D11FrameLease_HoldsTextureAndArraySliceUntilDisposed()
    {
        var released = false;
        var lease = new FfmpegMediaFrameLease(IntPtr.Zero, 0, _ => released = true);

        lease.ResetD3D11(1920, 1080, new IntPtr(0x1234), 7);

        Assert.Equal(FfmpegNativePixelFormat.D3D11Texture, lease.PixelFormat);
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
