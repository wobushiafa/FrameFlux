using System.Runtime.CompilerServices;

namespace FrameFlux.Capture;

/// <summary>
/// YUV color matrix and nominal range used when converting capture frames to BGRA32 on the CPU.
/// </summary>
public enum CaptureYuvColorSpace
{
    /// <summary>ITU-R BT.601 limited range (Y: 16..235, UV: 16..240). Typical for SD video.</summary>
    Bt601Limited = 0,

    /// <summary>ITU-R BT.709 limited range (Y: 16..235, UV: 16..240). Standard for 720p/1080p HD video.</summary>
    Bt709Limited = 1,

    /// <summary>ITU-R BT.601 full range (Y/UV: 0..255). Common for MJPEG / JPEG webcams.</summary>
    Bt601Full = 2,

    /// <summary>ITU-R BT.709 full range (Y/UV: 0..255).</summary>
    Bt709Full = 3
}

/// <summary>
/// High-performance unsafe pixel format conversions from capture buffers to BGRA32.
/// Supports positive and negative (bottom-up) source strides as well as BT.601/BT.709 limited/full ranges.
/// </summary>
public static unsafe class CapturePixelConverter
{
    private readonly record struct YuvCoefficients(int YOffset, int Cy, int Crv, int Cgu, int Cgv, int Cbu);

    private static YuvCoefficients GetCoefficients(CaptureYuvColorSpace colorSpace) =>
        colorSpace switch
        {
            // BT.709 limited: Y' = (Y - 16) * 1.16438, R = Y' + 1.79274*V, G = Y' - 0.21325*U - 0.53291*V, B = Y' + 2.11240*U
            CaptureYuvColorSpace.Bt709Limited => new(16, 298, 459, -55, -136, 541),
            // BT.601 full:    Y' = Y * 1.0,           R = Y' + 1.40200*V, G = Y' - 0.34414*U - 0.71414*V, B = Y' + 1.77200*U
            CaptureYuvColorSpace.Bt601Full => new(0, 256, 359, -88, -183, 454),
            // BT.709 full:    Y' = Y * 1.0,           R = Y' + 1.57480*V, G = Y' - 0.18732*U - 0.46812*V, B = Y' + 1.85560*U
            CaptureYuvColorSpace.Bt709Full => new(0, 256, 403, -48, -120, 475),
            // BT.601 limited: Y' = (Y - 16) * 1.16438, R = Y' + 1.59603*V, G = Y' - 0.39176*U - 0.81297*V, B = Y' + 2.01723*U
            _ => new(16, 298, 409, -100, -208, 516)
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    public static void Nv12ToBgra32(
        IntPtr yPlane,
        int yStride,
        IntPtr uvPlane,
        int uvStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride) =>
        Nv12ToBgra32(
            yPlane,
            yStride,
            uvPlane,
            uvStride,
            bgraDest,
            width,
            height,
            bgraStride,
            CaptureYuvColorSpace.Bt601Limited);

    public static void Nv12ToBgra32(
        IntPtr yPlane,
        int yStride,
        IntPtr uvPlane,
        int uvStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride,
        CaptureYuvColorSpace colorSpace)
    {
        if (width <= 0 || height <= 0 || yPlane == IntPtr.Zero || uvPlane == IntPtr.Zero || bgraDest == IntPtr.Zero)
        {
            return;
        }

        var coeff = GetCoefficients(colorSpace);
        var yPtr = (byte*)yPlane;
        var uvPtr = (byte*)uvPlane;
        var dstPtr = (byte*)bgraDest;

        Parallel.For(0, height, y =>
        {
            var yRow = yPtr + ((long)y * yStride);
            var uvRow = uvPtr + ((long)(y >> 1) * uvStride);
            var dstRow = (uint*)(dstPtr + ((long)y * bgraStride));

            for (var x = 0; x < width; x++)
            {
                var yVal = yRow[x];
                var uvIndex = (x >> 1) << 1;
                var uVal = uvRow[uvIndex] - 128;
                var vVal = uvRow[uvIndex + 1] - 128;

                var c = (yVal - coeff.YOffset) * coeff.Cy;
                var r = ClampToByte((c + coeff.Crv * vVal + 128) >> 8);
                var g = ClampToByte((c + coeff.Cgu * uVal + coeff.Cgv * vVal + 128) >> 8);
                var b = ClampToByte((c + coeff.Cbu * uVal + 128) >> 8);

                dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
            }
        });
    }

    public static void Yuy2ToBgra32(
        IntPtr srcYuy2,
        int srcStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride) =>
        Yuy2ToBgra32(
            srcYuy2,
            srcStride,
            bgraDest,
            width,
            height,
            bgraStride,
            CaptureYuvColorSpace.Bt601Limited);

    public static void Yuy2ToBgra32(
        IntPtr srcYuy2,
        int srcStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride,
        CaptureYuvColorSpace colorSpace)
    {
        if (width <= 0 || height <= 0 || srcYuy2 == IntPtr.Zero || bgraDest == IntPtr.Zero)
        {
            return;
        }

        var coeff = GetCoefficients(colorSpace);
        var srcPtr = (byte*)srcYuy2;
        var dstPtr = (byte*)bgraDest;

        Parallel.For(0, height, y =>
        {
            var srcRow = srcPtr + ((long)y * srcStride);
            var dstRow = (uint*)(dstPtr + ((long)y * bgraStride));

            var pairCount = width >> 1;
            for (var i = 0; i < pairCount; i++)
            {
                var srcOffset = i << 2;
                var y0 = srcRow[srcOffset];
                var u = srcRow[srcOffset + 1] - 128;
                var y1 = srcRow[srcOffset + 2];
                var v = srcRow[srcOffset + 3] - 128;

                var c0 = (y0 - coeff.YOffset) * coeff.Cy;
                var c1 = (y1 - coeff.YOffset) * coeff.Cy;
                var rAdd = coeff.Crv * v + 128;
                var gAdd = coeff.Cgu * u + coeff.Cgv * v + 128;
                var bAdd = coeff.Cbu * u + 128;

                var r0 = ClampToByte((c0 + rAdd) >> 8);
                var g0 = ClampToByte((c0 + gAdd) >> 8);
                var b0 = ClampToByte((c0 + bAdd) >> 8);
                var dstX = i << 1;
                dstRow[dstX] = (uint)(b0 | (g0 << 8) | (r0 << 16) | (0xFF << 24));

                var r1 = ClampToByte((c1 + rAdd) >> 8);
                var g1 = ClampToByte((c1 + gAdd) >> 8);
                var b1 = ClampToByte((c1 + bAdd) >> 8);
                dstRow[dstX + 1] = (uint)(b1 | (g1 << 8) | (r1 << 16) | (0xFF << 24));
            }

            if ((width & 1) != 0)
            {
                var x = width - 1;
                var srcOffset = (x >> 1) << 2;
                var y0 = srcRow[srcOffset];
                var u = srcRow[srcOffset + 1] - 128;
                var v = srcRow[srcOffset + 3] - 128;
                var c = (y0 - coeff.YOffset) * coeff.Cy;
                var r = ClampToByte((c + coeff.Crv * v + 128) >> 8);
                var g = ClampToByte((c + coeff.Cgu * u + coeff.Cgv * v + 128) >> 8);
                var b = ClampToByte((c + coeff.Cbu * u + 128) >> 8);
                dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
            }
        });
    }

    public static void Rgb32ToBgra32(
        IntPtr srcScanline0,
        int srcPitch,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride,
        bool forceOpaqueAlpha = true)
    {
        if (width <= 0 || height <= 0 || srcScanline0 == IntPtr.Zero || bgraDest == IntPtr.Zero)
        {
            return;
        }

        var srcPtr = (byte*)srcScanline0;
        var dstPtr = (byte*)bgraDest;
        var rowByteCount = checked((long)width * 4);

        Parallel.For(0, height, y =>
        {
            var srcRow = srcPtr + ((long)y * srcPitch);
            var dstRow = dstPtr + ((long)y * bgraStride);

            if (!forceOpaqueAlpha)
            {
                Buffer.MemoryCopy(srcRow, dstRow, rowByteCount, rowByteCount);
                return;
            }

            var srcPixels = (uint*)srcRow;
            var dstPixels = (uint*)dstRow;
            for (var x = 0; x < width; x++)
            {
                dstPixels[x] = srcPixels[x] | 0xFF000000u;
            }
        });
    }
}
