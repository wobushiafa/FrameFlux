using System.Runtime.InteropServices;
using FrameFlux.Capture;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed unsafe class CapturePixelConverterTests
{
    [Fact]
    public void Nv12ToBgra32_OutputsOpaqueAlphaAndValidLuminance()
    {
        const int width = 4;
        const int height = 4;
        var yPlane = Marshal.AllocHGlobal(width * height);
        var uvPlane = Marshal.AllocHGlobal(width * (height / 2));
        var bgra = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            new Span<byte>((void*)yPlane, width * height).Fill(235);
            new Span<byte>((void*)uvPlane, width * (height / 2)).Fill(128);

            CapturePixelConverter.Nv12ToBgra32(
                yPlane,
                width,
                uvPlane,
                width,
                bgra,
                width,
                height,
                width * 4);

            var pixels = new ReadOnlySpan<byte>((void*)bgra, width * height * 4);
            Assert.Equal(255, pixels[3]);
            Assert.True(pixels[0] >= 250);
            Assert.True(pixels[1] >= 250);
            Assert.True(pixels[2] >= 250);
        }
        finally
        {
            Marshal.FreeHGlobal(yPlane);
            Marshal.FreeHGlobal(uvPlane);
            Marshal.FreeHGlobal(bgra);
        }
    }

    [Fact]
    public void Yuy2ToBgra32_ConvertsEvenAndOddWidthsWithOpaqueAlpha()
    {
        const int width = 3;
        const int height = 2;
        const int yuy2Stride = 8;
        const int bgraStride = width * 4;

        var src = Marshal.AllocHGlobal(yuy2Stride * height);
        var dst = Marshal.AllocHGlobal(bgraStride * height);
        try
        {
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)src + (y * yuy2Stride);
                // Pixel 0 (dark), Pixel 1 (bright), UV neutral
                row[0] = 16;
                row[1] = 128;
                row[2] = 235;
                row[3] = 128;
                // Pixel 2 (mid)
                row[4] = 126;
                row[5] = 128;
                row[6] = 126;
                row[7] = 128;
            }

            CapturePixelConverter.Yuy2ToBgra32(src, yuy2Stride, dst, width, height, bgraStride);

            var outRow0 = new ReadOnlySpan<byte>((void*)dst, bgraStride);
            Assert.Equal(255, outRow0[3]);
            Assert.True(outRow0[0] <= 5);
            Assert.Equal(255, outRow0[7]);
            Assert.True(outRow0[4] >= 250);
            Assert.Equal(255, outRow0[11]);
            Assert.InRange(outRow0[8], 115, 145);
        }
        finally
        {
            Marshal.FreeHGlobal(src);
            Marshal.FreeHGlobal(dst);
        }
    }

    [Fact]
    public void Rgb32ToBgra32_HandlesNegativePitchAndForcesOpaqueAlpha()
    {
        const int width = 2;
        const int height = 2;
        const int stride = width * 4;
        var src = Marshal.AllocHGlobal(stride * height);
        var dst = Marshal.AllocHGlobal(stride * height);
        try
        {
            // Memory row 0 = bottom image row (blue, alpha=0)
            var memRow0 = new Span<byte>((void*)src, stride);
            memRow0[0] = 255; memRow0[1] = 0; memRow0[2] = 0; memRow0[3] = 0;
            memRow0[4] = 255; memRow0[5] = 0; memRow0[6] = 0; memRow0[7] = 0;

            // Memory row 1 = top image row (red, alpha=0)
            var memRow1 = new Span<byte>((void*)(src + stride), stride);
            memRow1[0] = 0; memRow1[1] = 0; memRow1[2] = 255; memRow1[3] = 0;
            memRow1[4] = 0; memRow1[5] = 0; memRow1[6] = 255; memRow1[7] = 0;

            // scanline0 points to top image row (memRow1) with negative pitch
            CapturePixelConverter.Rgb32ToBgra32(
                src + stride,
                -stride,
                dst,
                width,
                height,
                stride,
                forceOpaqueAlpha: true);

            var topOut = new ReadOnlySpan<byte>((void*)dst, stride);
            Assert.Equal(0, topOut[0]);
            Assert.Equal(0, topOut[1]);
            Assert.Equal(255, topOut[2]);
            Assert.Equal(255, topOut[3]);

            var bottomOut = new ReadOnlySpan<byte>((void*)(dst + stride), stride);
            Assert.Equal(255, bottomOut[0]);
            Assert.Equal(0, bottomOut[1]);
            Assert.Equal(0, bottomOut[2]);
            Assert.Equal(255, bottomOut[3]);
        }
        finally
        {
            Marshal.FreeHGlobal(src);
            Marshal.FreeHGlobal(dst);
        }
    }

    [Fact]
    public void Nv12ToBgra32_HandlesNegativePitchBottomUpLayout()
    {
        const int width = 4;
        const int height = 4;
        var yPlane = Marshal.AllocHGlobal(width * height);
        var uvPlane = Marshal.AllocHGlobal(width * (height / 2));
        var bgra = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            // Memory row 0..1 = bottom half (dark, Y=16), row 2..3 = top half (bright, Y=235)
            new Span<byte>((void*)yPlane, width * 2).Fill(16);
            new Span<byte>((void*)(yPlane + (width * 2)), width * 2).Fill(235);
            new Span<byte>((void*)uvPlane, width * (height / 2)).Fill(128);

            // Bottom-up pointers: scanline0 points to last row in memory, stride is negative
            var yTop = yPlane + (width * (height - 1));
            var uvTop = uvPlane + (width * ((height / 2) - 1));

            CapturePixelConverter.Nv12ToBgra32(
                yTop,
                -width,
                uvTop,
                -width,
                bgra,
                width,
                height,
                width * 4);

            var topRow = new ReadOnlySpan<byte>((void*)bgra, width * 4);
            var bottomRow = new ReadOnlySpan<byte>((void*)(bgra + (width * 4 * (height - 1))), width * 4);
            Assert.True(topRow[0] >= 250);
            Assert.Equal(255, topRow[3]);
            Assert.True(bottomRow[0] <= 5);
            Assert.Equal(255, bottomRow[3]);
        }
        finally
        {
            Marshal.FreeHGlobal(yPlane);
            Marshal.FreeHGlobal(uvPlane);
            Marshal.FreeHGlobal(bgra);
        }
    }

    [Fact]
    public void Yuy2ToBgra32_HandlesNegativePitchBottomUpLayout()
    {
        const int width = 2;
        const int height = 2;
        const int stride = width * 2;
        var src = Marshal.AllocHGlobal(stride * height);
        var dst = Marshal.AllocHGlobal(width * 4 * height);
        try
        {
            // Memory row 0 = bottom image row (dark Y=16)
            var memRow0 = (byte*)src;
            memRow0[0] = 16; memRow0[1] = 128; memRow0[2] = 16; memRow0[3] = 128;
            // Memory row 1 = top image row (bright Y=235)
            var memRow1 = (byte*)(src + stride);
            memRow1[0] = 235; memRow1[1] = 128; memRow1[2] = 235; memRow1[3] = 128;

            CapturePixelConverter.Yuy2ToBgra32(src + stride, -stride, dst, width, height, width * 4);

            var topRow = new ReadOnlySpan<byte>((void*)dst, width * 4);
            var bottomRow = new ReadOnlySpan<byte>((void*)(dst + (width * 4)), width * 4);
            Assert.True(topRow[0] >= 250);
            Assert.True(bottomRow[0] <= 5);
        }
        finally
        {
            Marshal.FreeHGlobal(src);
            Marshal.FreeHGlobal(dst);
        }
    }

    [Fact]
    public void Nv12ToBgra32_SupportsBt709AndFullRangeColorSpaces()
    {
        const int width = 2;
        const int height = 2;
        var yPlane = Marshal.AllocHGlobal(width * height);
        var uvPlane = Marshal.AllocHGlobal(width * (height / 2));
        var bgra601 = Marshal.AllocHGlobal(width * height * 4);
        var bgra709 = Marshal.AllocHGlobal(width * height * 4);
        var bgraFull = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            // Saturated red-ish chroma (Y=128, U=90, V=200) to distinguish BT.601 vs BT.709 matrices
            new Span<byte>((void*)yPlane, width * height).Fill(128);
            var uv = new Span<byte>((void*)uvPlane, width * (height / 2));
            uv[0] = 90;
            uv[1] = 200;

            CapturePixelConverter.Nv12ToBgra32(yPlane, width, uvPlane, width, bgra601, width, height, width * 4, CaptureYuvColorSpace.Bt601Limited);
            CapturePixelConverter.Nv12ToBgra32(yPlane, width, uvPlane, width, bgra709, width, height, width * 4, CaptureYuvColorSpace.Bt709Limited);

            var p601 = new ReadOnlySpan<byte>((void*)bgra601, 4);
            var p709 = new ReadOnlySpan<byte>((void*)bgra709, 4);
            // BT.709 has a stronger V->R coefficient and weaker V->G coefficient than BT.601
            Assert.True(p709[2] > p601[2]);
            Assert.True(p709[1] > p601[1]);

            // Full-range Y=255, U=128, V=128 -> exact white (255,255,255,255)
            new Span<byte>((void*)yPlane, width * height).Fill(255);
            uv.Fill(128);
            CapturePixelConverter.Nv12ToBgra32(yPlane, width, uvPlane, width, bgraFull, width, height, width * 4, CaptureYuvColorSpace.Bt601Full);
            var pFull = new ReadOnlySpan<byte>((void*)bgraFull, 4);
            Assert.Equal(255, pFull[0]);
            Assert.Equal(255, pFull[1]);
            Assert.Equal(255, pFull[2]);
            Assert.Equal(255, pFull[3]);
        }
        finally
        {
            Marshal.FreeHGlobal(yPlane);
            Marshal.FreeHGlobal(uvPlane);
            Marshal.FreeHGlobal(bgra601);
            Marshal.FreeHGlobal(bgra709);
            Marshal.FreeHGlobal(bgraFull);
        }
    }

    [Fact]
    public void Yuv420PToBgra32_OutputsOpaqueAlphaAndValidLuminance()
    {
        const int width = 10;
        const int height = 4;
        const int uvWidth = (width + 1) / 2;
        const int uvHeight = (height + 1) / 2;
        var yPlane = Marshal.AllocHGlobal(width * height);
        var uPlane = Marshal.AllocHGlobal(uvWidth * uvHeight);
        var vPlane = Marshal.AllocHGlobal(uvWidth * uvHeight);
        var bgra = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            new Span<byte>((void*)yPlane, width * height).Fill(235);
            new Span<byte>((void*)uPlane, uvWidth * uvHeight).Fill(128);
            new Span<byte>((void*)vPlane, uvWidth * uvHeight).Fill(128);

            CapturePixelConverter.Yuv420PToBgra32(
                yPlane,
                width,
                uPlane,
                uvWidth,
                vPlane,
                uvWidth,
                bgra,
                width,
                height,
                width * 4,
                CaptureYuvColorSpace.Bt601Limited);

            var pixels = new ReadOnlySpan<byte>((void*)bgra, width * height * 4);
            for (var i = 0; i < width * height; i++)
            {
                var off = i * 4;
                Assert.True(pixels[off] >= 250);
                Assert.True(pixels[off + 1] >= 250);
                Assert.True(pixels[off + 2] >= 250);
                Assert.Equal(255, pixels[off + 3]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(yPlane);
            Marshal.FreeHGlobal(uPlane);
            Marshal.FreeHGlobal(vPlane);
            Marshal.FreeHGlobal(bgra);
        }
    }

    [Fact]
    public void Rgb24AndBgr24ToBgra32_MapChannelsAndOpaqueAlpha()
    {
        const int width = 2;
        const int height = 1;
        var src = Marshal.AllocHGlobal(width * 3);
        var dstRgb = Marshal.AllocHGlobal(width * 4);
        var dstBgr = Marshal.AllocHGlobal(width * 4);
        try
        {
            var bytes = new Span<byte>((void*)src, width * 3);
            // Pixel 0: (10, 20, 30), Pixel 1: (40, 50, 60)
            bytes[0] = 10; bytes[1] = 20; bytes[2] = 30;
            bytes[3] = 40; bytes[4] = 50; bytes[5] = 60;

            CapturePixelConverter.Rgb24ToBgra32(src, width * 3, dstRgb, width, height, width * 4);
            CapturePixelConverter.Bgr24ToBgra32(src, width * 3, dstBgr, width, height, width * 4);

            var outRgb = new ReadOnlySpan<byte>((void*)dstRgb, width * 4);
            Assert.Equal(30, outRgb[0]); // B
            Assert.Equal(20, outRgb[1]); // G
            Assert.Equal(10, outRgb[2]); // R
            Assert.Equal(255, outRgb[3]); // A

            var outBgr = new ReadOnlySpan<byte>((void*)dstBgr, width * 4);
            Assert.Equal(10, outBgr[0]); // B
            Assert.Equal(20, outBgr[1]); // G
            Assert.Equal(30, outBgr[2]); // R
            Assert.Equal(255, outBgr[3]); // A
        }
        finally
        {
            Marshal.FreeHGlobal(src);
            Marshal.FreeHGlobal(dstRgb);
            Marshal.FreeHGlobal(dstBgr);
        }
    }
}
