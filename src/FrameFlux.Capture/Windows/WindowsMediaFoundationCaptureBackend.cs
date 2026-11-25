#if !ANDROID
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameFlux.Capture;

internal sealed unsafe class WindowsMediaFoundationCaptureBackend : ICaptureBackend
{
    private readonly CaptureFrameBufferPool _bufferPool;
#if NET8_0_OR_GREATER
    private WindowsCaptureD3D11TexturePool? _d3d11Pool;
    private IntPtr _dxgiDeviceManager;
    private bool _dxgiManagerActive;
#endif
    private readonly object _sync = new();
    private IntPtr _mediaSource;
    private IntPtr _sourceReader;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private int _width;
    private int _height;
    private int _defaultStride;
    private MediaCapturePixelFormat _outputPixelFormat;
    private CaptureYuvColorSpace _yuvColorSpace = CaptureYuvColorSpace.Bt601Limited;
    private MediaCaptureVideoFormat? _negotiatedFormat;
    private string _deviceDisplayName = string.Empty;
    private bool _hardwareActive;
    private bool _disposed;

    internal WindowsMediaFoundationCaptureBackend(CaptureFrameBufferPool bufferPool)
    {
        _bufferPool = bufferPool;
    }

    public CaptureOpenResult Open(
        ParsedCaptureUri parsedUri,
        MediaOpenOptions openOptions,
        CapturePlayerOptions playerOptions)
    {
        WindowsMediaFoundationInterop.EnsureInitialized();

        var devices = WindowsMediaFoundationEnumerator.GetVideoDevices(includeFormats: false);
        MediaCaptureDeviceInfo? targetDevice = null;

        if (!string.IsNullOrWhiteSpace(parsedUri.DeviceId))
        {
            targetDevice = devices.FirstOrDefault(d =>
                d.DeviceId.Equals(parsedUri.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (targetDevice is null)
            {
                throw new InvalidOperationException(
                    $"Capture device '{parsedUri.DeviceId}' was not found.");
            }
        }
        else
        {
            var index = parsedUri.DeviceIndex ?? 0;
            if (index < 0 || index >= devices.Count)
            {
                throw new InvalidOperationException(
                    devices.Count == 0
                        ? "No local video capture devices were detected."
                        : $"Capture device index {index} is out of range (0..{devices.Count - 1}).");
            }

            targetDevice = devices[index];
        }

        IntPtr mediaSource = IntPtr.Zero;
        IntPtr readerAttrs = IntPtr.Zero;
        IntPtr sourceReader = IntPtr.Zero;
#if NET8_0_OR_GREATER
        IntPtr dxgiManager = IntPtr.Zero;
        var dxgiManagerBound = false;
#endif

        try
        {
            mediaSource = WindowsMediaFoundationEnumerator.CreateDeviceMediaSource(targetDevice.DeviceId);

            var hr = WindowsMediaFoundationInterop.MFCreateAttributes(out readerAttrs, 4);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFCreateAttributes));

            var allowHardware = openOptions.Video.DecodingPolicy != MediaVideoDecodingPolicy.SoftwareOnly &&
                                playerOptions.EnableHardwareVideoOutput;
            var actualHardwareActive = false;

#if NET8_0_OR_GREATER
            if (allowHardware)
            {
                _ = WindowsMediaFoundationInterop.AttributesSetUInt32(
                    readerAttrs,
                    WindowsMediaFoundationInterop.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                    1);

                _d3d11Pool ??= new WindowsCaptureD3D11TexturePool();
                if (_d3d11Pool.TryGetDeviceNativePointer(out var d3d11DevicePtr))
                {
                    actualHardwareActive = true;
                    try
                    {
                        if (WindowsMediaFoundationInterop.MFCreateDXGIDeviceManager(
                                out var resetToken,
                                out dxgiManager) >= 0 &&
                            dxgiManager != IntPtr.Zero &&
                            WindowsMediaFoundationInterop.DXGIDeviceManagerResetDevice(
                                dxgiManager,
                                d3d11DevicePtr,
                                resetToken) >= 0 &&
                            WindowsMediaFoundationInterop.AttributesSetUnknown(
                                readerAttrs,
                                WindowsMediaFoundationInterop.MF_SOURCE_READER_D3D_MANAGER,
                                dxgiManager) >= 0)
                        {
                            dxgiManagerBound = true;
                        }
                        else
                        {
                            WindowsMediaFoundationInterop.SafeRelease(ref dxgiManager);
                        }
                    }
                    finally
                    {
                        WindowsMediaFoundationInterop.SafeRelease(ref d3d11DevicePtr);
                    }
                }
            }

            // MF_SOURCE_READER_D3D_MANAGER requires MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING
            // rather than MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING (which is mutually exclusive with D3D manager).
            _ = WindowsMediaFoundationInterop.AttributesSetUInt32(
                readerAttrs,
                dxgiManagerBound
                    ? WindowsMediaFoundationInterop.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING
                    : WindowsMediaFoundationInterop.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,
                1);
#else
            _ = WindowsMediaFoundationInterop.AttributesSetUInt32(
                readerAttrs,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,
                1);

            if (allowHardware)
            {
                _ = WindowsMediaFoundationInterop.AttributesSetUInt32(
                    readerAttrs,
                    WindowsMediaFoundationInterop.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                    1);
            }
#endif

            hr = WindowsMediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                mediaSource,
                readerAttrs,
                out sourceReader);

#if NET8_0_OR_GREATER
            if (hr < 0 && dxgiManagerBound)
            {
                // Some UVC drivers or MFTs reject MF_SOURCE_READER_D3D_MANAGER; recreate the media source
                // (in case MF shut it down on failure) and retry cleanly with standard video processing.
                _ = WindowsMediaFoundationInterop.AttributesDeleteItem(
                    readerAttrs,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_D3D_MANAGER);
                _ = WindowsMediaFoundationInterop.AttributesDeleteItem(
                    readerAttrs,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING);
                _ = WindowsMediaFoundationInterop.AttributesSetUInt32(
                    readerAttrs,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,
                    1);
                WindowsMediaFoundationInterop.SafeRelease(ref dxgiManager);
                dxgiManagerBound = false;

                if (mediaSource != IntPtr.Zero)
                {
                    _ = WindowsMediaFoundationInterop.MediaSourceShutdown(mediaSource);
                    WindowsMediaFoundationInterop.SafeRelease(ref mediaSource);
                }

                mediaSource = WindowsMediaFoundationEnumerator.CreateDeviceMediaSource(targetDevice.DeviceId);
                hr = WindowsMediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                    mediaSource,
                    readerAttrs,
                    out sourceReader);
            }
#endif

            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.MFCreateSourceReaderFromMediaSource));

            // Deselect all streams, then explicitly enable first video stream.
            _ = WindowsMediaFoundationInterop.SourceReaderSetStreamSelection(
                sourceReader,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS,
                false);
            hr = WindowsMediaFoundationInterop.SourceReaderSetStreamSelection(
                sourceReader,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                true);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, "SetStreamSelection(FIRST_VIDEO_STREAM)");

            var nativeFormats = WindowsMediaFoundationEnumerator.EnumerateNativeFormats(sourceReader);
            var selectedNative = CaptureFormatSelector.SelectBestFormat(
                nativeFormats,
                parsedUri.Width,
                parsedUri.Height,
                parsedUri.FrameRate,
                parsedUri.PixelFormat,
                openOptions.Video,
                playerOptions);

            if (selectedNative is not null)
            {
                ApplyBestNativeFormat(sourceReader, selectedNative);
            }

            var negotiatedOutput = ConfigureOutputMediaType(
                sourceReader,
                selectedNative,
                preferGpuNv12: actualHardwareActive);

            RefreshCurrentOutputMediaType(sourceReader, selectedNative?.PixelFormat);

            lock (_sync)
            {
                _mediaSource = mediaSource;
                _sourceReader = sourceReader;
                _negotiatedFormat = selectedNative ?? new MediaCaptureVideoFormat(_width, _height, 30d, _outputPixelFormat);
                _deviceDisplayName = targetDevice.UniqueDisplayName;
                _hardwareActive = actualHardwareActive;
#if NET8_0_OR_GREATER
                _dxgiDeviceManager = dxgiManager;
                _dxgiManagerActive = dxgiManagerBound;
                dxgiManager = IntPtr.Zero;
#endif
                mediaSource = IntPtr.Zero;
                sourceReader = IntPtr.Zero;
            }

            var fpsText = _negotiatedFormat.FrameRate > 0
                ? $"{_negotiatedFormat.FrameRate:0.##}fps"
                : "variable fps";
#if NET8_0_OR_GREATER
            var modeTag = actualHardwareActive
                ? (dxgiManagerBound ? "D3D11 DXGI GPU" : "D3D11 GPU")
                : "CPU Software";
#else
            var modeTag = "CPU Software";
#endif
            var diag =
                $"MediaFoundation [{_deviceDisplayName}: {_width}x{_height}@{fpsText} {_negotiatedFormat.PixelFormat} -> {negotiatedOutput}, {modeTag}]";

            return new CaptureOpenResult(
                targetDevice.DeviceId,
                _deviceDisplayName,
                targetDevice.PairedAudioDeviceId,
                _negotiatedFormat,
                negotiatedOutput,
                diag,
                IsHardwareVideoActive: actualHardwareActive);
        }
        finally
        {
            WindowsMediaFoundationInterop.SafeRelease(ref readerAttrs);
#if NET8_0_OR_GREATER
            WindowsMediaFoundationInterop.SafeRelease(ref dxgiManager);
#endif
            if (sourceReader != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.SourceReaderFlush(
                    sourceReader,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
                WindowsMediaFoundationInterop.SafeRelease(ref sourceReader);
            }

            if (mediaSource != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.MediaSourceShutdown(mediaSource);
                WindowsMediaFoundationInterop.SafeRelease(ref mediaSource);
            }
        }
    }

    public void Start(
        Func<bool> prefersHardwareFrame,
        Func<bool> needsCpuFrame,
        Action<CaptureDeliveredFrame> onFrame,
        Action<Exception, bool> onError)
    {
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            if (_captureThread is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _captureThread = new Thread(() => RunCaptureLoop(token, prefersHardwareFrame, needsCpuFrame, onFrame, onError))
            {
                IsBackground = true,
                Name = $"FrameFlux.Capture ({_deviceDisplayName})"
            };
            _captureThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        IntPtr localSource = IntPtr.Zero;
        IntPtr localReader = IntPtr.Zero;

        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            thread = _captureThread;
            _captureThread = null;
            // Take temporary AddRef'd handles so we can interrupt a blocked synchronous ReadSample
            // outside the lock without racing with ReleaseMediaFoundationHandles().
            localSource = WindowsMediaFoundationInterop.AddRef(_mediaSource);
            localReader = WindowsMediaFoundationInterop.AddRef(_sourceReader);
        }

        cts?.Cancel();

        try
        {
            if (localReader != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.SourceReaderFlush(
                    localReader,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            }

            if (localSource != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.MediaSourceShutdown(localSource);
            }
        }
        finally
        {
            WindowsMediaFoundationInterop.SafeRelease(ref localReader);
            WindowsMediaFoundationInterop.SafeRelease(ref localSource);
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }

        cts?.Dispose();
        ReleaseMediaFoundationHandles();
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
        }

        Stop();
#if NET8_0_OR_GREATER
        _d3d11Pool?.Dispose();
        _d3d11Pool = null;
#endif
    }

    private void RunCaptureLoop(
        CancellationToken token,
        Func<bool> prefersD3D11,
        Func<bool> needsCpuFrame,
        Action<CaptureDeliveredFrame> onFrame,
        Action<Exception, bool> onError)
    {
        while (!token.IsCancellationRequested)
        {
            IntPtr reader;
            lock (_sync)
            {
                // Hold an independent COM reference for the duration of ReadSample so even if Stop()
                // releases the backend's handle after a Join timeout, this call cannot use-after-free.
                reader = WindowsMediaFoundationInterop.AddRef(_sourceReader);
            }

            if (reader == IntPtr.Zero)
            {
                break;
            }

            IntPtr sample = IntPtr.Zero;
            try
            {
                var readStart = Stopwatch.GetTimestamp();
                var hr = WindowsMediaFoundationInterop.SourceReaderReadSample(
                    reader,
                    WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out _,
                    out var streamFlags,
                    out _,
                    out sample);
                var readMs = RuntimeCompat.Elapsed(readStart).TotalMilliseconds;

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (hr < 0)
                {
                    var isDisconnect = WindowsMediaFoundationInterop.IsDeviceDisconnectHResult(hr);
                    var ex = Marshal.GetExceptionForHR(hr) ??
                             new InvalidOperationException($"Media Foundation ReadSample failed (0x{hr:X8}).");
                    onError(ex, isDisconnect || !token.IsCancellationRequested);
                    break;
                }

                if ((streamFlags & WindowsMediaFoundationInterop.MF_SOURCE_READERF_ERROR) != 0)
                {
                    onError(new InvalidOperationException("The video capture device reported a stream error."), true);
                    break;
                }

                if ((streamFlags & WindowsMediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    onError(new InvalidOperationException("The video capture device stream ended."), true);
                    break;
                }

                if ((streamFlags & WindowsMediaFoundationInterop.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                {
                    RefreshCurrentOutputMediaType(reader, _negotiatedFormat?.PixelFormat);
                }

                if (sample == IntPtr.Zero ||
                    (streamFlags & WindowsMediaFoundationInterop.MF_SOURCE_READERF_STREAMTICK) != 0)
                {
                    continue;
                }

                var decodeStart = Stopwatch.GetTimestamp();
                var delivered = ExtractAndDeliverFrame(
                    sample,
                    prefersD3D11(),
                    needsCpuFrame(),
                    readMs,
                    decodeStart);
                if (delivered is { } frame)
                {
                    onFrame(frame);
                }
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    onError(exception, false);
                }
                break;
            }
            finally
            {
                WindowsMediaFoundationInterop.SafeRelease(ref sample);
                WindowsMediaFoundationInterop.SafeRelease(ref reader);
            }
        }
    }

    private CaptureDeliveredFrame? ExtractAndDeliverFrame(
        IntPtr sample,
        bool wantD3D11,
        bool wantCpu,
        double readMs,
        long decodeStartTicks)
    {
        var width = Volatile.Read(ref _width);
        var height = Volatile.Read(ref _height);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        CaptureMediaFrameLease? d3d11Lease = null;

#if NET8_0_OR_GREATER
        // Fast path 1: True GPU-to-GPU zero-readback path via IMFDXGIBuffer when DXGI Device Manager is active.
        if (wantD3D11 && _hardwareActive && _dxgiManagerActive && _d3d11Pool is not null)
        {
            if (TryExtractDxgiGpuLease(sample, width, height, out d3d11Lease) && !wantCpu)
            {
                var gpuDecodeMs = RuntimeCompat.Elapsed(decodeStartTicks).TotalMilliseconds;
                return new CaptureDeliveredFrame(d3d11Lease, null, width, height, readMs, gpuDecodeMs);
            }
        }
#endif

        IntPtr buffer = IntPtr.Zero;
        IntPtr buffer2D = IntPtr.Zero;
        var locked2D = false;
        var locked1D = false;

        try
        {
            var hr = WindowsMediaFoundationInterop.SampleConvertToContiguousBuffer(sample, out buffer);
            if (hr < 0 || buffer == IntPtr.Zero)
            {
                d3d11Lease?.Dispose();
                return null;
            }

            IntPtr scanline0;
            int pitch;
            int maxBytes;

            if (WindowsMediaFoundationInterop.QueryInterface(
                    buffer,
                    WindowsMediaFoundationInterop.IID_IMF2DBuffer,
                    out buffer2D) >= 0 &&
                buffer2D != IntPtr.Zero &&
                WindowsMediaFoundationInterop.Buffer2DLock(buffer2D, out scanline0, out pitch) >= 0)
            {
                locked2D = true;
                maxBytes = int.MaxValue;
            }
            else
            {
                WindowsMediaFoundationInterop.SafeRelease(ref buffer2D);
                if (WindowsMediaFoundationInterop.BufferLock(
                        buffer,
                        out scanline0,
                        out _,
                        out var currentLength) < 0)
                {
                    d3d11Lease?.Dispose();
                    return null;
                }

                locked1D = true;
                maxBytes = unchecked((int)currentLength);
                pitch = Volatile.Read(ref _defaultStride);
                if (pitch == 0)
                {
                    pitch = _outputPixelFormat switch
                    {
                        MediaCapturePixelFormat.Nv12 => width,
                        MediaCapturePixelFormat.Yuy2 => width * 2,
                        _ => width * 4
                    };
                }
            }

            if (scanline0 == IntPtr.Zero || pitch == 0)
            {
                d3d11Lease?.Dispose();
                return null;
            }

            var pixelFormat = _outputPixelFormat;
            var absPitch = Math.Abs(pitch);

            if (pixelFormat == MediaCapturePixelFormat.Nv12)
            {
                if (locked1D && maxBytes < checked(absPitch * height * 3 / 2))
                {
                    d3d11Lease?.Dispose();
                    return null;
                }

                ComputeNv12Planes(scanline0, pitch, height, out var yPlane, out var uvPlane);

#if NET8_0_OR_GREATER
                // Fast path 2: Direct NV12 upload to GPU Format.NV12 texture (2.67x less PCIe bandwidth, zero CPU YUV conversion).
                if (d3d11Lease is null && wantD3D11 && _hardwareActive && _d3d11Pool is not null)
                {
                    if (_d3d11Pool.TryUploadNv12Frame(yPlane, pitch, uvPlane, pitch, width, height, out d3d11Lease) &&
                        !wantCpu)
                    {
                        var nv12UploadMs = RuntimeCompat.Elapsed(decodeStartTicks).TotalMilliseconds;
                        return new CaptureDeliveredFrame(d3d11Lease, null, width, height, readMs, nv12UploadMs);
                    }
                }
#endif
                return ConvertAndDeliverCpuBackedFrame(
                    width,
                    height,
                    wantD3D11,
                    wantCpu,
                    readMs,
                    decodeStartTicks,
                    d3d11Lease,
                    (bgraBuffer, bgraStride) => CapturePixelConverter.Nv12ToBgra32(
                        yPlane,
                        pitch,
                        uvPlane,
                        pitch,
                        bgraBuffer,
                        width,
                        height,
                        bgraStride,
                        _yuvColorSpace));
            }

            if (pixelFormat == MediaCapturePixelFormat.Yuy2)
            {
                if (locked1D && maxBytes < checked(absPitch * height))
                {
                    d3d11Lease?.Dispose();
                    return null;
                }

                var yuy2Scanline0 = ResolveTopScanline(scanline0, pitch, height, locked1D);
                return ConvertAndDeliverCpuBackedFrame(
                    width,
                    height,
                    wantD3D11,
                    wantCpu,
                    readMs,
                    decodeStartTicks,
                    d3d11Lease,
                    (bgraBuffer, bgraStride) => CapturePixelConverter.Yuy2ToBgra32(
                        yuy2Scanline0,
                        pitch,
                        bgraBuffer,
                        width,
                        height,
                        bgraStride,
                        _yuvColorSpace));
            }

            if (locked1D && maxBytes < checked(absPitch * height))
            {
                d3d11Lease?.Dispose();
                return null;
            }

            var rgbScanline0 = ResolveTopScanline(scanline0, pitch, height, locked1D);
            return ConvertAndDeliverCpuBackedFrame(
                width,
                height,
                wantD3D11,
                wantCpu,
                readMs,
                decodeStartTicks,
                d3d11Lease,
                (bgraBuffer, bgraStride) => CapturePixelConverter.Rgb32ToBgra32(
                    rgbScanline0,
                    pitch,
                    bgraBuffer,
                    width,
                    height,
                    bgraStride,
                    forceOpaqueAlpha: true));
        }
        finally
        {
            if (locked2D && buffer2D != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.Buffer2DUnlock(buffer2D);
            }

            if (locked1D && buffer != IntPtr.Zero)
            {
                _ = WindowsMediaFoundationInterop.BufferUnlock(buffer);
            }

            WindowsMediaFoundationInterop.SafeRelease(ref buffer2D);
            WindowsMediaFoundationInterop.SafeRelease(ref buffer);
        }
    }

    private CaptureDeliveredFrame ConvertAndDeliverCpuBackedFrame(
        int width,
        int height,
        bool wantD3D11,
        bool wantCpu,
        double readMs,
        long decodeStartTicks,
        CaptureMediaFrameLease? existingD3D11Lease,
        Action<IntPtr, int> convertToBgra)
    {
        var bgraStride = checked(width * 4);
        var bgraSize = checked(bgraStride * height);
        var bgraBuffer = _bufferPool.Rent(bgraSize);
        var ownershipTransferredToCpuLease = false;
        var d3d11Lease = existingD3D11Lease;

        try
        {
            convertToBgra(bgraBuffer, bgraStride);

#if NET8_0_OR_GREATER
            if (d3d11Lease is null && wantD3D11 && _hardwareActive && _d3d11Pool is not null)
            {
                _d3d11Pool.TryUploadBgraFrame(bgraBuffer, width, height, bgraStride, out d3d11Lease);
            }
#endif

            CaptureMediaFrameLease? cpuLease = null;
            if (wantCpu || d3d11Lease is null)
            {
                cpuLease = CaptureMediaFrameLease.CreateBgraCpu(
                    width,
                    height,
                    bgraStride,
                    bgraBuffer,
                    bgraSize,
                    _bufferPool);
                ownershipTransferredToCpuLease = true;
            }

            var decodeMs = RuntimeCompat.Elapsed(decodeStartTicks).TotalMilliseconds;
            return new CaptureDeliveredFrame(d3d11Lease, cpuLease, width, height, readMs, decodeMs);
        }
        catch
        {
            d3d11Lease?.Dispose();
            throw;
        }
        finally
        {
            if (!ownershipTransferredToCpuLease)
            {
                _bufferPool.Return(bgraBuffer, bgraSize);
            }
        }
    }

#if NET8_0_OR_GREATER
    private bool TryExtractDxgiGpuLease(
        IntPtr sample,
        int width,
        int height,
        out CaptureMediaFrameLease? lease)
    {
        lease = null;
        IntPtr buffer = IntPtr.Zero;
        IntPtr dxgiBuffer = IntPtr.Zero;
        IntPtr texturePtr = IntPtr.Zero;

        try
        {
            if (WindowsMediaFoundationInterop.SampleGetBufferCount(sample, out var count) < 0 || count == 0)
            {
                return false;
            }

            if (WindowsMediaFoundationInterop.SampleGetBufferByIndex(sample, 0, out buffer) < 0 || buffer == IntPtr.Zero)
            {
                return false;
            }

            if (WindowsMediaFoundationInterop.QueryInterface(
                    buffer,
                    WindowsMediaFoundationInterop.IID_IMFDXGIBuffer,
                    out dxgiBuffer) < 0 || dxgiBuffer == IntPtr.Zero)
            {
                return false;
            }

            if (WindowsMediaFoundationInterop.DXGIBufferGetResource(
                    dxgiBuffer,
                    WindowsMediaFoundationInterop.IID_ID3D11Texture2D,
                    out texturePtr) < 0 || texturePtr == IntPtr.Zero)
            {
                return false;
            }

            var subresourceIndex = 0u;
            _ = WindowsMediaFoundationInterop.DXGIBufferGetSubresourceIndex(dxgiBuffer, out subresourceIndex);

            return _d3d11Pool!.TryCopyGpuTexture(
                texturePtr,
                checked((int)subresourceIndex),
                width,
                height,
                out lease);
        }
        catch
        {
            return false;
        }
        finally
        {
            WindowsMediaFoundationInterop.SafeRelease(ref texturePtr);
            WindowsMediaFoundationInterop.SafeRelease(ref dxgiBuffer);
            WindowsMediaFoundationInterop.SafeRelease(ref buffer);
        }
    }
#endif

    /// <summary>
    /// Resolves the pointer to the top image row when a 1D Lock() returned the buffer base address
    /// with a negative MF_MT_DEFAULT_STRIDE (bottom-up layout). For 2D Lock2D(), scanline0 already
    /// points to the top image row even when pitch is negative.
    /// </summary>
    private static IntPtr ResolveTopScanline(IntPtr scanline0, int pitch, int height, bool locked1D)
    {
        if (locked1D && pitch < 0 && height > 1)
        {
            return (IntPtr)((byte*)scanline0 + ((long)(-pitch) * (height - 1)));
        }

        return scanline0;
    }

    private static void ComputeNv12Planes(
        IntPtr scanline0,
        int pitch,
        int height,
        out IntPtr yPlane,
        out IntPtr uvPlane)
    {
        if (pitch > 0)
        {
            yPlane = scanline0;
            uvPlane = (IntPtr)((byte*)scanline0 + ((long)pitch * height));
            return;
        }

        // Bottom-up NV12: scanline0 is the top row of Y (last row in memory), pitch is negative.
        var absPitch = (long)(-pitch);
        var yBase = (byte*)scanline0 - (absPitch * (height - 1));
        var uvBase = yBase + (absPitch * height);
        var uvHeight = Math.Max(1, height >> 1);
        yPlane = scanline0;
        uvPlane = (IntPtr)(uvBase + (absPitch * (uvHeight - 1)));
    }

    private static void ApplyBestNativeFormat(IntPtr sourceReader, MediaCaptureVideoFormat target)
    {
        for (uint typeIndex = 0; ; typeIndex++)
        {
            var hr = WindowsMediaFoundationInterop.SourceReaderGetNativeMediaType(
                sourceReader,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                typeIndex,
                out var mediaType);
            if (hr == WindowsMediaFoundationInterop.MF_E_NO_MORE_TYPES || hr < 0 || mediaType == IntPtr.Zero)
            {
                break;
            }

            try
            {
                if (WindowsMediaFoundationEnumerator.TryReadVideoFormat(mediaType, out var candidate) &&
                    candidate.Width == target.Width &&
                    candidate.Height == target.Height &&
                    candidate.PixelFormat == target.PixelFormat &&
                    Math.Abs(candidate.FrameRate - target.FrameRate) <= 0.5d)
                {
                    _ = WindowsMediaFoundationInterop.SourceReaderSetCurrentMediaType(
                        sourceReader,
                        WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                        mediaType);
                    return;
                }
            }
            finally
            {
                WindowsMediaFoundationInterop.SafeRelease(ref mediaType);
            }
        }
    }

    private static MediaCapturePixelFormat ConfigureOutputMediaType(
        IntPtr sourceReader,
        MediaCaptureVideoFormat? nativeFormat,
        bool preferGpuNv12)
    {
        var candidates = new List<MediaCapturePixelFormat>();
        if (preferGpuNv12)
        {
            // When hardware GPU presentation is enabled, prefer NV12 first even for native YUY2 webcams
            // so Media Foundation's GPU video processor handles YUY2->NV12 without CPU conversion + 4Bpp BGRA upload.
            if (nativeFormat is { PixelFormat: MediaCapturePixelFormat.Bgra32 })
            {
                candidates.Add(MediaCapturePixelFormat.Bgra32);
            }

            candidates.Add(MediaCapturePixelFormat.Nv12);
        }
        else if (nativeFormat is { PixelFormat: MediaCapturePixelFormat.Nv12 or MediaCapturePixelFormat.Yuy2 or MediaCapturePixelFormat.Bgra32 })
        {
            candidates.Add(nativeFormat.PixelFormat);
        }

        if (!candidates.Contains(MediaCapturePixelFormat.Nv12)) candidates.Add(MediaCapturePixelFormat.Nv12);
        if (!candidates.Contains(MediaCapturePixelFormat.Bgra32)) candidates.Add(MediaCapturePixelFormat.Bgra32);
        if (!candidates.Contains(MediaCapturePixelFormat.Yuy2)) candidates.Add(MediaCapturePixelFormat.Yuy2);

        foreach (var candidate in candidates)
        {
            var subtype = WindowsMediaFoundationInterop.MapPixelFormatToSubtype(candidate);
            if (subtype == Guid.Empty)
            {
                continue;
            }

            IntPtr mediaType = IntPtr.Zero;
            try
            {
                if (WindowsMediaFoundationInterop.MFCreateMediaType(out mediaType) < 0 || mediaType == IntPtr.Zero)
                {
                    continue;
                }

                _ = WindowsMediaFoundationInterop.AttributesSetGuid(
                    mediaType,
                    WindowsMediaFoundationInterop.MF_MT_MAJOR_TYPE,
                    WindowsMediaFoundationInterop.MFMediaType_Video);
                _ = WindowsMediaFoundationInterop.AttributesSetGuid(
                    mediaType,
                    WindowsMediaFoundationInterop.MF_MT_SUBTYPE,
                    subtype);

                if (WindowsMediaFoundationInterop.SourceReaderSetCurrentMediaType(
                        sourceReader,
                        WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                        mediaType) >= 0)
                {
                    return candidate;
                }
            }
            finally
            {
                WindowsMediaFoundationInterop.SafeRelease(ref mediaType);
            }
        }

        throw new InvalidOperationException(
            "Unable to negotiate a supported uncompressed output format (NV12, RGB32, or YUY2) from the capture device.");
    }

    private void RefreshCurrentOutputMediaType(IntPtr sourceReader, MediaCapturePixelFormat? nativePixelFormat)
    {
        IntPtr currentType = IntPtr.Zero;
        try
        {
            var hr = WindowsMediaFoundationInterop.SourceReaderGetCurrentMediaType(
                sourceReader,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                out currentType);
            WindowsMediaFoundationInterop.ThrowIfFailed(hr, nameof(WindowsMediaFoundationInterop.SourceReaderGetCurrentMediaType));

            var width = Volatile.Read(ref _width);
            var height = Volatile.Read(ref _height);
            if (WindowsMediaFoundationInterop.AttributesGetUInt64(
                    currentType,
                    WindowsMediaFoundationInterop.MF_MT_FRAME_SIZE,
                    out var packedSize) >= 0)
            {
                WindowsMediaFoundationInterop.UnpackSize(packedSize, out width, out height);
                Volatile.Write(ref _width, width);
                Volatile.Write(ref _height, height);
            }

            if (WindowsMediaFoundationInterop.AttributesGetGuid(
                    currentType,
                    WindowsMediaFoundationInterop.MF_MT_SUBTYPE,
                    out var subtype) >= 0)
            {
                _outputPixelFormat = WindowsMediaFoundationInterop.MapSubtypeToPixelFormat(subtype);
            }

            var stride = 0;
            if (WindowsMediaFoundationInterop.AttributesGetUInt32(
                    currentType,
                    WindowsMediaFoundationInterop.MF_MT_DEFAULT_STRIDE,
                    out var rawStride) >= 0)
            {
                stride = unchecked((int)rawStride);
            }

            Volatile.Write(ref _defaultStride, stride);
            _yuvColorSpace = ResolveColorSpace(currentType, width, height, nativePixelFormat);
        }
        finally
        {
            WindowsMediaFoundationInterop.SafeRelease(ref currentType);
        }
    }

    private static CaptureYuvColorSpace ResolveColorSpace(
        IntPtr mediaType,
        int width,
        int height,
        MediaCapturePixelFormat? nativePixelFormat)
    {
        var isBt709 = width >= 1280 || height >= 720;
        if (WindowsMediaFoundationInterop.AttributesGetUInt32(
                mediaType,
                WindowsMediaFoundationInterop.MF_MT_YUV_MATRIX,
                out var matrix) >= 0)
        {
            if (matrix == WindowsMediaFoundationInterop.MFVideoTransferMatrix_BT709)
            {
                isBt709 = true;
            }
            else if (matrix == WindowsMediaFoundationInterop.MFVideoTransferMatrix_BT601)
            {
                isBt709 = false;
            }
        }

        var isFullRange = nativePixelFormat == MediaCapturePixelFormat.Mjpeg;
        if (WindowsMediaFoundationInterop.AttributesGetUInt32(
                mediaType,
                WindowsMediaFoundationInterop.MF_MT_VIDEO_NOMINAL_RANGE,
                out var nominalRange) >= 0)
        {
            if (nominalRange == WindowsMediaFoundationInterop.MFNominalRange_0_255)
            {
                isFullRange = true;
            }
            else if (nominalRange == WindowsMediaFoundationInterop.MFNominalRange_16_235)
            {
                isFullRange = false;
            }
        }

        return (isBt709, isFullRange) switch
        {
            (true, true) => CaptureYuvColorSpace.Bt709Full,
            (true, false) => CaptureYuvColorSpace.Bt709Limited,
            (false, true) => CaptureYuvColorSpace.Bt601Full,
            _ => CaptureYuvColorSpace.Bt601Limited
        };
    }

    private void ReleaseMediaFoundationHandles()
    {
        IntPtr reader;
        IntPtr source;
#if NET8_0_OR_GREATER
        IntPtr dxgiManager;
#endif
        lock (_sync)
        {
            reader = _sourceReader;
            _sourceReader = IntPtr.Zero;
            source = _mediaSource;
            _mediaSource = IntPtr.Zero;
#if NET8_0_OR_GREATER
            dxgiManager = _dxgiDeviceManager;
            _dxgiDeviceManager = IntPtr.Zero;
            _dxgiManagerActive = false;
#endif
        }

        if (reader != IntPtr.Zero)
        {
            _ = WindowsMediaFoundationInterop.SourceReaderFlush(
                reader,
                WindowsMediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            WindowsMediaFoundationInterop.SafeRelease(ref reader);
        }

        if (source != IntPtr.Zero)
        {
            _ = WindowsMediaFoundationInterop.MediaSourceShutdown(source);
            WindowsMediaFoundationInterop.SafeRelease(ref source);
        }

#if NET8_0_OR_GREATER
        WindowsMediaFoundationInterop.SafeRelease(ref dxgiManager);
#endif
    }
}
#endif
