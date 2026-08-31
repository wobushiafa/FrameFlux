using Avalonia.Media;
using FrameFlux.Avalonia;
using Xunit;

namespace FrameFlux.Avalonia.Android.Tests;

public sealed class AndroidNativeSurfaceLayoutTests
{
    [Theory]
    [InlineData(Stretch.Fill, 1000, 1000)]
    [InlineData(Stretch.Uniform, 1000, 563)]
    [InlineData(Stretch.UniformToFill, 1778, 1000)]
    [InlineData(Stretch.None, 1920, 1080)]
    public void Calculate_AppliesRequestedStretch(
        Stretch stretch,
        int expectedWidth,
        int expectedHeight)
    {
        var layout = AndroidNativeSurfaceLayoutCalculator.Calculate(
            1920,
            1080,
            1000,
            1000,
            stretch);

        Assert.Equal(expectedWidth, layout.Width);
        Assert.Equal(expectedHeight, layout.Height);
    }

    [Fact]
    public void Calculate_UsesTargetSizeBeforeSourceMetadataArrives()
    {
        var layout = AndroidNativeSurfaceLayoutCalculator.Calculate(
            0,
            0,
            640,
            360,
            Stretch.Uniform);

        Assert.Equal(640, layout.Width);
        Assert.Equal(360, layout.Height);
    }
}
