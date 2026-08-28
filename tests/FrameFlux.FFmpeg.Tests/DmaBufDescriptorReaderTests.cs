using System.Runtime.InteropServices;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class DmaBufDescriptorReaderTests
{
    [Fact]
    public void Read_ParsesFfmpegDrmPrimeDescriptor()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var descriptor = Marshal.AllocHGlobal(528);
        try
        {
            Marshal.Copy(new byte[528], 0, descriptor, 528);
            Marshal.WriteInt32(descriptor, 0, 1);
            Marshal.WriteInt32(descriptor, 8, 17);
            Marshal.WriteInt64(descriptor, 16, 3_110_400);
            Marshal.WriteInt64(descriptor, 24, 0);
            Marshal.WriteInt32(descriptor, 104, 1);
            Marshal.WriteInt32(descriptor, 112, unchecked((int)LinuxNv12));
            Marshal.WriteInt32(descriptor, 116, 2);
            Marshal.WriteInt32(descriptor, 120, 0);
            Marshal.WriteInt64(descriptor, 128, 0);
            Marshal.WriteInt64(descriptor, 136, 1920);
            Marshal.WriteInt32(descriptor, 144, 0);
            Marshal.WriteInt64(descriptor, 152, 2_073_600);
            Marshal.WriteInt64(descriptor, 160, 1920);

            var result = DmaBufDescriptorReader.Read(descriptor);

            Assert.Equal(1, result.Objects.Length);
            Assert.Equal(17, result.Objects.Span[0].FileDescriptor);
            Assert.Equal(3_110_400, result.Objects.Span[0].Size);
            Assert.Equal(1, result.Layers.Length);
            Assert.Equal(LinuxNv12, result.Layers.Span[0].Format);
            Assert.Equal(2, result.Layers.Span[0].Planes.Length);
            Assert.Equal(2_073_600, result.Layers.Span[0].Planes.Span[1].Offset);
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
        }
    }

    private const uint LinuxNv12 = 0x3231564E;
}
