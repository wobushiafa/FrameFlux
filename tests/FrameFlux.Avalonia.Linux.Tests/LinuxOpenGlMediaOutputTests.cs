using Avalonia.Media;
using Xunit;

namespace FrameFlux.Avalonia.Linux.Tests;

public sealed class LinuxOpenGlMediaOutputTests
{
    [Fact]
    public void NativeSurface_RequestsAndAcceptsOnlyDmaBufFrames()
    {
        using var output = new LinuxNativeSurfaceMediaOutput();

        Assert.Equal(MediaFrameStorageKind.DmaBuf, output.PreferredFrameStorage);
        Assert.True(output.Supports(MediaFrameStorageKind.DmaBuf, MediaPixelFormat.Nv12));
        Assert.True(output.Supports(MediaFrameStorageKind.DmaBuf, MediaPixelFormat.Unknown));
        Assert.False(output.Supports(MediaFrameStorageKind.CpuMemory, MediaPixelFormat.Bgra32));
        Assert.False(output.Supports(MediaFrameStorageKind.D3D11Texture, MediaPixelFormat.Nv12));
    }

    [Fact]
    public void Uniform_PreservesSourceAspectRatio()
    {
        var vertices = LinuxOpenGlMediaOutput.BuildVertices(
            1920,
            1080,
            1000,
            1000,
            Stretch.Uniform);

        Assert.Equal(-1f, vertices[0]);
        Assert.Equal(0.5625f, vertices[1], 4);
        Assert.Equal(-0.5625f, vertices[5], 4);
        Assert.Equal(1f, vertices[8]);
    }

    [Fact]
    public void UniformToFill_CropsWideSourceCoordinates()
    {
        var vertices = LinuxOpenGlMediaOutput.BuildVertices(
            1920,
            1080,
            1000,
            1000,
            Stretch.UniformToFill);

        Assert.Equal(0.21875f, vertices[2], 4);
        Assert.Equal(0.78125f, vertices[10], 4);
        Assert.Equal(-1f, vertices[0]);
        Assert.Equal(1f, vertices[1]);
    }

    [Fact]
    public void None_UsesSourcePixelSizeRelativeToTarget()
    {
        var vertices = LinuxOpenGlMediaOutput.BuildVertices(
            640,
            360,
            1280,
            720,
            Stretch.None);

        Assert.Equal(-0.5f, vertices[0]);
        Assert.Equal(0.5f, vertices[1]);
        Assert.Equal(0.5f, vertices[8]);
        Assert.Equal(-0.5f, vertices[5]);
    }
}
