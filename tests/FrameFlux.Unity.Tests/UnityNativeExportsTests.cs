using System.Text;
using FrameFlux.Unity.Native;
using Xunit;

namespace FrameFlux.Unity.Tests;

public sealed unsafe class UnityNativeExportsTests
{
    [Fact]
    public void InvalidHandle_ReturnsErrorAcrossCdeclBoundary()
    {
        delegate* unmanaged[Cdecl]<long, int, double, int> command = &UnityNativeExports.Command;
        delegate* unmanaged[Cdecl]<long, byte*, int, int> error = &UnityNativeExports.Error;
        Assert.Equal(-1, command(long.MaxValue, 0, 0));
        var bytes = new byte[128];
        fixed (byte* output = bytes)
        {
            var required = error(0, output, bytes.Length);
            Assert.True(required > 1);
            Assert.Contains("handle", Encoding.UTF8.GetString(bytes, 0, required - 1));
        }
    }

    [Fact]
    public void NullCreateOutput_IsContainedAndErrorIsNullTerminated()
    {
        delegate* unmanaged[Cdecl]<byte*, long*, int> create = &UnityNativeExports.Create;
        delegate* unmanaged[Cdecl]<long, byte*, int, int> error = &UnityNativeExports.Error;
        Assert.Equal(-1, create(null, null));
        byte output = 255;
        Assert.True(error(0, &output, 1) > 1);
        Assert.Equal(0, output);
    }
}
