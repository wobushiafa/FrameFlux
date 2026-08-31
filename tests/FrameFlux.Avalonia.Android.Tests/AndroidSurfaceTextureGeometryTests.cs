using Avalonia.Media;
using FrameFlux.Avalonia;
using Xunit;

namespace FrameFlux.Avalonia.Android.Tests;

public sealed class AndroidSurfaceTextureGeometryTests
{
    [Fact]
    public void Fill_MapsTopToOneAndBottomToZero()
    {
        var vertices = AndroidSurfaceTextureGeometry.BuildVertices(
            1920,
            1080,
            1000,
            1000,
            Stretch.Fill);

        Assert.Equal(
            [
                -1f, 1f, 0f, 1f,
                -1f, -1f, 0f, 0f,
                1f, -1f, 1f, 0f,
                -1f, 1f, 0f, 1f,
                1f, -1f, 1f, 0f,
                1f, 1f, 1f, 1f
            ],
            vertices);
    }

    [Fact]
    public void Uniform_LetterboxesWideSource()
    {
        var vertices = AndroidSurfaceTextureGeometry.BuildVertices(
            1920,
            1080,
            1000,
            1000,
            Stretch.Uniform);

        Assert.Equal(0.5625f, vertices[1]);
        Assert.Equal(-0.5625f, vertices[5]);
        Assert.Equal(0f, vertices[2]);
        Assert.Equal(1f, vertices[3]);
    }

    [Fact]
    public void UniformToFill_CropsWideSourceHorizontally()
    {
        var vertices = AndroidSurfaceTextureGeometry.BuildVertices(
            1920,
            1080,
            1000,
            1000,
            Stretch.UniformToFill);

        Assert.Equal(0.21875f, vertices[2]);
        Assert.Equal(0.78125f, vertices[10]);
        Assert.Equal(1f, vertices[1]);
        Assert.Equal(-1f, vertices[5]);
    }
}
