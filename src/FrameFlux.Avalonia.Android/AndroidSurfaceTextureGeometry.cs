using Avalonia.Media;

namespace FrameFlux.Avalonia;

internal static class AndroidSurfaceTextureGeometry
{
    internal static float[] BuildVertices(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Stretch stretch)
    {
        var positionScaleX = 1d;
        var positionScaleY = 1d;
        var u0 = 0d;
        var u1 = 1d;
        var v0 = 0d;
        var v1 = 1d;
        var sourceAspect = sourceWidth / (double)sourceHeight;
        var targetAspect = targetWidth / (double)targetHeight;

        if (stretch == Stretch.Uniform)
        {
            if (sourceAspect > targetAspect) positionScaleY = targetAspect / sourceAspect;
            else positionScaleX = sourceAspect / targetAspect;
        }
        else if (stretch == Stretch.UniformToFill)
        {
            if (sourceAspect > targetAspect)
            {
                var visibleWidth = targetAspect / sourceAspect;
                u0 = (1d - visibleWidth) / 2d;
                u1 = 1d - u0;
            }
            else
            {
                var visibleHeight = sourceAspect / targetAspect;
                v0 = (1d - visibleHeight) / 2d;
                v1 = 1d - v0;
            }
        }
        else if (stretch == Stretch.None)
        {
            positionScaleX = sourceWidth / (double)targetWidth;
            positionScaleY = sourceHeight / (double)targetHeight;
        }

        var left = (float)-positionScaleX;
        var right = (float)positionScaleX;
        var bottom = (float)-positionScaleY;
        var top = (float)positionScaleY;
        return
        [
            left, top, (float)u0, (float)v1,
            left, bottom, (float)u0, (float)v0,
            right, bottom, (float)u1, (float)v0,
            left, top, (float)u0, (float)v1,
            right, bottom, (float)u1, (float)v0,
            right, top, (float)u1, (float)v1
        ];
    }
}
