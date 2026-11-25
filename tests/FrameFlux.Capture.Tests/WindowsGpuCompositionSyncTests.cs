using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameFlux.Rendering.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed class WindowsGpuCompositionSyncTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void SharedComposition_RapidAlternatingBgraFrames_HasZeroTornTilesAcrossD3D9AndMil()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var verifier = D3D9MilVerificationContext.TryCreate(Width, Height);
        if (verifier is null)
        {
            return;
        }

        const int frameBytes = Width * Height * 4;
        var redBuffer = Marshal.AllocHGlobal(frameBytes);
        var blueBuffer = Marshal.AllocHGlobal(frameBytes);
        try
        {
            FillSolidBgra(redBuffer, Width * Height, 0xFFFF0000u);
            FillSolidBgra(blueBuffer, Width * Height, 0xFF0000FFu);

            using var pool = new WindowsCaptureD3D11TexturePool();
            using var composition = new WindowsD3D11CompositionTexture(
                WindowsD3D11CompositionSynchronization.Shared);

            for (var frameIndex = 0; frameIndex < 60; frameIndex++)
            {
                var isEven = (frameIndex & 1) == 0;
                var sourceBuffer = isEven ? redBuffer : blueBuffer;
                var expectedPixel = isEven ? 0xFFFF0000u : 0xFF0000FFu;

                Assert.True(pool.TryUploadBgraFrame(
                    sourceBuffer,
                    Width,
                    Height,
                    Width * 4,
                    out var lease));
                using (lease)
                {
                    Assert.NotNull(lease);
                    Assert.True(lease!.TryGetD3D11Texture(out var d3d11Frame));
                    Assert.True(composition.TryPresent(
                        Width,
                        Height,
                        Width,
                        Height,
                        d3d11Frame,
                        out var composed));

                    var badPixels = verifier.CopyViaD3D9AndCountBadPixelsOnMil(
                        composed.SharedHandle,
                        pixel => pixel != expectedPixel);

                    Assert.Equal(0, badPixels);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(redBuffer);
            Marshal.FreeHGlobal(blueBuffer);
        }
    }

    [Fact]
    public void SharedComposition_RapidAlternatingNv12Frames_HasZeroTornTilesAcrossD3D9AndMil()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var verifier = D3D9MilVerificationContext.TryCreate(Width, Height);
        if (verifier is null)
        {
            return;
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            var result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out device,
                out context);
            if (result.Failure || device is null || context is null)
            {
                return;
            }
        }
        catch
        {
            device?.Dispose();
            context?.Dispose();
            return;
        }

        using (device)
        using (context)
        using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);

            using var nv12Array = device.CreateTexture2D(new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 2,
                Format = Vortice.DXGI.Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.Decoder
            });

            using var staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 2,
                Format = Vortice.DXGI.Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Write
            });

            UploadSolidNv12Slice(context, staging, nv12Array, slice: 0, luma: 235);
            UploadSolidNv12Slice(context, staging, nv12Array, slice: 1, luma: 16);
            context.Flush();

            using var composition = new WindowsD3D11CompositionTexture(
                WindowsD3D11CompositionSynchronization.Shared);

            for (var frameIndex = 0; frameIndex < 40; frameIndex++)
            {
                var slice = frameIndex & 1;
                var expectBright = slice == 0;
                var d3d11Frame = new MediaD3D11TextureBuffer(nv12Array.NativePointer, slice);

                Assert.True(composition.TryPresent(
                    Width,
                    Height,
                    Width,
                    Height,
                    d3d11Frame,
                    out var composed));

                var badPixels = verifier.CopyViaD3D9AndCountBadPixelsOnMil(
                    composed.SharedHandle,
                    pixel =>
                    {
                        var red = (byte)((pixel >> 16) & 0xFF);
                        return expectBright ? red < 200 : red > 40;
                    });

                Assert.Equal(0, badPixels);
            }
        }
    }

    [Fact]
    public unsafe void Pool_TryUploadNv12Frame_ComposesZeroTornTilesAcrossD3D9AndMil()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var verifier = D3D9MilVerificationContext.TryCreate(Width, Height);
        if (verifier is null)
        {
            return;
        }

        var nv12Bytes = Width * Height * 3 / 2;
        var brightNv12 = Marshal.AllocHGlobal(nv12Bytes);
        var darkNv12 = Marshal.AllocHGlobal(nv12Bytes);
        try
        {
            new Span<byte>((void*)brightNv12, Width * Height).Fill(235);
            new Span<byte>((void*)(brightNv12 + (Width * Height)), Width * (Height / 2)).Fill(128);
            new Span<byte>((void*)darkNv12, Width * Height).Fill(16);
            new Span<byte>((void*)(darkNv12 + (Width * Height)), Width * (Height / 2)).Fill(128);

            using var pool = new WindowsCaptureD3D11TexturePool();
            using var composition = new WindowsD3D11CompositionTexture(
                WindowsD3D11CompositionSynchronization.Shared);

            for (var frameIndex = 0; frameIndex < 30; frameIndex++)
            {
                var expectBright = (frameIndex & 1) == 0;
                var src = expectBright ? brightNv12 : darkNv12;

                // Alternate between contiguous positive-stride and bottom-up negative-stride uploads
                var useNegativeStride = (frameIndex & 2) != 0;
                IntPtr yPtr;
                IntPtr uvPtr;
                int stride;
                if (useNegativeStride)
                {
                    yPtr = src + (Width * (Height - 1));
                    uvPtr = src + (Width * Height) + (Width * ((Height / 2) - 1));
                    stride = -Width;
                }
                else
                {
                    yPtr = src;
                    uvPtr = src + (Width * Height);
                    stride = Width;
                }

                Assert.True(pool.TryUploadNv12Frame(yPtr, stride, uvPtr, stride, Width, Height, out var lease));
                using (lease)
                {
                    Assert.NotNull(lease);
                    Assert.Equal(MediaPixelFormat.Nv12, lease!.PixelFormat);
                    Assert.True(lease.TryGetD3D11Texture(out var d3d11Frame));
                    Assert.True(composition.TryPresent(
                        Width,
                        Height,
                        Width,
                        Height,
                        d3d11Frame,
                        out var composed));

                    var badPixels = verifier.CopyViaD3D9AndCountBadPixelsOnMil(
                        composed.SharedHandle,
                        pixel =>
                        {
                            var red = (byte)((pixel >> 16) & 0xFF);
                            return expectBright ? red < 200 : red > 40;
                        });

                    Assert.Equal(0, badPixels);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(brightNv12);
            Marshal.FreeHGlobal(darkNv12);
        }
    }

    private static unsafe void FillSolidBgra(IntPtr buffer, int pixelCount, uint argbColor)
    {
        var pixels = (uint*)buffer;
        for (var i = 0; i < pixelCount; i++)
        {
            pixels[i] = argbColor;
        }
    }

    private static unsafe void UploadSolidNv12Slice(
        ID3D11DeviceContext context,
        ID3D11Texture2D staging,
        ID3D11Texture2D target,
        uint slice,
        byte luma)
    {
        var mapped = context.Map(staging, slice, MapMode.Write);
        try
        {
            var ptr = (byte*)mapped.DataPointer;
            for (var y = 0; y < Height; y++)
            {
                new Span<byte>(ptr + (y * mapped.RowPitch), Width).Fill(luma);
            }

            for (var y = 0; y < Height / 2; y++)
            {
                new Span<byte>(ptr + ((Height + y) * mapped.RowPitch), Width).Fill(128);
            }
        }
        finally
        {
            context.Unmap(staging, slice);
        }

        context.CopySubresourceRegion(target, slice, 0, 0, 0, staging, slice);
    }

    private sealed class D3D9MilVerificationContext : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly IDirect3D9Ex _d3d9;
        private readonly IDirect3DDevice9Ex _producerDevice;
        private readonly IDirect3DQuery9 _producerQuery;
        private readonly IDirect3DTexture9 _backBufferTexture;
        private readonly IDirect3DSurface9 _backBufferSurface;
        private readonly IDirect3DDevice9Ex _milDevice;
        private readonly IDirect3DTexture9 _milSharedTexture;
        private readonly IDirect3DSurface9 _milSharedSurface;
        private readonly IDirect3DSurface9 _milCopyTarget;
        private readonly IDirect3DSurface9 _milSystemSurface;

        private IntPtr _openedD3D11Handle;
        private IDirect3DTexture9? _d3d11SharedTexture;
        private IDirect3DSurface9? _d3d11SharedSurface;

        private D3D9MilVerificationContext(
            int width,
            int height,
            IDirect3D9Ex d3d9,
            IDirect3DDevice9Ex producerDevice,
            IDirect3DQuery9 producerQuery,
            IDirect3DTexture9 backBufferTexture,
            IDirect3DSurface9 backBufferSurface,
            IDirect3DDevice9Ex milDevice,
            IDirect3DTexture9 milSharedTexture,
            IDirect3DSurface9 milSharedSurface,
            IDirect3DSurface9 milCopyTarget,
            IDirect3DSurface9 milSystemSurface)
        {
            _width = width;
            _height = height;
            _d3d9 = d3d9;
            _producerDevice = producerDevice;
            _producerQuery = producerQuery;
            _backBufferTexture = backBufferTexture;
            _backBufferSurface = backBufferSurface;
            _milDevice = milDevice;
            _milSharedTexture = milSharedTexture;
            _milSharedSurface = milSharedSurface;
            _milCopyTarget = milCopyTarget;
            _milSystemSurface = milSystemSurface;
        }

        public static D3D9MilVerificationContext? TryCreate(int width, int height)
        {
            IDirect3D9Ex? d3d9 = null;
            IDirect3DDevice9Ex? producer = null;
            IDirect3DQuery9? producerQuery = null;
            IDirect3DTexture9? backTexture = null;
            IDirect3DSurface9? backSurface = null;
            IDirect3DDevice9Ex? mil = null;
            IDirect3DTexture9? milSharedTex = null;
            IDirect3DSurface9? milSharedSurf = null;
            IDirect3DSurface9? milCopyTarget = null;
            IDirect3DSurface9? milSystemSurface = null;

            try
            {
                d3d9 = D3D9.Direct3DCreate9Ex();
                if (d3d9.AdapterCount == 0)
                {
                    d3d9.Dispose();
                    return null;
                }

                var hwnd = GetDesktopWindow();
                var parameters = new Vortice.Direct3D9.PresentParameters
                {
                    Windowed = true,
                    SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                    DeviceWindowHandle = hwnd,
                    PresentationInterval = PresentInterval.Default
                };
                var flags = CreateFlags.HardwareVertexProcessing |
                            CreateFlags.Multithreaded |
                            CreateFlags.FpuPreserve;

                producer = d3d9.CreateDeviceEx(0, DeviceType.Hardware, hwnd, flags, parameters);
                producerQuery = producer.CreateQuery(Vortice.Direct3D9.QueryType.Event);

                var backHandle = IntPtr.Zero;
                backTexture = producer.CreateTexture(
                    (uint)width,
                    (uint)height,
                    1,
                    Vortice.Direct3D9.Usage.RenderTarget,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    Pool.Default,
                    ref backHandle);
                backSurface = backTexture.GetSurfaceLevel(0);

                mil = d3d9.CreateDeviceEx(0, DeviceType.Hardware, hwnd, flags, parameters);
                milSharedTex = mil.CreateTexture(
                    (uint)width,
                    (uint)height,
                    1,
                    Vortice.Direct3D9.Usage.RenderTarget,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    Pool.Default,
                    ref backHandle);
                milSharedSurf = milSharedTex.GetSurfaceLevel(0);
                milCopyTarget = mil.CreateRenderTarget(
                    (uint)width,
                    (uint)height,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    MultisampleType.None,
                    0,
                    lockable: false);
                milSystemSurface = mil.CreateOffscreenPlainSurface(
                    (uint)width,
                    (uint)height,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    Pool.SystemMemory);

                return new D3D9MilVerificationContext(
                    width,
                    height,
                    d3d9,
                    producer,
                    producerQuery,
                    backTexture,
                    backSurface,
                    mil,
                    milSharedTex,
                    milSharedSurf,
                    milCopyTarget,
                    milSystemSurface);
            }
            catch
            {
                milSystemSurface?.Dispose();
                milCopyTarget?.Dispose();
                milSharedSurf?.Dispose();
                milSharedTex?.Dispose();
                mil?.Dispose();
                backSurface?.Dispose();
                backTexture?.Dispose();
                producerQuery?.Dispose();
                producer?.Dispose();
                d3d9?.Dispose();
                return null;
            }
        }

        public unsafe int CopyViaD3D9AndCountBadPixelsOnMil(
            IntPtr d3d11SharedHandle,
            Func<uint, bool> isBadPixel)
        {
            if (_openedD3D11Handle != d3d11SharedHandle || _d3d11SharedSurface is null)
            {
                _d3d11SharedSurface?.Dispose();
                _d3d11SharedTexture?.Dispose();
                var handle = d3d11SharedHandle;
                _d3d11SharedTexture = _producerDevice.CreateTexture(
                    (uint)_width,
                    (uint)_height,
                    1,
                    Vortice.Direct3D9.Usage.RenderTarget,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    Pool.Default,
                    ref handle);
                _d3d11SharedSurface = _d3d11SharedTexture.GetSurfaceLevel(0);
                _openedD3D11Handle = d3d11SharedHandle;
            }

            var rect = new Vortice.Direct3D9.Rect(0, 0, _width, _height);
            _producerDevice.StretchRect(
                _d3d11SharedSurface,
                rect,
                _backBufferSurface,
                rect,
                TextureFilter.None);

            WaitForD3D9Event(_producerQuery);

            _milDevice.StretchRect(_milSharedSurface, rect, _milCopyTarget, rect, TextureFilter.None);
            _milDevice.GetRenderTargetData(_milCopyTarget, _milSystemSurface);

            var locked = _milSystemSurface.LockRect(LockFlags.ReadOnly);
            var bad = 0;
            try
            {
                var basePtr = (byte*)locked.DataPointer;
                for (var y = 0; y < _height; y++)
                {
                    var row = (uint*)(basePtr + (y * locked.Pitch));
                    for (var x = 0; x < _width; x++)
                    {
                        if (isBadPixel(row[x]))
                        {
                            bad++;
                        }
                    }
                }
            }
            finally
            {
                _milSystemSurface.UnlockRect();
            }

            return bad;
        }

        private static void WaitForD3D9Event(IDirect3DQuery9 query)
        {
            query.Issue(Issue.End);
            var start = Stopwatch.GetTimestamp();
            var timeoutTicks = Stopwatch.Frequency / 5; // 200 ms guard for test stability
            var iteration = 0;
            while (true)
            {
                if (query.GetData(out bool completed, true) && completed)
                {
                    return;
                }

                if (Stopwatch.GetTimestamp() - start >= timeoutTicks)
                {
                    throw new TimeoutException("Timed out waiting for D3D9 event query.");
                }

                if (iteration++ < 64)
                {
                    Thread.SpinWait(32);
                }
                else
                {
                    Thread.Yield();
                }
            }
        }

        public void Dispose()
        {
            _d3d11SharedSurface?.Dispose();
            _d3d11SharedTexture?.Dispose();
            _milSystemSurface.Dispose();
            _milCopyTarget.Dispose();
            _milSharedSurface.Dispose();
            _milSharedTexture.Dispose();
            _milDevice.Dispose();
            _backBufferSurface.Dispose();
            _backBufferTexture.Dispose();
            _producerQuery.Dispose();
            _producerDevice.Dispose();
            _d3d9.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
    }
}
