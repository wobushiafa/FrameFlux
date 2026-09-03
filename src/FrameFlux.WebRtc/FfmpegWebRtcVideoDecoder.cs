using System.Runtime.InteropServices;
using SIPSorceryMedia.Abstractions;

namespace FrameFlux.WebRtc;

/// <summary>
/// High-performance video decoder that dynamically binds to native FFmpeg libraries (avcodec/avutil)
/// if available in the application runtime directory.
/// Supports H.264, H.265 (HEVC), VP8, VP9, and AV1, with D3D11VA hardware acceleration on Windows.
/// </summary>
public sealed class FfmpegWebRtcVideoDecoder : IWebRtcVideoDecoder
{
    private static readonly Lazy<bool> NativeAvailable = new(ProbeNativeLibraries);
    private static IntPtr _avUtilHandle;
    private static IntPtr _avCodecHandle;
    private static IntPtr _hwDeviceCtx;

    private static AvCodecFindDecoderByName? _findDecoderByName;
    private static AvCodecAllocContext3? _allocContext3;
    private static AvCodecOpen2? _open2;
    private static AvCodecSendPacket? _sendPacket;
    private static AvCodecReceiveFrame? _receiveFrame;
    private static AvCodecFlushBuffers? _flushBuffers;
    private static AvCodecFreeContext? _freeContext;
    private static AvPacketAlloc? _packetAlloc;
    private static AvPacketFree? _packetFree;
    private static AvFrameAlloc? _frameAlloc;
    private static AvFrameFree? _frameFree;
    private static AvFrameUnref? _frameUnref;
    private static AvCodecGetHardwareConfig? _codecGetHwConfig;

    // hwaccel
    private static AvHwdeviceFindTypeByName? _hwDeviceFindTypeByName;
    private static AvHwDeviceCtxCreate? _hwDeviceCtxCreate;
    private static AvBufferRef? _bufferRef;
    private static AvBufferUnref? _bufferUnref;
    private static AvHwFrameTransferData? _hwFrameTransferData;

    private readonly object _sync = new();
    private IntPtr _codecContext;
    private IntPtr _packet;
    private IntPtr _frame;
    private IntPtr _transferFrame;
    private int _currentHwPixelFormat = -1;
    private AvCodecGetFormat? _getFormatHandler;
    private IntPtr _getFormatPointer;
    private VideoCodecsEnum? _currentCodec;
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether native FFmpeg libraries are available on this system.
    /// </summary>
    public static bool IsSupported => NativeAvailable.Value;

    /// <summary>
    /// Gets or sets the decoding policy (SoftwareOnly, HardwarePreferred, HardwareRequired).
    /// </summary>
    public MediaVideoDecodingPolicy DecodingPolicy { get; set; } = MediaVideoDecodingPolicy.HardwarePreferred;

    /// <summary>
    /// Gets a value indicating whether hardware acceleration is actively being used.
    /// </summary>
    public bool IsHardwareAccelerated { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether D3D11 hardware texture output is preferred and supported by the current presenter.
    /// </summary>
    public bool CanOutputD3D11Texture { get; set; } = true;

    public FfmpegWebRtcVideoDecoder()
    {
        if (IsSupported)
        {
            _packet = _packetAlloc!();
            _frame = _frameAlloc!();
            _transferFrame = _frameAlloc!();
        }
    }

    public bool CanDecode(VideoFormat format)
    {
        if (!IsSupported)
        {
            return false;
        }

        return format.Codec is VideoCodecsEnum.H264
            or VideoCodecsEnum.H265
            or VideoCodecsEnum.VP8
            or VideoCodecsEnum.VP9
            or VideoCodecsEnum.AV1;
    }

    public bool TryDecode(
        ReadOnlySpan<byte> encodedPayload,
        VideoFormat format,
        WebRtcFrameBufferPool pool,
        out WebRtcMediaFrameLease? decodedFrame)
    {
        decodedFrame = null;

        if (!IsSupported || _disposed || encodedPayload.IsEmpty)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            if (!EnsureCodecContext(format.Codec))
            {
                return false;
            }

            unsafe
            {
                fixed (byte* pData = encodedPayload)
                {
                    // AVPacket layout in 64-bit FFmpeg (5.x, 6.x, 7.x):
                    // offset 24: uint8_t *data
                    // offset 32: int size
                    Marshal.WriteIntPtr(_packet, 24, (IntPtr)pData);
                    Marshal.WriteInt32(_packet, 32, encodedPayload.Length);

                    var sendResult = _sendPacket!(_codecContext, _packet);
                    if (sendResult < 0)
                    {
                        return false;
                    }

                    var recvResult = _receiveFrame!(_codecContext, _frame);
                    if (recvResult != 0)
                    {
                        // Needs more packets (e.g. waiting for keyframe or B-frames)
                        return false;
                    }

                    // AVFrame layout in 64-bit FFmpeg:
                    // offset 0..31: uint8_t *data[4]
                    // offset 64..79: int linesize[4]
                    // offset 104: int width
                    // offset 108: int height
                    var sourceFrame = _frame;
                    var pixFmt = Marshal.ReadInt32(sourceFrame, 116);
                    var width = Marshal.ReadInt32(sourceFrame, 104);
                    var height = Marshal.ReadInt32(sourceFrame, 108);

                    var data0 = Marshal.ReadIntPtr(sourceFrame, 0);
                    var data1 = Marshal.ReadIntPtr(sourceFrame, IntPtr.Size);
                    var data2 = Marshal.ReadIntPtr(sourceFrame, IntPtr.Size * 2);

                    var lineSize0 = Marshal.ReadInt32(sourceFrame, 64);
                    var lineSize1 = Marshal.ReadInt32(sourceFrame, 68);
                    var lineSize2 = Marshal.ReadInt32(sourceFrame, 72);

                    // If hardware frame (D3D11VA = 171/174 or DXVA2 = 53)
                    if (pixFmt == _currentHwPixelFormat || pixFmt is 171 or 174 or 53)
                    {
                        // Path A: Presenter supports direct D3D11Texture (GpuComposition / NativeSurface)
                        if (CanOutputD3D11Texture && _frameClone is not null && width > 0 && height > 0 && data0 != IntPtr.Zero)
                        {
                            var clone = _frameClone(_frame);
                            if (clone != IntPtr.Zero)
                            {
                                var lease = new WebRtcMediaFrameLease(IntPtr.Zero, 0, _ =>
                                {
                                    var ptr = clone;
                                    if (ptr != IntPtr.Zero)
                                    {
                                        _frameFree!(ref ptr);
                                    }
                                });
                                lease.ResetD3D11(width, height, data0, (int)data1);
                                decodedFrame = lease;
                                _frameUnref!(_frame);
                                return true;
                            }
                        }

                        // Path B: Presenter wants CPU memory (SoftwareBitmap), try transfer or fallback to software decoding
                        var transferred = false;
                        if (_hwFrameTransferData is not null)
                        {
                            _frameUnref!(_transferFrame);
                            var transferRes = _hwFrameTransferData(_transferFrame, _frame, 0);
                            if (transferRes >= 0)
                            {
                                sourceFrame = _transferFrame;
                                pixFmt = Marshal.ReadInt32(sourceFrame, 116);
                                lineSize0 = Marshal.ReadInt32(sourceFrame, 64);
                                lineSize1 = Marshal.ReadInt32(sourceFrame, 68);
                                lineSize2 = Marshal.ReadInt32(sourceFrame, 72);
                                data0 = Marshal.ReadIntPtr(sourceFrame, 0);
                                data1 = Marshal.ReadIntPtr(sourceFrame, IntPtr.Size);
                                data2 = Marshal.ReadIntPtr(sourceFrame, IntPtr.Size * 2);
                                transferred = true;
                            }
                        }

                        if (!transferred)
                        {
                            // Transfer failed or unavailable: smoothly fallback to software decoding
                            _frameUnref!(_frame);
                            FallbackToSoftware(format.Codec);
                            return false;
                        }
                    }

                    if (width <= 0 || height <= 0 || lineSize0 <= 0 || data0 == IntPtr.Zero)
                    {
                        _frameUnref!(_frame);
                        if (sourceFrame != _frame)
                        {
                            _frameUnref!(_transferFrame);
                        }
                        return false;
                    }

                    // Copy YUV420P / YUVJ420P planes
                    if (pixFmt is 0 or 12) // AV_PIX_FMT_YUV420P (0) or AV_PIX_FMT_YUVJ420P (12)
                    {
                        if (lineSize1 <= 0 || lineSize2 <= 0 || data1 == IntPtr.Zero || data2 == IntPtr.Zero)
                        {
                            _frameUnref!(_frame);
                            if (sourceFrame != _frame)
                            {
                                _frameUnref!(_transferFrame);
                            }
                            return false;
                        }

                        var ySize = lineSize0 * height;
                        var uSize = lineSize1 * (height / 2);
                        var vSize = lineSize2 * (height / 2);
                        var totalSize = ySize + uSize + vSize;
                        if (totalSize <= 0)
                        {
                            _frameUnref!(_frame);
                            if (sourceFrame != _frame)
                            {
                                _frameUnref!(_transferFrame);
                            }
                            return false;
                        }

                        var buffer = pool.Rent(totalSize);
                        var lease = new WebRtcMediaFrameLease(buffer, totalSize, l => pool.Return(l.Buffer, l.Size));

                        var dstY = buffer;
                        var dstU = buffer + ySize;
                        var dstV = buffer + ySize + uSize;

                        Buffer.MemoryCopy((void*)data0, (void*)dstY, ySize, ySize);
                        Buffer.MemoryCopy((void*)data1, (void*)dstU, uSize, uSize);
                        Buffer.MemoryCopy((void*)data2, (void*)dstV, vSize, vSize);

                        lease.ResetYuv420P(width, height, lineSize0, lineSize1);
                        lease.IsFullRange = (pixFmt == 12);
                        decodedFrame = lease;

                        _frameUnref!(_frame);
                        if (sourceFrame != _frame)
                        {
                            _frameUnref!(_transferFrame);
                        }
                        return true;
                    }
                    else if (pixFmt == 23) // AV_PIX_FMT_NV12 (standard D3D11VA transfer output)
                    {
                        if (lineSize1 <= 0 || data1 == IntPtr.Zero)
                        {
                            _frameUnref!(_frame);
                            if (sourceFrame != _frame)
                            {
                                _frameUnref!(_transferFrame);
                            }
                            return false;
                        }

                        var ySize = lineSize0 * height;
                        var uvSize = lineSize1 * (height / 2);
                        var totalSize = ySize + uvSize;
                        if (totalSize <= 0)
                        {
                            _frameUnref!(_frame);
                            if (sourceFrame != _frame)
                            {
                                _frameUnref!(_transferFrame);
                            }
                            return false;
                        }

                        var buffer = pool.Rent(totalSize);
                        var lease = new WebRtcMediaFrameLease(buffer, totalSize, l => pool.Return(l.Buffer, l.Size));

                        Buffer.MemoryCopy((void*)data0, (void*)buffer, ySize, ySize);
                        Buffer.MemoryCopy((void*)data1, (void*)(buffer + ySize), uvSize, uvSize);

                        lease.ResetNv12(width, height, lineSize0, lineSize1);
                        decodedFrame = lease;

                        _frameUnref!(_frame);
                        if (sourceFrame != _frame)
                        {
                            _frameUnref!(_transferFrame);
                        }
                        return true;
                    }
                    else if (pixFmt is 28 or 30 or 32) // BGRA / BGR0
                    {
                        var size = lineSize0 * height;
                        if (size <= 0)
                        {
                            _frameUnref!(_frame);
                            if (sourceFrame != _frame)
                            {
                                _frameUnref!(_transferFrame);
                            }
                            return false;
                        }

                        var buffer = pool.Rent(size);
                        var lease = new WebRtcMediaFrameLease(buffer, size, l => pool.Return(l.Buffer, l.Size));
                        Buffer.MemoryCopy((void*)data0, (void*)buffer, size, size);
                        lease.ResetBgra(width, height, lineSize0);
                        decodedFrame = lease;

                        _frameUnref!(_frame);
                        if (sourceFrame != _frame)
                        {
                            _frameUnref!(_transferFrame);
                        }
                        return true;
                    }
                    else
                    {
                        // Unrecognized format or un-transferable hardware frame
                        _frameUnref!(_frame);
                        if (sourceFrame != _frame)
                        {
                            _frameUnref!(_transferFrame);
                        }
                        return false;
                    }
                }
            }
        }
    }

    private bool EnsureCodecContext(VideoCodecsEnum codec)
    {
        if (_codecContext != IntPtr.Zero && _currentCodec == codec)
        {
            return true;
        }

        if (_codecContext != IntPtr.Zero)
        {
            _freeContext!(ref _codecContext);
            _codecContext = IntPtr.Zero;
        }

        IsHardwareAccelerated = false;

        var codecName = codec switch
        {
            VideoCodecsEnum.H264 => "h264",
            VideoCodecsEnum.H265 => "hevc",
            VideoCodecsEnum.VP8 => "vp8",
            VideoCodecsEnum.VP9 => "vp9",
            VideoCodecsEnum.AV1 => "av1",
            _ => null
        };

        if (codecName is null)
        {
            return false;
        }

        var pCodec = _findDecoderByName!(codecName);
        if (pCodec == IntPtr.Zero)
        {
            return false;
        }

        var ctx = _allocContext3!(pCodec);
        if (ctx == IntPtr.Zero)
        {
            return false;
        }

        // Configure D3D11VA hardware acceleration on Windows if requested
        if (DecodingPolicy != MediaVideoDecodingPolicy.SoftwareOnly &&
            OperatingSystem.IsWindows() &&
            _hwDeviceCtxCreate is not null &&
            _codecGetHwConfig is not null)
        {
            try
            {
                var devType = _hwDeviceFindTypeByName?.Invoke("d3d11va") ?? 7;
                var hwPixFmt = FindHardwarePixelFormat(pCodec, devType);
                if (hwPixFmt >= 0)
                {
                    if (_hwDeviceCtx == IntPtr.Zero)
                    {
                        var createRes = _hwDeviceCtxCreate(ref _hwDeviceCtx, devType, IntPtr.Zero, IntPtr.Zero, 0);
                        if (createRes < 0)
                        {
                            _hwDeviceCtx = IntPtr.Zero;
                        }
                    }

                    if (_hwDeviceCtx != IntPtr.Zero && _bufferRef is not null)
                    {
                        var devRef = _bufferRef(_hwDeviceCtx);
                        if (devRef != IntPtr.Zero)
                        {
                            _currentHwPixelFormat = hwPixFmt;
                            _getFormatHandler = SelectHardwareFormat;
                            _getFormatPointer = Marshal.GetFunctionPointerForDelegate(_getFormatHandler);

                            // FFmpeg 7/8 x64: offset 192 = get_format, offset 560 = hw_device_ctx
                            Marshal.WriteIntPtr(ctx, 192, _getFormatPointer);
                            Marshal.WriteIntPtr(ctx, 560, devRef);
                            IsHardwareAccelerated = true;
                        }
                    }
                }
            }
            catch
            {
                // Fallback to software if preferred
            }

            if (!IsHardwareAccelerated && DecodingPolicy == MediaVideoDecodingPolicy.HardwareRequired)
            {
                _freeContext!(ref ctx);
                throw new InvalidOperationException("Hardware decoding (D3D11VA) is required by policy, but failed to initialize.");
            }
        }

        var openResult = _open2!(ctx, pCodec, IntPtr.Zero);
        if (openResult < 0)
        {
            _freeContext!(ref ctx);
            if (IsHardwareAccelerated && DecodingPolicy == MediaVideoDecodingPolicy.HardwarePreferred)
            {
                // Fallback to pure software decoding
                IsHardwareAccelerated = false;
                ctx = _allocContext3!(pCodec);
                if (ctx != IntPtr.Zero && _open2!(ctx, pCodec, IntPtr.Zero) >= 0)
                {
                    _codecContext = ctx;
                    _currentCodec = codec;
                    return true;
                }
                if (ctx != IntPtr.Zero)
                {
                    _freeContext!(ref ctx);
                }
            }
            return false;
        }

        _codecContext = ctx;
        _currentCodec = codec;
        return true;
    }

    private void FallbackToSoftware(VideoCodecsEnum codec)
    {
        if (DecodingPolicy == MediaVideoDecodingPolicy.HardwareRequired)
        {
            return;
        }

        if (_codecContext != IntPtr.Zero)
        {
            _freeContext!(ref _codecContext);
            _codecContext = IntPtr.Zero;
        }

        IsHardwareAccelerated = false;

        var codecName = codec switch
        {
            VideoCodecsEnum.H264 => "h264",
            VideoCodecsEnum.H265 => "hevc",
            VideoCodecsEnum.VP8 => "vp8",
            VideoCodecsEnum.VP9 => "vp9",
            VideoCodecsEnum.AV1 => "av1",
            _ => null
        };

        if (codecName is null)
        {
            return;
        }

        var pCodec = _findDecoderByName!(codecName);
        if (pCodec == IntPtr.Zero)
        {
            return;
        }

        var ctx = _allocContext3!(pCodec);
        if (ctx == IntPtr.Zero)
        {
            return;
        }

        if (_open2!(ctx, pCodec, IntPtr.Zero) >= 0)
        {
            _codecContext = ctx;
            _currentCodec = codec;
        }
        else
        {
            _freeContext!(ref ctx);
        }
    }

    private int SelectHardwareFormat(IntPtr avctx, IntPtr formats)
    {
        if (formats == IntPtr.Zero)
        {
            return -1;
        }

        unsafe
        {
            var p = (int*)formats;
            while (*p != -1)
            {
                if (*p == _currentHwPixelFormat)
                {
                    return *p;
                }
                p++;
            }
            return *(int*)formats;
        }
    }

    private static int FindHardwarePixelFormat(IntPtr codec, int deviceType)
    {
        if (_codecGetHwConfig is null)
        {
            return -1;
        }

        for (var index = 0; ; index++)
        {
            var config = _codecGetHwConfig(codec, index);
            if (config == IntPtr.Zero)
            {
                return -1;
            }

            var pixelFormat = Marshal.ReadInt32(config, 0);
            var methods = Marshal.ReadInt32(config, sizeof(int));
            var devType = Marshal.ReadInt32(config, sizeof(int) * 2);

            // HardwareDeviceContextMethod = 0x01
            if (devType == deviceType && (methods & 0x01) != 0)
            {
                return pixelFormat;
            }
        }
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

            if (_codecContext != IntPtr.Zero)
            {
                _freeContext!(ref _codecContext);
                _codecContext = IntPtr.Zero;
            }

            if (_packet != IntPtr.Zero)
            {
                _packetFree!(ref _packet);
                _packet = IntPtr.Zero;
            }

            if (_frame != IntPtr.Zero)
            {
                _frameFree!(ref _frame);
                _frame = IntPtr.Zero;
            }

            if (_transferFrame != IntPtr.Zero)
            {
                _frameFree!(ref _transferFrame);
                _transferFrame = IntPtr.Zero;
            }

            if (_hwDeviceCtx != IntPtr.Zero && _bufferUnref is not null)
            {
                _bufferUnref(ref _hwDeviceCtx);
                _hwDeviceCtx = IntPtr.Zero;
            }
        }
    }

    private static bool ProbeNativeLibraries()
    {
        try
        {
            var avutilNames = new[] { "avutil-60", "avutil-59", "avutil-58", "avutil-57", "avutil", "libavutil.so.60", "libavutil.so.59", "libavutil.so" };
            var avcodecNames = new[] { "avcodec-62", "avcodec-61", "avcodec-60", "avcodec-59", "avcodec", "libavcodec.so.62", "libavcodec.so.61", "libavcodec.so" };

            var searchDirs = new List<string>
            {
                AppContext.BaseDirectory,
                Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\native\artifacts\runtimes\win-x64\native")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\native\artifacts\runtimes\win-x64\native"))
            };

            // 1. Try load avutil
            foreach (var name in avutilNames)
            {
                foreach (var dir in searchDirs)
                {
                    var file = Path.Combine(dir, OperatingSystem.IsWindows() ? $"{name}.dll" : name);
                    if (File.Exists(file) && NativeLibrary.TryLoad(file, out _avUtilHandle))
                    {
                        break;
                    }
                }

                if (_avUtilHandle != IntPtr.Zero || NativeLibrary.TryLoad(name, typeof(FfmpegWebRtcVideoDecoder).Assembly, null, out _avUtilHandle))
                {
                    break;
                }
            }

            if (_avUtilHandle == IntPtr.Zero)
            {
                return false;
            }

            // 2. Try load avcodec
            foreach (var name in avcodecNames)
            {
                foreach (var dir in searchDirs)
                {
                    var file = Path.Combine(dir, OperatingSystem.IsWindows() ? $"{name}.dll" : name);
                    if (File.Exists(file) && NativeLibrary.TryLoad(file, out _avCodecHandle))
                    {
                        break;
                    }
                }

                if (_avCodecHandle != IntPtr.Zero || NativeLibrary.TryLoad(name, typeof(FfmpegWebRtcVideoDecoder).Assembly, null, out _avCodecHandle))
                {
                    break;
                }
            }

            if (_avCodecHandle == IntPtr.Zero)
            {
                return false;
            }

            // Bind avcodec functions
            _findDecoderByName = Marshal.GetDelegateForFunctionPointer<AvCodecFindDecoderByName>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_find_decoder_by_name"));
            _allocContext3 = Marshal.GetDelegateForFunctionPointer<AvCodecAllocContext3>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_alloc_context3"));
            _open2 = Marshal.GetDelegateForFunctionPointer<AvCodecOpen2>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_open2"));
            _sendPacket = Marshal.GetDelegateForFunctionPointer<AvCodecSendPacket>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_send_packet"));
            _receiveFrame = Marshal.GetDelegateForFunctionPointer<AvCodecReceiveFrame>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_receive_frame"));
            _flushBuffers = Marshal.GetDelegateForFunctionPointer<AvCodecFlushBuffers>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_flush_buffers"));
            _freeContext = Marshal.GetDelegateForFunctionPointer<AvCodecFreeContext>(
                NativeLibrary.GetExport(_avCodecHandle, "avcodec_free_context"));
            _packetAlloc = Marshal.GetDelegateForFunctionPointer<AvPacketAlloc>(
                NativeLibrary.GetExport(_avCodecHandle, "av_packet_alloc"));
            _packetFree = Marshal.GetDelegateForFunctionPointer<AvPacketFree>(
                NativeLibrary.GetExport(_avCodecHandle, "av_packet_free"));

            if (NativeLibrary.TryGetExport(_avCodecHandle, "avcodec_get_hw_config", out var pGetHwConfig))
            {
                _codecGetHwConfig = Marshal.GetDelegateForFunctionPointer<AvCodecGetHardwareConfig>(pGetHwConfig);
            }

            // Bind avutil functions
            _frameAlloc = Marshal.GetDelegateForFunctionPointer<AvFrameAlloc>(
                NativeLibrary.GetExport(_avUtilHandle, "av_frame_alloc"));
            _frameFree = Marshal.GetDelegateForFunctionPointer<AvFrameFree>(
                NativeLibrary.GetExport(_avUtilHandle, "av_frame_free"));
            _frameUnref = Marshal.GetDelegateForFunctionPointer<AvFrameUnref>(
                NativeLibrary.GetExport(_avUtilHandle, "av_frame_unref"));
            if (NativeLibrary.TryGetExport(_avUtilHandle, "av_frame_clone", out var pClone))
            {
                _frameClone = Marshal.GetDelegateForFunctionPointer<AvFrameClone>(pClone);
            }

            // Bind hardware acceleration functions if present in avutil
            if (NativeLibrary.TryGetExport(_avUtilHandle, "av_hwdevice_ctx_create", out var pHwCreate))
            {
                _hwDeviceCtxCreate = Marshal.GetDelegateForFunctionPointer<AvHwDeviceCtxCreate>(pHwCreate);
            }
            if (NativeLibrary.TryGetExport(_avUtilHandle, "av_hwdevice_find_type_by_name", out var pHwFind))
            {
                _hwDeviceFindTypeByName = Marshal.GetDelegateForFunctionPointer<AvHwdeviceFindTypeByName>(pHwFind);
            }
            if (NativeLibrary.TryGetExport(_avUtilHandle, "av_buffer_ref", out var pBufRef))
            {
                _bufferRef = Marshal.GetDelegateForFunctionPointer<AvBufferRef>(pBufRef);
            }
            if (NativeLibrary.TryGetExport(_avUtilHandle, "av_buffer_unref", out var pBufUnref))
            {
                _bufferUnref = Marshal.GetDelegateForFunctionPointer<AvBufferUnref>(pBufUnref);
            }
            if (NativeLibrary.TryGetExport(_avUtilHandle, "av_hwframe_transfer_data", out var pTransfer))
            {
                _hwFrameTransferData = Marshal.GetDelegateForFunctionPointer<AvHwFrameTransferData>(pTransfer);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvCodecFindDecoderByName(string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvCodecAllocContext3(IntPtr codec);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecOpen2(IntPtr avctx, IntPtr codec, IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecSendPacket(IntPtr avctx, IntPtr pkt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecReceiveFrame(IntPtr avctx, IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvCodecFlushBuffers(IntPtr avctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvCodecFreeContext(ref IntPtr avctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvPacketAlloc();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvPacketFree(ref IntPtr pkt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvFrameAlloc();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvFrameFree(ref IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvFrameUnref(IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvCodecGetHardwareConfig(IntPtr codec, int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvCodecGetFormat(IntPtr avctx, IntPtr fmtPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvHwdeviceFindTypeByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvHwDeviceCtxCreate(
        ref IntPtr deviceCtx,
        int deviceType,
        IntPtr device,
        IntPtr opts,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvBufferRef(IntPtr buf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvBufferUnref(ref IntPtr buf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AvHwFrameTransferData(IntPtr dst, IntPtr src, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AvFrameClone(IntPtr src);

    private static AvFrameClone? _frameClone;
}
