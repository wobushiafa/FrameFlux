using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

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
/// Supports positive and negative (bottom-up) source strides, BT.601/BT.709 limited/full ranges,
/// SIMD vectorization on .NET 8+, and coarse-grained banded parallelism for large frames.
/// </summary>
public static unsafe class CapturePixelConverter
{
    private const int ParallelPixelThreshold = 640 * 480;

    private readonly record struct YuvCoefficients(int YOffset, int Cy, int Crv, int Cgu, int Cgv, int Cbu);

    private static YuvCoefficients GetCoefficients(CaptureYuvColorSpace colorSpace) =>
        colorSpace switch
        {
            CaptureYuvColorSpace.Bt709Limited => new(16, 298, 459, -55, -136, 541),
            CaptureYuvColorSpace.Bt601Full => new(0, 256, 359, -88, -183, 454),
            CaptureYuvColorSpace.Bt709Full => new(0, 256, 403, -48, -120, 475),
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

        if ((long)width * height < ParallelPixelThreshold || height < 16)
        {
            ConvertNv12Band(yPtr, yStride, uvPtr, uvStride, dstPtr, width, bgraStride, 0, height, coeff);
            return;
        }

        var bandCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var pairRows = (height + 1) >> 1;
        var pairsPerBand = (pairRows + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = (band * pairsPerBand) << 1;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, ((band + 1) * pairsPerBand) << 1);
            ConvertNv12Band(yPtr, yStride, uvPtr, uvStride, dstPtr, width, bgraStride, startY, endY, coeff);
        });
    }

    private static void ConvertNv12Band(
        byte* yPtr,
        int yStride,
        byte* uvPtr,
        int uvStride,
        byte* dstPtr,
        int width,
        int bgraStride,
        int startY,
        int endY,
        YuvCoefficients coeff)
    {
#if NET8_0_OR_GREATER
        var useVec256 = Vector256.IsHardwareAccelerated && width >= 8;
        var useVec128 = !useVec256 && Vector128.IsHardwareAccelerated && width >= 4;
        var vYOff256 = Vector256.Create(coeff.YOffset);
        var vCy256 = Vector256.Create(coeff.Cy);
        var vCrv256 = Vector256.Create(coeff.Crv);
        var vCgu256 = Vector256.Create(coeff.Cgu);
        var vCgv256 = Vector256.Create(coeff.Cgv);
        var vCbu256 = Vector256.Create(coeff.Cbu);
        var v128_256 = Vector256.Create(128);
        var vZero256 = Vector256<int>.Zero;
        var v255_256 = Vector256.Create(255);
        var vAlpha256 = Vector256.Create(unchecked((int)0xFF000000u));

        var vYOff128 = Vector128.Create(coeff.YOffset);
        var vCy128 = Vector128.Create(coeff.Cy);
        var vCrv128 = Vector128.Create(coeff.Crv);
        var vCgu128 = Vector128.Create(coeff.Cgu);
        var vCgv128 = Vector128.Create(coeff.Cgv);
        var vCbu128 = Vector128.Create(coeff.Cbu);
        var v128_128 = Vector128.Create(128);
        var vZero128 = Vector128<int>.Zero;
        var v255_128 = Vector128.Create(255);
        var vAlpha128 = Vector128.Create(unchecked((int)0xFF000000u));
#endif
        var evenWidth = width & ~1;

        for (var y = startY; y < endY; y += 2)
        {
            var hasSecondRow = y + 1 < endY;
            var yRow0 = yPtr + ((long)y * yStride);
            var yRow1 = hasSecondRow ? yPtr + ((long)(y + 1) * yStride) : null;
            var uvRow = uvPtr + ((long)(y >> 1) * uvStride);
            var dstRow0 = (uint*)(dstPtr + ((long)y * bgraStride));
            var dstRow1 = hasSecondRow ? (uint*)(dstPtr + ((long)(y + 1) * bgraStride)) : null;

            var x = 0;
#if NET8_0_OR_GREATER
            if (useVec256)
            {
                var limit = width & ~7;
                for (; x < limit; x += 8)
                {
                    var u0 = uvRow[x] - 128;
                    var v0 = uvRow[x + 1] - 128;
                    var u1 = uvRow[x + 2] - 128;
                    var v1 = uvRow[x + 3] - 128;
                    var u2 = uvRow[x + 4] - 128;
                    var v2 = uvRow[x + 5] - 128;
                    var u3 = uvRow[x + 6] - 128;
                    var v3 = uvRow[x + 7] - 128;

                    var uVec = Vector256.Create(u0, u0, u1, u1, u2, u2, u3, u3);
                    var vVec = Vector256.Create(v0, v0, v1, v1, v2, v2, v3, v3);
                    var rAdd = vVec * vCrv256 + v128_256;
                    var gAdd = uVec * vCgu256 + vVec * vCgv256 + v128_256;
                    var bAdd = uVec * vCbu256 + v128_256;

                    StoreBgra8(yRow0 + x, dstRow0 + x, vYOff256, vCy256, rAdd, gAdd, bAdd, vZero256, v255_256, vAlpha256);
                    if (hasSecondRow)
                    {
                        StoreBgra8(yRow1 + x, dstRow1 + x, vYOff256, vCy256, rAdd, gAdd, bAdd, vZero256, v255_256, vAlpha256);
                    }
                }
            }
            else if (useVec128)
            {
                var limit = width & ~3;
                for (; x < limit; x += 4)
                {
                    var u0 = uvRow[x] - 128;
                    var v0 = uvRow[x + 1] - 128;
                    var u1 = uvRow[x + 2] - 128;
                    var v1 = uvRow[x + 3] - 128;

                    var uVec = Vector128.Create(u0, u0, u1, u1);
                    var vVec = Vector128.Create(v0, v0, v1, v1);
                    var rAdd = vVec * vCrv128 + v128_128;
                    var gAdd = uVec * vCgu128 + vVec * vCgv128 + v128_128;
                    var bAdd = uVec * vCbu128 + v128_128;

                    StoreBgra4(yRow0 + x, dstRow0 + x, vYOff128, vCy128, rAdd, gAdd, bAdd, vZero128, v255_128, vAlpha128);
                    if (hasSecondRow)
                    {
                        StoreBgra4(yRow1 + x, dstRow1 + x, vYOff128, vCy128, rAdd, gAdd, bAdd, vZero128, v255_128, vAlpha128);
                    }
                }
            }
#endif

            for (; x < evenWidth; x += 2)
            {
                var uVal = uvRow[x] - 128;
                var vVal = uvRow[x + 1] - 128;
                var rAdd = coeff.Crv * vVal + 128;
                var gAdd = coeff.Cgu * uVal + coeff.Cgv * vVal + 128;
                var bAdd = coeff.Cbu * uVal + 128;

                WritePair(yRow0, dstRow0, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                if (hasSecondRow)
                {
                    WritePair(yRow1, dstRow1, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                }
            }

            if (x < width)
            {
                var uvIndex = (x >> 1) << 1;
                var uVal = uvRow[uvIndex] - 128;
                var vVal = uvRow[uvIndex + 1] - 128;
                var rAdd = coeff.Crv * vVal + 128;
                var gAdd = coeff.Cgu * uVal + coeff.Cgv * vVal + 128;
                var bAdd = coeff.Cbu * uVal + 128;

                WriteSingle(yRow0, dstRow0, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                if (hasSecondRow)
                {
                    WriteSingle(yRow1, dstRow1, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                }
            }
        }
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBgra8(
        byte* ySrc,
        uint* dst,
        Vector256<int> yOffset,
        Vector256<int> cy,
        Vector256<int> rAdd,
        Vector256<int> gAdd,
        Vector256<int> bAdd,
        Vector256<int> vZero,
        Vector256<int> v255,
        Vector256<int> vAlpha)
    {
        var yVec = Vector256.Create(
            (int)ySrc[0], (int)ySrc[1], (int)ySrc[2], (int)ySrc[3],
            (int)ySrc[4], (int)ySrc[5], (int)ySrc[6], (int)ySrc[7]);
        var c = (yVec - yOffset) * cy;
        var r = Vector256.Min(v255, Vector256.Max(vZero, Vector256.ShiftRightArithmetic(c + rAdd, 8)));
        var g = Vector256.Min(v255, Vector256.Max(vZero, Vector256.ShiftRightArithmetic(c + gAdd, 8)));
        var b = Vector256.Min(v255, Vector256.Max(vZero, Vector256.ShiftRightArithmetic(c + bAdd, 8)));
        var packed = b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | vAlpha;
        Vector256.Store(packed.AsUInt32(), dst);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBgra4(
        byte* ySrc,
        uint* dst,
        Vector128<int> yOffset,
        Vector128<int> cy,
        Vector128<int> rAdd,
        Vector128<int> gAdd,
        Vector128<int> bAdd,
        Vector128<int> vZero,
        Vector128<int> v255,
        Vector128<int> vAlpha)
    {
        var yVec = Vector128.Create((int)ySrc[0], (int)ySrc[1], (int)ySrc[2], (int)ySrc[3]);
        var c = (yVec - yOffset) * cy;
        var r = Vector128.Min(v255, Vector128.Max(vZero, Vector128.ShiftRightArithmetic(c + rAdd, 8)));
        var g = Vector128.Min(v255, Vector128.Max(vZero, Vector128.ShiftRightArithmetic(c + gAdd, 8)));
        var b = Vector128.Min(v255, Vector128.Max(vZero, Vector128.ShiftRightArithmetic(c + bAdd, 8)));
        var packed = b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16) | vAlpha;
        Vector128.Store(packed.AsUInt32(), dst);
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePair(
        byte* yRow,
        uint* dstRow,
        int x,
        int yOffset,
        int cy,
        int rAdd,
        int gAdd,
        int bAdd)
    {
        var c0 = (yRow[x] - yOffset) * cy;
        var r0 = ClampToByte((c0 + rAdd) >> 8);
        var g0 = ClampToByte((c0 + gAdd) >> 8);
        var b0 = ClampToByte((c0 + bAdd) >> 8);
        dstRow[x] = (uint)(b0 | (g0 << 8) | (r0 << 16) | (0xFF << 24));

        var c1 = (yRow[x + 1] - yOffset) * cy;
        var r1 = ClampToByte((c1 + rAdd) >> 8);
        var g1 = ClampToByte((c1 + gAdd) >> 8);
        var b1 = ClampToByte((c1 + bAdd) >> 8);
        dstRow[x + 1] = (uint)(b1 | (g1 << 8) | (r1 << 16) | (0xFF << 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSingle(
        byte* yRow,
        uint* dstRow,
        int x,
        int yOffset,
        int cy,
        int rAdd,
        int gAdd,
        int bAdd)
    {
        var c = (yRow[x] - yOffset) * cy;
        var r = ClampToByte((c + rAdd) >> 8);
        var g = ClampToByte((c + gAdd) >> 8);
        var b = ClampToByte((c + bAdd) >> 8);
        dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
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

        if ((long)width * height < ParallelPixelThreshold || height < 16)
        {
            ConvertYuy2Band(srcPtr, srcStride, dstPtr, width, bgraStride, 0, height, coeff);
            return;
        }

        var bandCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var rowsPerBand = (height + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = band * rowsPerBand;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, startY + rowsPerBand);
            ConvertYuy2Band(srcPtr, srcStride, dstPtr, width, bgraStride, startY, endY, coeff);
        });
    }

    private static void ConvertYuy2Band(
        byte* srcPtr,
        int srcStride,
        byte* dstPtr,
        int width,
        int bgraStride,
        int startY,
        int endY,
        YuvCoefficients coeff)
    {
#if NET8_0_OR_GREATER
        var useVec256 = Vector256.IsHardwareAccelerated && width >= 8;
        var vYOff256 = Vector256.Create(coeff.YOffset);
        var vCy256 = Vector256.Create(coeff.Cy);
        var vCrv256 = Vector256.Create(coeff.Crv);
        var vCgu256 = Vector256.Create(coeff.Cgu);
        var vCgv256 = Vector256.Create(coeff.Cgv);
        var vCbu256 = Vector256.Create(coeff.Cbu);
        var v128_256 = Vector256.Create(128);
        var vZero256 = Vector256<int>.Zero;
        var v255_256 = Vector256.Create(255);
        var vAlpha256 = Vector256.Create(unchecked((int)0xFF000000u));
#endif
        var pairCount = width >> 1;

        for (var y = startY; y < endY; y++)
        {
            var srcRow = srcPtr + ((long)y * srcStride);
            var dstRow = (uint*)(dstPtr + ((long)y * bgraStride));

            var i = 0;
#if NET8_0_OR_GREATER
            if (useVec256)
            {
                var vecPairs = pairCount & ~3;
                for (; i < vecPairs; i += 4)
                {
                    var s = srcRow + (i << 2);
                    var yVec = Vector256.Create(
                        (int)s[0], (int)s[2], (int)s[4], (int)s[6],
                        (int)s[8], (int)s[10], (int)s[12], (int)s[14]);
                    var u0 = s[1] - 128; var v0 = s[3] - 128;
                    var u1 = s[5] - 128; var v1 = s[7] - 128;
                    var u2 = s[9] - 128; var v2 = s[11] - 128;
                    var u3 = s[13] - 128; var v3 = s[15] - 128;
                    var uVec = Vector256.Create(u0, u0, u1, u1, u2, u2, u3, u3);
                    var vVec = Vector256.Create(v0, v0, v1, v1, v2, v2, v3, v3);

                    var c = (yVec - vYOff256) * vCy256;
                    var r = Vector256.Min(v255_256, Vector256.Max(vZero256, Vector256.ShiftRightArithmetic(c + vVec * vCrv256 + v128_256, 8)));
                    var g = Vector256.Min(v255_256, Vector256.Max(vZero256, Vector256.ShiftRightArithmetic(c + uVec * vCgu256 + vVec * vCgv256 + v128_256, 8)));
                    var b = Vector256.Min(v255_256, Vector256.Max(vZero256, Vector256.ShiftRightArithmetic(c + uVec * vCbu256 + v128_256, 8)));
                    var packed = b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | vAlpha256;
                    Vector256.Store(packed.AsUInt32(), dstRow + (i << 1));
                }
            }
#endif

            for (; i < pairCount; i++)
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
        }
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

        if (!forceOpaqueAlpha && srcPitch == bgraStride && srcPitch == rowByteCount)
        {
            var total = checked(rowByteCount * height);
            Buffer.MemoryCopy(srcPtr, dstPtr, total, total);
            return;
        }

        // Memory-bandwidth bound; run single-threaded SIMD to avoid ThreadPool scheduling overhead.
#if NET8_0_OR_GREATER
        var alpha256 = Vector256.Create(0xFF000000u);
        var alpha128 = Vector128.Create(0xFF000000u);
#endif
        for (var y = 0; y < height; y++)
        {
            var srcRow = srcPtr + ((long)y * srcPitch);
            var dstRow = dstPtr + ((long)y * bgraStride);

            if (!forceOpaqueAlpha)
            {
                Buffer.MemoryCopy(srcRow, dstRow, rowByteCount, rowByteCount);
                continue;
            }

            var srcPixels = (uint*)srcRow;
            var dstPixels = (uint*)dstRow;
            var x = 0;
#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated)
            {
                var limit = width & ~7;
                for (; x < limit; x += 8)
                {
                    Vector256.Store(Vector256.Load(srcPixels + x) | alpha256, dstPixels + x);
                }
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                var limit = width & ~3;
                for (; x < limit; x += 4)
                {
                    Vector128.Store(Vector128.Load(srcPixels + x) | alpha128, dstPixels + x);
                }
            }
#endif
            for (; x < width; x++)
            {
                dstPixels[x] = srcPixels[x] | 0xFF000000u;
            }
        }
    }

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
        int bgraStride) =>
        Yuv420PToBgra32(
            yPlane,
            yStride,
            uPlane,
            uStride,
            vPlane,
            vStride,
            bgraDest,
            width,
            height,
            bgraStride,
            CaptureYuvColorSpace.Bt601Limited);

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
        CaptureYuvColorSpace colorSpace)
    {
        if (width <= 0 || height <= 0 ||
            yPlane == IntPtr.Zero || uPlane == IntPtr.Zero || vPlane == IntPtr.Zero || bgraDest == IntPtr.Zero)
        {
            return;
        }

        var coeff = GetCoefficients(colorSpace);
        var yPtr = (byte*)yPlane;
        var uPtr = (byte*)uPlane;
        var vPtr = (byte*)vPlane;
        var dstPtr = (byte*)bgraDest;

        if ((long)width * height < ParallelPixelThreshold || height < 16)
        {
            ConvertYuv420PBand(yPtr, yStride, uPtr, uStride, vPtr, vStride, dstPtr, width, bgraStride, 0, height, coeff);
            return;
        }

        var bandCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var pairRows = (height + 1) >> 1;
        var pairsPerBand = (pairRows + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = (band * pairsPerBand) << 1;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, ((band + 1) * pairsPerBand) << 1);
            ConvertYuv420PBand(yPtr, yStride, uPtr, uStride, vPtr, vStride, dstPtr, width, bgraStride, startY, endY, coeff);
        });
    }

    private static void ConvertYuv420PBand(
        byte* yPtr,
        int yStride,
        byte* uPtr,
        int uStride,
        byte* vPtr,
        int vStride,
        byte* dstPtr,
        int width,
        int bgraStride,
        int startY,
        int endY,
        YuvCoefficients coeff)
    {
#if NET8_0_OR_GREATER
        var useVec256 = Vector256.IsHardwareAccelerated && width >= 8;
        var useVec128 = !useVec256 && Vector128.IsHardwareAccelerated && width >= 4;
        var vYOff256 = Vector256.Create(coeff.YOffset);
        var vCy256 = Vector256.Create(coeff.Cy);
        var vCrv256 = Vector256.Create(coeff.Crv);
        var vCgu256 = Vector256.Create(coeff.Cgu);
        var vCgv256 = Vector256.Create(coeff.Cgv);
        var vCbu256 = Vector256.Create(coeff.Cbu);
        var v128_256 = Vector256.Create(128);
        var vZero256 = Vector256<int>.Zero;
        var v255_256 = Vector256.Create(255);
        var vAlpha256 = Vector256.Create(unchecked((int)0xFF000000u));

        var vYOff128 = Vector128.Create(coeff.YOffset);
        var vCy128 = Vector128.Create(coeff.Cy);
        var vCrv128 = Vector128.Create(coeff.Crv);
        var vCgu128 = Vector128.Create(coeff.Cgu);
        var vCgv128 = Vector128.Create(coeff.Cgv);
        var vCbu128 = Vector128.Create(coeff.Cbu);
        var v128_128 = Vector128.Create(128);
        var vZero128 = Vector128<int>.Zero;
        var v255_128 = Vector128.Create(255);
        var vAlpha128 = Vector128.Create(unchecked((int)0xFF000000u));
#endif
        var evenWidth = width & ~1;

        for (var y = startY; y < endY; y += 2)
        {
            var hasSecondRow = y + 1 < endY;
            var yRow0 = yPtr + ((long)y * yStride);
            var yRow1 = hasSecondRow ? yPtr + ((long)(y + 1) * yStride) : null;
            var chromaY = y >> 1;
            var uRow = uPtr + ((long)chromaY * uStride);
            var vRow = vPtr + ((long)chromaY * vStride);
            var dstRow0 = (uint*)(dstPtr + ((long)y * bgraStride));
            var dstRow1 = hasSecondRow ? (uint*)(dstPtr + ((long)(y + 1) * bgraStride)) : null;

            var x = 0;
#if NET8_0_OR_GREATER
            if (useVec256)
            {
                var limit = width & ~7;
                for (; x < limit; x += 8)
                {
                    var cx = x >> 1;
                    var u0 = uRow[cx] - 128;
                    var u1 = uRow[cx + 1] - 128;
                    var u2 = uRow[cx + 2] - 128;
                    var u3 = uRow[cx + 3] - 128;
                    var v0 = vRow[cx] - 128;
                    var v1 = vRow[cx + 1] - 128;
                    var v2 = vRow[cx + 2] - 128;
                    var v3 = vRow[cx + 3] - 128;

                    var uVec = Vector256.Create(u0, u0, u1, u1, u2, u2, u3, u3);
                    var vVec = Vector256.Create(v0, v0, v1, v1, v2, v2, v3, v3);
                    var rAdd = vVec * vCrv256 + v128_256;
                    var gAdd = uVec * vCgu256 + vVec * vCgv256 + v128_256;
                    var bAdd = uVec * vCbu256 + v128_256;

                    StoreBgra8(yRow0 + x, dstRow0 + x, vYOff256, vCy256, rAdd, gAdd, bAdd, vZero256, v255_256, vAlpha256);
                    if (hasSecondRow)
                    {
                        StoreBgra8(yRow1 + x, dstRow1 + x, vYOff256, vCy256, rAdd, gAdd, bAdd, vZero256, v255_256, vAlpha256);
                    }
                }
            }
            else if (useVec128)
            {
                var limit = width & ~3;
                for (; x < limit; x += 4)
                {
                    var cx = x >> 1;
                    var u0 = uRow[cx] - 128;
                    var u1 = uRow[cx + 1] - 128;
                    var v0 = vRow[cx] - 128;
                    var v1 = vRow[cx + 1] - 128;

                    var uVec = Vector128.Create(u0, u0, u1, u1);
                    var vVec = Vector128.Create(v0, v0, v1, v1);
                    var rAdd = vVec * vCrv128 + v128_128;
                    var gAdd = uVec * vCgu128 + vVec * vCgv128 + v128_128;
                    var bAdd = uVec * vCbu128 + v128_128;

                    StoreBgra4(yRow0 + x, dstRow0 + x, vYOff128, vCy128, rAdd, gAdd, bAdd, vZero128, v255_128, vAlpha128);
                    if (hasSecondRow)
                    {
                        StoreBgra4(yRow1 + x, dstRow1 + x, vYOff128, vCy128, rAdd, gAdd, bAdd, vZero128, v255_128, vAlpha128);
                    }
                }
            }
#endif

            for (; x < evenWidth; x += 2)
            {
                var cx = x >> 1;
                var uVal = uRow[cx] - 128;
                var vVal = vRow[cx] - 128;
                var rAdd = coeff.Crv * vVal + 128;
                var gAdd = coeff.Cgu * uVal + coeff.Cgv * vVal + 128;
                var bAdd = coeff.Cbu * uVal + 128;

                WritePair(yRow0, dstRow0, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                if (hasSecondRow)
                {
                    WritePair(yRow1, dstRow1, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                }
            }

            if (x < width)
            {
                var cx = x >> 1;
                var uVal = uRow[cx] - 128;
                var vVal = vRow[cx] - 128;
                var rAdd = coeff.Crv * vVal + 128;
                var gAdd = coeff.Cgu * uVal + coeff.Cgv * vVal + 128;
                var bAdd = coeff.Cbu * uVal + 128;

                WriteSingle(yRow0, dstRow0, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                if (hasSecondRow)
                {
                    WriteSingle(yRow1, dstRow1, x, coeff.YOffset, coeff.Cy, rAdd, gAdd, bAdd);
                }
            }
        }
    }

    public static void Rgb24ToBgra32(
        IntPtr srcRgb24,
        int srcStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride)
    {
        if (width <= 0 || height <= 0 || srcRgb24 == IntPtr.Zero || bgraDest == IntPtr.Zero)
        {
            return;
        }

        var srcPtr = (byte*)srcRgb24;
        var dstPtr = (byte*)bgraDest;
        for (var y = 0; y < height; y++)
        {
            var srcRow = srcPtr + ((long)y * srcStride);
            var dstRow = (uint*)(dstPtr + ((long)y * bgraStride));
            for (var x = 0; x < width; x++)
            {
                var off = x * 3;
                var r = srcRow[off];
                var g = srcRow[off + 1];
                var b = srcRow[off + 2];
                dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
            }
        }
    }

    public static void Bgr24ToBgra32(
        IntPtr srcBgr24,
        int srcStride,
        IntPtr bgraDest,
        int width,
        int height,
        int bgraStride)
    {
        if (width <= 0 || height <= 0 || srcBgr24 == IntPtr.Zero || bgraDest == IntPtr.Zero)
        {
            return;
        }

        var srcPtr = (byte*)srcBgr24;
        var dstPtr = (byte*)bgraDest;
        for (var y = 0; y < height; y++)
        {
            var srcRow = srcPtr + ((long)y * srcStride);
            var dstRow = (uint*)(dstPtr + ((long)y * bgraStride));
            for (var x = 0; x < width; x++)
            {
                var off = x * 3;
                var b = srcRow[off];
                var g = srcRow[off + 1];
                var r = srcRow[off + 2];
                dstRow[x] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
            }
        }
    }
}
