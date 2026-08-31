using Avalonia.Media;

namespace FrameFlux.Avalonia;

internal static class AndroidNativeSurfaceLayoutCalculator
{
    internal static AndroidNativeSurfaceLayout Calculate(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Stretch stretch)
    {
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);
        if (sourceWidth <= 0 || sourceHeight <= 0 || stretch == Stretch.Fill)
        {
            return new AndroidNativeSurfaceLayout(targetWidth, targetHeight);
        }

        if (stretch == Stretch.None)
        {
            return new AndroidNativeSurfaceLayout(sourceWidth, sourceHeight);
        }

        var widthScale = targetWidth / (double)sourceWidth;
        var heightScale = targetHeight / (double)sourceHeight;
        var scale = stretch == Stretch.UniformToFill
            ? Math.Max(widthScale, heightScale)
            : Math.Min(widthScale, heightScale);
        return new AndroidNativeSurfaceLayout(
            Math.Max(1, (int)Math.Round(
                sourceWidth * scale,
                MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(
                sourceHeight * scale,
                MidpointRounding.AwayFromZero)));
    }
}

internal readonly record struct AndroidNativeSurfaceLayout(int Width, int Height);
