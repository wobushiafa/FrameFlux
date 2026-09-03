using System.Runtime.CompilerServices;

namespace FrameFlux.WebRtc;

/// <summary>
/// High-performance unsafe pixel format converter.
/// Converts YUV420P, NV12, RGB/RGBA frames to BGRA32 for presentation rendering.
/// </summary>
public static unsafe class WebRtcPixelConverter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// Converts a planar YUV420P frame to BGRA32.
    /// Supports both Studio Range (16-235) and Full Range (0-255, YUVJ420P).
    /// </summary>
    public static void Yuv420PToBgra32(
        IntPtr yPlane,
        int yStride,
        IntPtr uPlane,
        int uStride,
        IntPtr vPlane,
        int vStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride,
        bool isFullRange = false)
    {
        var yPtr = (byte*)yPlane;
        var uPtr = (byte*)uPlane;
        var vPtr = (byte*)vPlane;
        var dstPtr = (byte*)bgraDest;

        Parallel.For(0, height, y =>
        {
            var yRow = yPtr + (y * yStride);
            var uvRowIndex = y / 2;
            var uRow = uPtr + (uvRowIndex * uStride);
            var vRow = vPtr + (uvRowIndex * vStride);
            var dstRow = (uint*)(dstPtr + (y * bgraStride));

            if (isFullRange)
            {
                // Full range (JPEG / PC range: Y: 0..255, U/V: 0..255)
                for (var x = 0; x < width; x++)
                {
                    var yVal = yRow[x];
                    var uvIndex = x / 2;
                    var uVal = uRow[uvIndex] - 128;
                    var vVal = vRow[uvIndex] - 128;

                    var yScaled = yVal << 8;
                    var r = ClampToByte((yScaled + 359 * vVal + 128) >> 8);
                    var g = ClampToByte((yScaled - 88 * uVal - 183 * vVal + 128) >> 8);
                    var b = ClampToByte((yScaled + 454 * uVal + 128) >> 8);

                    dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
                }
            }
            else
            {
                // Studio range (ITU-R BT.601 limited range: Y: 16..235)
                for (var x = 0; x < width; x++)
                {
                    var yVal = yRow[x];
                    var uvIndex = x / 2;
                    var uVal = uRow[uvIndex] - 128;
                    var vVal = vRow[uvIndex] - 128;

                    var c = (yVal - 16) * 298;
                    var r = ClampToByte((c + 409 * vVal + 128) >> 8);
                    var g = ClampToByte((c - 100 * uVal - 208 * vVal + 128) >> 8);
                    var b = ClampToByte((c + 516 * uVal + 128) >> 8);

                    dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
                }
            }
        });
    }

    /// <summary>
    /// Converts a semi-planar NV12 (interleaved UV) frame to BGRA32.
    /// </summary>
    public static void Nv12ToBgra32(
        IntPtr yPlane,
        int yStride,
        IntPtr uvPlane,
        int uvStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride,
        bool isFullRange = false)
    {
        var yPtr = (byte*)yPlane;
        var uvPtr = (byte*)uvPlane;
        var dstPtr = (byte*)bgraDest;

        Parallel.For(0, height, y =>
        {
            var yRow = yPtr + (y * yStride);
            var uvRow = uvPtr + ((y / 2) * uvStride);
            var dstRow = (uint*)(dstPtr + (y * bgraStride));

            if (isFullRange)
            {
                for (var x = 0; x < width; x++)
                {
                    var yVal = yRow[x];
                    var uvIndex = (x / 2) * 2;
                    var uVal = uvRow[uvIndex] - 128;
                    var vVal = uvRow[uvIndex + 1] - 128;

                    var yScaled = yVal << 8;
                    var r = ClampToByte((yScaled + 359 * vVal + 128) >> 8);
                    var g = ClampToByte((yScaled - 88 * uVal - 183 * vVal + 128) >> 8);
                    var b = ClampToByte((yScaled + 454 * uVal + 128) >> 8);

                    dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
                }
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    var yVal = yRow[x];
                    var uvIndex = (x / 2) * 2;
                    var uVal = uvRow[uvIndex] - 128;
                    var vVal = uvRow[uvIndex + 1] - 128;

                    var c = (yVal - 16) * 298;
                    var r = ClampToByte((c + 409 * vVal + 128) >> 8);
                    var g = ClampToByte((c - 100 * uVal - 208 * vVal + 128) >> 8);
                    var b = ClampToByte((c + 516 * uVal + 128) >> 8);

                    dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
                }
            }
        });
    }

    /// <summary>
    /// Converts a 24-bit RGB frame to BGRA32.
    /// </summary>
    public static void Rgb24ToBgra32(
        IntPtr rgbSource,
        int rgbStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride)
    {
        var srcPtr = (byte*)rgbSource;
        var dstPtr = (byte*)bgraDest;

        Parallel.For(0, height, y =>
        {
            var srcRow = srcPtr + (y * rgbStride);
            var dstRow = (uint*)(dstPtr + (y * bgraStride));

            for (var x = 0; x < width; x++)
            {
                var srcPixel = srcRow + (x * 3);
                var r = srcPixel[0];
                var g = srcPixel[1];
                var b = srcPixel[2];

                dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
            }
        });
    }

    /// <summary>
    /// Converts a 32-bit RGBA frame to BGRA32.
    /// </summary>
    public static void Rgba32ToBgra32(
        IntPtr rgbaSource,
        int rgbaStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride)
    {
        var srcPtr = (byte*)rgbaSource;
        var dstPtr = (byte*)bgraDest;

        Parallel.For(0, height, y =>
        {
            var srcRow = (uint*)(srcPtr + (y * rgbaStride));
            var dstRow = (uint*)(dstPtr + (y * bgraStride));

            for (var x = 0; x < width; x++)
            {
                var rgba = srcRow[x];
                var r = (byte)(rgba & 0xFF);
                var g = (byte)((rgba >> 8) & 0xFF);
                var b = (byte)((rgba >> 16) & 0xFF);
                var a = (byte)((rgba >> 24) & 0xFF);

                dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (a << 24));
            }
        });
    }
}
