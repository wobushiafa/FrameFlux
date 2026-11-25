#if NET8_0_OR_GREATER && !ANDROID
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FrameFlux.Capture;

/// <summary>
/// Manages 16-slice D3D11 BGRA32 and NV12 texture arrays for GPU composition / swapchain presentation.
/// Supports GPU-to-GPU copies from Media Foundation DXGI samples, direct NV12 uploads (2.67x less
/// PCIe bandwidth than BGRA), and BGRA fallback uploads.
/// </summary>
internal sealed unsafe class WindowsCaptureD3D11TexturePool : IDisposable
{
    private const int SliceCount = 16;

    private readonly object _sync = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Multithread? _multithread;
    private bool _supportsNv12Texture;

    private ID3D11Texture2D? _bgraTextureArray;
    private IntPtr _currentBgraTexturePtr;
    private readonly bool[] _bgraSliceInUse = new bool[SliceCount];
    private int _bgraWidth;
    private int _bgraHeight;
    private int _nextBgraSlice;

    private ID3D11Texture2D? _nv12TextureArray;
    private ID3D11Texture2D? _nv12StagingTexture;
    private IntPtr _currentNv12TexturePtr;
    private readonly bool[] _nv12SliceInUse = new bool[SliceCount];
    private int _nv12Width;
    private int _nv12Height;
    private int _nextNv12Slice;

    private bool _disposed;

    /// <summary>
    /// Ensures the D3D11 device exists and returns an AddRef'd native ID3D11Device pointer
    /// suitable for binding to an IMFDXGIDeviceManager. Caller must release the returned pointer.
    /// </summary>
    internal bool TryGetDeviceNativePointer(out IntPtr devicePtr)
    {
        devicePtr = IntPtr.Zero;
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                EnsureDevice();
                if (_device is null || _device.NativePointer == IntPtr.Zero)
                {
                    return false;
                }

                Marshal.AddRef(_device.NativePointer);
                devicePtr = _device.NativePointer;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Copies a GPU-resident Media Foundation DXGI texture into a pooled slice on the same D3D11 device.
    /// Avoids CPU readback and decouples rendering lifetime from the camera driver's sample queue.
    /// </summary>
    internal bool TryCopyGpuTexture(
        IntPtr sourceTexturePtr,
        int sourceSubresourceIndex,
        int width,
        int height,
        out CaptureMediaFrameLease? lease)
    {
        lease = null;
        if (sourceTexturePtr == IntPtr.Zero || width <= 0 || height <= 0 || sourceSubresourceIndex < 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed || _device is null || _context is null)
            {
                return false;
            }

            Marshal.AddRef(sourceTexturePtr);
            using var sourceTexture = new ID3D11Texture2D(sourceTexturePtr);
            using var sourceDevice = sourceTexture.Device;
            if (sourceDevice.NativePointer != _device.NativePointer)
            {
                return false;
            }

            var desc = sourceTexture.Description;
            var isNv12 = desc.Format == Format.NV12;
            var isBgra = desc.Format == Format.B8G8R8A8_UNorm;
            if (!isNv12 && !isBgra)
            {
                return false;
            }

            if (isNv12 && ((width & 1) != 0 || (height & 1) != 0 || !_supportsNv12Texture))
            {
                return false;
            }

            if (desc.Width < (uint)width || desc.Height < (uint)height)
            {
                return false;
            }

            try
            {
                if (isNv12)
                {
                    EnsureNv12Textures(width, height);
                }
                else
                {
                    EnsureBgraTextures(width, height);
                }
            }
            catch
            {
                return false;
            }

            var sliceArray = isNv12 ? _nv12SliceInUse : _bgraSliceInUse;
            var sliceIndex = FindAvailableSlice(sliceArray, ref (isNv12 ? ref _nextNv12Slice : ref _nextBgraSlice));
            var destTexture = isNv12 ? _nv12TextureArray : _bgraTextureArray;
            var destPtr = isNv12 ? _currentNv12TexturePtr : _currentBgraTexturePtr;
            if (sliceIndex < 0 || destTexture is null || destPtr == IntPtr.Zero)
            {
                return false;
            }

            sliceArray[sliceIndex] = true;
            try
            {
                var destSubresource = checked((uint)sliceIndex);
                var srcSubresource = checked((uint)sourceSubresourceIndex);
                var needsCrop = desc.Width != (uint)width || desc.Height != (uint)height;

                _multithread?.Enter();
                try
                {
                    if (needsCrop)
                    {
                        _context.CopySubresourceRegion(
                            destTexture,
                            destSubresource,
                            0,
                            0,
                            0,
                            sourceTexture,
                            srcSubresource,
                            new Box(0, 0, 0, width, height, 1));
                    }
                    else
                    {
                        _context.CopySubresourceRegion(
                            destTexture,
                            destSubresource,
                            0,
                            0,
                            0,
                            sourceTexture,
                            srcSubresource);
                    }

                    _context.Flush();
                }
                finally
                {
                    _multithread?.Leave();
                }

                Marshal.AddRef(destPtr);
                lease = CaptureMediaFrameLease.CreateD3D11(
                    width,
                    height,
                    destPtr,
                    sliceIndex,
                    () => ReleaseSlice(isNv12, sliceIndex, destPtr),
                    isNv12 ? MediaPixelFormat.Nv12 : MediaPixelFormat.Bgra32);
                return true;
            }
            catch
            {
                sliceArray[sliceIndex] = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Uploads an NV12 frame directly to a D3D11 Format.NV12 texture slice so GPU VideoProcessorBlt
    /// performs YUV-to-BGRA conversion without CPU color conversion or 4-Bpp PCIe uploads.
    /// Supports both positive and negative (bottom-up) strides.
    /// </summary>
    internal bool TryUploadNv12Frame(
        IntPtr yPlane,
        int yStride,
        IntPtr uvPlane,
        int uvStride,
        int width,
        int height,
        out CaptureMediaFrameLease? lease)
    {
        lease = null;
        if (yPlane == IntPtr.Zero ||
            uvPlane == IntPtr.Zero ||
            width <= 0 ||
            height <= 0 ||
            (width & 1) != 0 ||
            (height & 1) != 0 ||
            yStride == 0 ||
            uvStride == 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                EnsureDevice();
                if (!_supportsNv12Texture)
                {
                    return false;
                }

                EnsureNv12Textures(width, height);
            }
            catch
            {
                return false;
            }

            var sliceIndex = FindAvailableSlice(_nv12SliceInUse, ref _nextNv12Slice);
            if (sliceIndex < 0 ||
                _nv12TextureArray is null ||
                _nv12StagingTexture is null ||
                _context is null)
            {
                return false;
            }

            _nv12SliceInUse[sliceIndex] = true;
            var texture = _nv12TextureArray;
            var staging = _nv12StagingTexture;
            var texturePtr = _currentNv12TexturePtr;
            try
            {
                var subresource = checked((uint)sliceIndex);
                var uvHeight = height >> 1;
                _multithread?.Enter();
                try
                {
                    var mapped = _context.Map(staging, 0, MapMode.Write);
                    try
                    {
                        var dst = (byte*)mapped.DataPointer;
                        var dstPitch = (long)mapped.RowPitch;
                        var ySrc = (byte*)yPlane;
                        for (var y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(ySrc + ((long)y * yStride), dst + (y * dstPitch), width, width);
                        }

                        var uvDst = dst + (height * dstPitch);
                        var uvSrc = (byte*)uvPlane;
                        for (var y = 0; y < uvHeight; y++)
                        {
                            Buffer.MemoryCopy(uvSrc + ((long)y * uvStride), uvDst + (y * dstPitch), width, width);
                        }
                    }
                    finally
                    {
                        _context.Unmap(staging, 0);
                    }

                    _context.CopySubresourceRegion(
                        texture,
                        subresource,
                        0,
                        0,
                        0,
                        staging,
                        0);
                    _context.Flush();
                }
                finally
                {
                    _multithread?.Leave();
                }

                Marshal.AddRef(texturePtr);
                lease = CaptureMediaFrameLease.CreateD3D11(
                    width,
                    height,
                    texturePtr,
                    sliceIndex,
                    () => ReleaseSlice(isNv12: true, sliceIndex, texturePtr),
                    MediaPixelFormat.Nv12);
                return true;
            }
            catch
            {
                _nv12SliceInUse[sliceIndex] = false;
                return false;
            }
        }
    }

    internal bool TryUploadBgraFrame(
        IntPtr bgraData,
        int width,
        int height,
        int bgraStride,
        out CaptureMediaFrameLease? lease)
    {
        lease = null;
        if (bgraData == IntPtr.Zero || width <= 0 || height <= 0 || bgraStride <= 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                EnsureDevice();
                EnsureBgraTextures(width, height);
            }
            catch
            {
                return false;
            }

            var sliceIndex = FindAvailableSlice(_bgraSliceInUse, ref _nextBgraSlice);
            if (sliceIndex < 0 || _bgraTextureArray is null || _context is null)
            {
                return false;
            }

            _bgraSliceInUse[sliceIndex] = true;
            var texture = _bgraTextureArray;
            var texturePtr = _currentBgraTexturePtr;
            try
            {
                var subresource = checked((uint)sliceIndex);
                _multithread?.Enter();
                try
                {
                    _context.UpdateSubresource(
                        texture,
                        subresource,
                        null,
                        bgraData,
                        checked((uint)bgraStride),
                        0);
                    _context.Flush();
                }
                finally
                {
                    _multithread?.Leave();
                }

                // AddRef so the texture COM pointer remains valid even if the pool resizes or disposes while in-flight.
                Marshal.AddRef(texturePtr);
                lease = CaptureMediaFrameLease.CreateD3D11(
                    width,
                    height,
                    texturePtr,
                    sliceIndex,
                    () => ReleaseSlice(isNv12: false, sliceIndex, texturePtr),
                    MediaPixelFormat.Bgra32);
                return true;
            }
            catch
            {
                _bgraSliceInUse[sliceIndex] = false;
                return false;
            }
        }
    }

    private void ReleaseSlice(bool isNv12, int sliceIndex, IntPtr texturePtr)
    {
        Marshal.Release(texturePtr);
        lock (_sync)
        {
            if (isNv12)
            {
                if (_currentNv12TexturePtr == texturePtr &&
                    (uint)sliceIndex < (uint)_nv12SliceInUse.Length)
                {
                    _nv12SliceInUse[sliceIndex] = false;
                }
            }
            else if (_currentBgraTexturePtr == texturePtr &&
                     (uint)sliceIndex < (uint)_bgraSliceInUse.Length)
            {
                _bgraSliceInUse[sliceIndex] = false;
            }
        }
    }

    private static int FindAvailableSlice(bool[] sliceInUse, ref int nextSlice)
    {
        for (var i = 0; i < SliceCount; i++)
        {
            var candidate = (nextSlice + i) % SliceCount;
            if (!sliceInUse[candidate])
            {
                nextSlice = (candidate + 1) % SliceCount;
                return candidate;
            }
        }

        // All slices are still held by UI/presenter; let caller fall back or drop smoothly.
        return -1;
    }

    private void EnsureDevice()
    {
        if (_device is not null)
        {
            return;
        }

        var creationFlags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            creationFlags,
            featureLevels,
            out _device,
            out _context).CheckError();

        _multithread = _device!.QueryInterfaceOrNull<ID3D11Multithread>() ??
                       _context?.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);

        var nv12Support = _device.CheckFormatSupport(Format.NV12);
        _supportsNv12Texture = (nv12Support & (FormatSupport.Texture2D | FormatSupport.DecoderOutput)) != 0;
    }

    private void EnsureBgraTextures(int width, int height)
    {
        if (_bgraWidth == width && _bgraHeight == height && _bgraTextureArray is not null)
        {
            return;
        }

        DisposeBgraTextures();
        _bgraWidth = width;
        _bgraHeight = height;

        var desc = new Texture2DDescription
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = SliceCount,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        _bgraTextureArray = _device!.CreateTexture2D(desc);
        _currentBgraTexturePtr = _bgraTextureArray.NativePointer;
        Array.Clear(_bgraSliceInUse);
        _nextBgraSlice = 0;
    }

    private void EnsureNv12Textures(int width, int height)
    {
        if (_nv12Width == width &&
            _nv12Height == height &&
            _nv12TextureArray is not null &&
            _nv12StagingTexture is not null)
        {
            return;
        }

        DisposeNv12Textures();
        _nv12Width = width;
        _nv12Height = height;

        var desc = new Texture2DDescription
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = SliceCount,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.Decoder,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        var stagingDesc = new Texture2DDescription
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        };

        try
        {
            _nv12TextureArray = _device!.CreateTexture2D(desc);
        }
        catch
        {
            desc.BindFlags = BindFlags.None;
            _nv12TextureArray = _device!.CreateTexture2D(desc);
        }

        _nv12StagingTexture = _device.CreateTexture2D(stagingDesc);
        _currentNv12TexturePtr = _nv12TextureArray.NativePointer;
        Array.Clear(_nv12SliceInUse);
        _nextNv12Slice = 0;
    }

    private void DisposeBgraTextures()
    {
        _currentBgraTexturePtr = IntPtr.Zero;
        _bgraTextureArray?.Dispose();
        _bgraTextureArray = null;
        Array.Clear(_bgraSliceInUse);
    }

    private void DisposeNv12Textures()
    {
        _currentNv12TexturePtr = IntPtr.Zero;
        _nv12StagingTexture?.Dispose();
        _nv12StagingTexture = null;
        _nv12TextureArray?.Dispose();
        _nv12TextureArray = null;
        Array.Clear(_nv12SliceInUse);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeBgraTextures();
            DisposeNv12Textures();
            _multithread?.Dispose();
            _multithread = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
        }
    }
}
#endif
