using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace FrameFlux.WebRtc;

/// <summary>
/// High-performance unsafe pixel format converter.
/// Converts YUV420P, NV12, RGB/RGBA frames to BGRA32 for presentation rendering.
/// </summary>
public static unsafe class WebRtcPixelConverter
{
    private const int ParallelPixelThreshold = 640 * 480;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static int GetBandCount(int width, int height)
    {
        if ((long)width * height < ParallelPixelThreshold || height < 16)
        {
            return 1;
        }

        return Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

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
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bandCount = GetBandCount(width, height);
        if (bandCount <= 1)
        {
            ConvertYuv420PBand(
                (byte*)yPlane, yStride,
                (byte*)uPlane, uStride,
                (byte*)vPlane, vStride,
                (byte*)bgraDest, bgraStride,
                width, 0, height, isFullRange);
            return;
        }

        var pairRows = (height + 1) / 2;
        var pairsPerBand = (pairRows + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = band * pairsPerBand * 2;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, startY + pairsPerBand * 2);
            ConvertYuv420PBand(
                (byte*)yPlane, yStride,
                (byte*)uPlane, uStride,
                (byte*)vPlane, vStride,
                (byte*)bgraDest, bgraStride,
                width, startY, endY, isFullRange);
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
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bandCount = GetBandCount(width, height);
        if (bandCount <= 1)
        {
            ConvertNv12Band(
                (byte*)yPlane, yStride,
                (byte*)uvPlane, uvStride,
                (byte*)bgraDest, bgraStride,
                width, 0, height, isFullRange);
            return;
        }

        var pairRows = (height + 1) / 2;
        var pairsPerBand = (pairRows + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = band * pairsPerBand * 2;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, startY + pairsPerBand * 2);
            ConvertNv12Band(
                (byte*)yPlane, yStride,
                (byte*)uvPlane, uvStride,
                (byte*)bgraDest, bgraStride,
                width, startY, endY, isFullRange);
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
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bandCount = GetBandCount(width, height);
        if (bandCount <= 1)
        {
            ConvertRgb24Band((byte*)rgbSource, rgbStride, (byte*)bgraDest, bgraStride, width, 0, height);
            return;
        }

        var rowsPerBand = (height + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = band * rowsPerBand;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, startY + rowsPerBand);
            ConvertRgb24Band((byte*)rgbSource, rgbStride, (byte*)bgraDest, bgraStride, width, startY, endY);
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
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bandCount = GetBandCount(width, height);
        if (bandCount <= 1)
        {
            ConvertRgba32Band((byte*)rgbaSource, rgbaStride, (byte*)bgraDest, bgraStride, width, 0, height);
            return;
        }

        var rowsPerBand = (height + bandCount - 1) / bandCount;
        Parallel.For(0, bandCount, band =>
        {
            var startY = band * rowsPerBand;
            if (startY >= height)
            {
                return;
            }

            var endY = Math.Min(height, startY + rowsPerBand);
            ConvertRgba32Band((byte*)rgbaSource, rgbaStride, (byte*)bgraDest, bgraStride, width, startY, endY);
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
        int bgraStride,
        int width,
        int startY,
        int endY,
        bool isFullRange)
    {
        int yCoeff = isFullRange ? 256 : 298;
        int yOffset = isFullRange ? 0 : 16;
        int rV = isFullRange ? 359 : 409;
        int gU = isFullRange ? -88 : -100;
        int gV = isFullRange ? -183 : -208;
        int bU = isFullRange ? 454 : 516;

        for (var y = startY; y < endY; y += 2)
        {
            var hasSecondRow = y + 1 < endY;
            var yRow0 = yPtr + (nint)y * yStride;
            var yRow1 = hasSecondRow ? yRow0 + yStride : null;
            var uvRowIdx = y >> 1;
            var uRow = uPtr + (nint)uvRowIdx * uStride;
            var vRow = vPtr + (nint)uvRowIdx * vStride;
            var dstRow0 = (uint*)(dstPtr + (nint)y * bgraStride);
            var dstRow1 = hasSecondRow ? (uint*)(dstPtr + (nint)(y + 1) * bgraStride) : null;

            var x = 0;
#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated && width >= 8)
            {
                var vYCoeff = Vector256.Create(yCoeff);
                var vYOffset = Vector256.Create(yOffset);
                var vRV = Vector256.Create(rV);
                var vGU = Vector256.Create(gU);
                var vGV = Vector256.Create(gV);
                var vBU = Vector256.Create(bU);
                var v128 = Vector256.Create(128);
                var vZero = Vector256<int>.Zero;
                var v255 = Vector256.Create(255);
                var vAlpha = Vector256.Create(0xFF000000u);

                var simdLimit = width - 7;
                for (; x < simdLimit; x += 8)
                {
                    var uvIdx = x >> 1;
                    int u0 = uRow[uvIdx] - 128;
                    int u1 = uRow[uvIdx + 1] - 128;
                    int u2 = uRow[uvIdx + 2] - 128;
                    int u3 = uRow[uvIdx + 3] - 128;
                    int v0 = vRow[uvIdx] - 128;
                    int v1 = vRow[uvIdx + 1] - 128;
                    int v2 = vRow[uvIdx + 2] - 128;
                    int v3 = vRow[uvIdx + 3] - 128;

                    var uVec = Vector256.Create(u0, u0, u1, u1, u2, u2, u3, u3);
                    var vVec = Vector256.Create(v0, v0, v1, v1, v2, v2, v3, v3);

                    var rOff = vRV * vVec + v128;
                    var gOff = vGU * uVec + vGV * vVec + v128;
                    var bOff = vBU * uVec + v128;

                    StoreBgra8(yRow0 + x, dstRow0 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    if (hasSecondRow)
                    {
                        StoreBgra8(yRow1 + x, dstRow1 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    }
                }
            }
            else if (Vector128.IsHardwareAccelerated && width >= 4)
            {
                var vYCoeff = Vector128.Create(yCoeff);
                var vYOffset = Vector128.Create(yOffset);
                var vRV = Vector128.Create(rV);
                var vGU = Vector128.Create(gU);
                var vGV = Vector128.Create(gV);
                var vBU = Vector128.Create(bU);
                var v128 = Vector128.Create(128);
                var vZero = Vector128<int>.Zero;
                var v255 = Vector128.Create(255);
                var vAlpha = Vector128.Create(0xFF000000u);

                var simdLimit = width - 3;
                for (; x < simdLimit; x += 4)
                {
                    var uvIdx = x >> 1;
                    int u0 = uRow[uvIdx] - 128;
                    int u1 = uRow[uvIdx + 1] - 128;
                    int v0 = vRow[uvIdx] - 128;
                    int v1 = vRow[uvIdx + 1] - 128;

                    var uVec = Vector128.Create(u0, u0, u1, u1);
                    var vVec = Vector128.Create(v0, v0, v1, v1);

                    var rOff = vRV * vVec + v128;
                    var gOff = vGU * uVec + vGV * vVec + v128;
                    var bOff = vBU * uVec + v128;

                    StoreBgra4(yRow0 + x, dstRow0 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    if (hasSecondRow)
                    {
                        StoreBgra4(yRow1 + x, dstRow1 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    }
                }
            }
#endif

            for (; x + 1 < width; x += 2)
            {
                var uvIdx = x >> 1;
                var uVal = uRow[uvIdx] - 128;
                var vVal = vRow[uvIdx] - 128;
                var rOff = rV * vVal + 128;
                var gOff = gU * uVal + gV * vVal + 128;
                var bOff = bU * uVal + 128;

                dstRow0[x] = PackYuvPixel(yRow0[x], yOffset, yCoeff, rOff, gOff, bOff);
                dstRow0[x + 1] = PackYuvPixel(yRow0[x + 1], yOffset, yCoeff, rOff, gOff, bOff);
                if (hasSecondRow)
                {
                    dstRow1[x] = PackYuvPixel(yRow1[x], yOffset, yCoeff, rOff, gOff, bOff);
                    dstRow1[x + 1] = PackYuvPixel(yRow1[x + 1], yOffset, yCoeff, rOff, gOff, bOff);
                }
            }

            if (x < width)
            {
                var uvIdx = x >> 1;
                var uVal = uRow[uvIdx] - 128;
                var vVal = vRow[uvIdx] - 128;
                var rOff = rV * vVal + 128;
                var gOff = gU * uVal + gV * vVal + 128;
                var bOff = bU * uVal + 128;

                dstRow0[x] = PackYuvPixel(yRow0[x], yOffset, yCoeff, rOff, gOff, bOff);
                if (hasSecondRow)
                {
                    dstRow1[x] = PackYuvPixel(yRow1[x], yOffset, yCoeff, rOff, gOff, bOff);
                }
            }
        }
    }

    private static void ConvertNv12Band(
        byte* yPtr,
        int yStride,
        byte* uvPtr,
        int uvStride,
        byte* dstPtr,
        int bgraStride,
        int width,
        int startY,
        int endY,
        bool isFullRange)
    {
        int yCoeff = isFullRange ? 256 : 298;
        int yOffset = isFullRange ? 0 : 16;
        int rV = isFullRange ? 359 : 409;
        int gU = isFullRange ? -88 : -100;
        int gV = isFullRange ? -183 : -208;
        int bU = isFullRange ? 454 : 516;

        for (var y = startY; y < endY; y += 2)
        {
            var hasSecondRow = y + 1 < endY;
            var yRow0 = yPtr + (nint)y * yStride;
            var yRow1 = hasSecondRow ? yRow0 + yStride : null;
            var uvRow = uvPtr + (nint)(y >> 1) * uvStride;
            var dstRow0 = (uint*)(dstPtr + (nint)y * bgraStride);
            var dstRow1 = hasSecondRow ? (uint*)(dstPtr + (nint)(y + 1) * bgraStride) : null;

            var x = 0;
#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated && width >= 8)
            {
                var vYCoeff = Vector256.Create(yCoeff);
                var vYOffset = Vector256.Create(yOffset);
                var vRV = Vector256.Create(rV);
                var vGU = Vector256.Create(gU);
                var vGV = Vector256.Create(gV);
                var vBU = Vector256.Create(bU);
                var v128 = Vector256.Create(128);
                var vZero = Vector256<int>.Zero;
                var v255 = Vector256.Create(255);
                var vAlpha = Vector256.Create(0xFF000000u);

                var simdLimit = width - 7;
                for (; x < simdLimit; x += 8)
                {
                    int u0 = uvRow[x] - 128;
                    int v0 = uvRow[x + 1] - 128;
                    int u1 = uvRow[x + 2] - 128;
                    int v1 = uvRow[x + 3] - 128;
                    int u2 = uvRow[x + 4] - 128;
                    int v2 = uvRow[x + 5] - 128;
                    int u3 = uvRow[x + 6] - 128;
                    int v3 = uvRow[x + 7] - 128;

                    var uVec = Vector256.Create(u0, u0, u1, u1, u2, u2, u3, u3);
                    var vVec = Vector256.Create(v0, v0, v1, v1, v2, v2, v3, v3);

                    var rOff = vRV * vVec + v128;
                    var gOff = vGU * uVec + vGV * vVec + v128;
                    var bOff = vBU * uVec + v128;

                    StoreBgra8(yRow0 + x, dstRow0 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    if (hasSecondRow)
                    {
                        StoreBgra8(yRow1 + x, dstRow1 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    }
                }
            }
            else if (Vector128.IsHardwareAccelerated && width >= 4)
            {
                var vYCoeff = Vector128.Create(yCoeff);
                var vYOffset = Vector128.Create(yOffset);
                var vRV = Vector128.Create(rV);
                var vGU = Vector128.Create(gU);
                var vGV = Vector128.Create(gV);
                var vBU = Vector128.Create(bU);
                var v128 = Vector128.Create(128);
                var vZero = Vector128<int>.Zero;
                var v255 = Vector128.Create(255);
                var vAlpha = Vector128.Create(0xFF000000u);

                var simdLimit = width - 3;
                for (; x < simdLimit; x += 4)
                {
                    int u0 = uvRow[x] - 128;
                    int v0 = uvRow[x + 1] - 128;
                    int u1 = uvRow[x + 2] - 128;
                    int v1 = uvRow[x + 3] - 128;

                    var uVec = Vector128.Create(u0, u0, u1, u1);
                    var vVec = Vector128.Create(v0, v0, v1, v1);

                    var rOff = vRV * vVec + v128;
                    var gOff = vGU * uVec + vGV * vVec + v128;
                    var bOff = vBU * uVec + v128;

                    StoreBgra4(yRow0 + x, dstRow0 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    if (hasSecondRow)
                    {
                        StoreBgra4(yRow1 + x, dstRow1 + x, vYOffset, vYCoeff, rOff, gOff, bOff, vZero, v255, vAlpha);
                    }
                }
            }
#endif

            for (; x + 1 < width; x += 2)
            {
                var uvIdx = x & ~1;
                var uVal = uvRow[uvIdx] - 128;
                var vVal = uvRow[uvIdx + 1] - 128;
                var rOff = rV * vVal + 128;
                var gOff = gU * uVal + gV * vVal + 128;
                var bOff = bU * uVal + 128;

                dstRow0[x] = PackYuvPixel(yRow0[x], yOffset, yCoeff, rOff, gOff, bOff);
                dstRow0[x + 1] = PackYuvPixel(yRow0[x + 1], yOffset, yCoeff, rOff, gOff, bOff);
                if (hasSecondRow)
                {
                    dstRow1[x] = PackYuvPixel(yRow1[x], yOffset, yCoeff, rOff, gOff, bOff);
                    dstRow1[x + 1] = PackYuvPixel(yRow1[x + 1], yOffset, yCoeff, rOff, gOff, bOff);
                }
            }

            if (x < width)
            {
                var uvIdx = x & ~1;
                var uVal = uvRow[uvIdx] - 128;
                var vVal = uvRow[uvIdx + 1] - 128;
                var rOff = rV * vVal + 128;
                var gOff = gU * uVal + gV * vVal + 128;
                var bOff = bU * uVal + 128;

                dstRow0[x] = PackYuvPixel(yRow0[x], yOffset, yCoeff, rOff, gOff, bOff);
                if (hasSecondRow)
                {
                    dstRow1[x] = PackYuvPixel(yRow1[x], yOffset, yCoeff, rOff, gOff, bOff);
                }
            }
        }
    }

    private static void ConvertRgb24Band(
        byte* srcPtr,
        int rgbStride,
        byte* dstPtr,
        int bgraStride,
        int width,
        int startY,
        int endY)
    {
        for (var y = startY; y < endY; y++)
        {
            var srcRow = srcPtr + (nint)y * rgbStride;
            var dstRow = (uint*)(dstPtr + (nint)y * bgraStride);
            var x = 0;
            var s = srcRow;

            for (; x + 3 < width; x += 4, s += 12)
            {
                dstRow[x] = (uint)(s[2] | (s[1] << 8) | (s[0] << 16) | 0xFF000000u);
                dstRow[x + 1] = (uint)(s[5] | (s[4] << 8) | (s[3] << 16) | 0xFF000000u);
                dstRow[x + 2] = (uint)(s[8] | (s[7] << 8) | (s[6] << 16) | 0xFF000000u);
                dstRow[x + 3] = (uint)(s[11] | (s[10] << 8) | (s[9] << 16) | 0xFF000000u);
            }

            for (; x < width; x++, s += 3)
            {
                dstRow[x] = (uint)(s[2] | (s[1] << 8) | (s[0] << 16) | 0xFF000000u);
            }
        }
    }

    private static void ConvertRgba32Band(
        byte* srcPtr,
        int rgbaStride,
        byte* dstPtr,
        int bgraStride,
        int width,
        int startY,
        int endY)
    {
#if NET8_0_OR_GREATER
        var shuffle256 = Vector256.Create(
            (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
            2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        var shuffle128 = Vector128.Create(
            (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
#endif
        for (var y = startY; y < endY; y++)
        {
            var srcRow = srcPtr + (nint)y * rgbaStride;
            var dstRow = dstPtr + (nint)y * bgraStride;
            var x = 0;

#if NET8_0_OR_GREATER
            if (Avx2.IsSupported && width >= 8)
            {
                var limit = width - 7;
                for (; x < limit; x += 8)
                {
                    var v = Vector256.Load(srcRow + x * 4);
                    Avx2.Shuffle(v, shuffle256).Store(dstRow + x * 4);
                }
            }

            if (Ssse3.IsSupported && width - x >= 4)
            {
                var limit = width - 3;
                for (; x < limit; x += 4)
                {
                    var v = Vector128.Load(srcRow + x * 4);
                    Ssse3.Shuffle(v, shuffle128).Store(dstRow + x * 4);
                }
            }
            else if (AdvSimd.Arm64.IsSupported && width - x >= 4)
            {
                var limit = width - 3;
                for (; x < limit; x += 4)
                {
                    var v = Vector128.Load(srcRow + x * 4);
                    AdvSimd.Arm64.VectorTableLookup(v, shuffle128).Store(dstRow + x * 4);
                }
            }
#endif

            var srcU32 = (uint*)srcRow;
            var dstU32 = (uint*)dstRow;
            for (; x < width; x++)
            {
                var rgba = srcU32[x];
                dstU32[x] = (rgba & 0xFF00FF00u) |
                    ((rgba & 0x000000FFu) << 16) |
                    ((rgba >> 16) & 0x000000FFu);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PackYuvPixel(int yVal, int yOffset, int yCoeff, int rOff, int gOff, int bOff)
    {
        var c = (yVal - yOffset) * yCoeff;
        var r = ClampToByte((c + rOff) >> 8);
        var g = ClampToByte((c + gOff) >> 8);
        var b = ClampToByte((c + bOff) >> 8);
        return (uint)(b | (g << 8) | (r << 16) | 0xFF000000u);
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBgra8(
        byte* ySrc,
        uint* dst,
        Vector256<int> vYOffset,
        Vector256<int> vYCoeff,
        Vector256<int> rOff,
        Vector256<int> gOff,
        Vector256<int> bOff,
        Vector256<int> vZero,
        Vector256<int> v255,
        Vector256<uint> vAlpha)
    {
        var yVec = Vector256.Create(
            (int)ySrc[0], ySrc[1], ySrc[2], ySrc[3],
            ySrc[4], ySrc[5], ySrc[6], ySrc[7]);
        var c = (yVec - vYOffset) * vYCoeff;

        var r = Vector256.Min(Vector256.Max((c + rOff) >> 8, vZero), v255).AsUInt32();
        var g = Vector256.Min(Vector256.Max((c + gOff) >> 8, vZero), v255).AsUInt32();
        var b = Vector256.Min(Vector256.Max((c + bOff) >> 8, vZero), v255).AsUInt32();

        var bgra = b | (g << 8) | (r << 16) | vAlpha;
        bgra.Store(dst);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBgra4(
        byte* ySrc,
        uint* dst,
        Vector128<int> vYOffset,
        Vector128<int> vYCoeff,
        Vector128<int> rOff,
        Vector128<int> gOff,
        Vector128<int> bOff,
        Vector128<int> vZero,
        Vector128<int> v255,
        Vector128<uint> vAlpha)
    {
        var yVec = Vector128.Create((int)ySrc[0], ySrc[1], ySrc[2], ySrc[3]);
        var c = (yVec - vYOffset) * vYCoeff;

        var r = Vector128.Min(Vector128.Max((c + rOff) >> 8, vZero), v255).AsUInt32();
        var g = Vector128.Min(Vector128.Max((c + gOff) >> 8, vZero), v255).AsUInt32();
        var b = Vector128.Min(Vector128.Max((c + bOff) >> 8, vZero), v255).AsUInt32();

        var bgra = b | (g << 8) | (r << 16) | vAlpha;
        bgra.Store(dst);
    }
#endif
}
