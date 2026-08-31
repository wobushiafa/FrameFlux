using System.Globalization;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class DirectRtspSession(FFmpegApi api, bool packetReader) : IDisposable
{
    private const int MediaTypeVideo = 0;
    private const int MediaTypeAudio = 1;
    private const int ErrorAgain = -11;
    private const int ErrorEof = -541478725;
    private const int ErrorExit = -1414092869;
    private const int ErrorNoMemory = -12;
    private static readonly InterruptCallbackDelegate InterruptCallback = HandleInterrupt;

    private readonly FFmpegApi _api = api;
    private readonly bool _packetReader = packetReader;
    private GCHandle _interruptHandle;
    private IntPtr _formatContext;
    private IntPtr _codecContext;
    private IntPtr _packet;
    private IntPtr _decodeFrame;
    private IntPtr _scaleContext;
    private IntPtr _stream;
    private IntPtr _codecParameters;
    private IHardwareDecoderContext? _hardwareDecoder;
    private int _videoStreamIndex = -1;
    private readonly BoundedAudioFrameQueue _audioFrames = new();
    private IntPtr _audioCodecContext;
    private IntPtr _audioDecodeFrame;
    private IntPtr _audioStream;
    private int _audioStreamIndex = -1;
    private int _videoTimeBaseNumerator;
    private int _videoTimeBaseDenominator = 1;
    private int _audioTimeBaseNumerator;
    private int _audioTimeBaseDenominator = 1;
    private FFmpegAudioResampler? _audioResampler;
    private bool _cancelled;
    private bool _disposed;
    private bool _preserveHardwareFrames;

    internal string Error { get; private set; } = "Unknown FFmpeg error.";
    internal string VideoDecoderDiagnostics { get; private set; } = "Disabled";
    internal bool IsHardwareVideoDecodingActive => _hardwareDecoder is not null;
    internal long LastHardwareTransferTicks => _hardwareDecoder?.LastTransferTicks ?? 0;
    internal bool HasAudio => _audioCodecContext != IntPtr.Zero;

    internal int Open(in NativeRtspOptions options)
    {
        if (options.Url == IntPtr.Zero)
        {
            Error = "The RTSP URL is empty.";
            return -1;
        }

        VideoDecoderDiagnostics = options.UseHardwareAcceleration != 0
            ? $"{HardwareDecoderContextFactory.PlatformBackendName} requested"
            : "Disabled";
        _preserveHardwareFrames = options.PreserveHardwareFrames != 0;
        _formatContext = _api.AvFormatAllocContext();
        if (_formatContext == IntPtr.Zero)
        {
            return Fail(ErrorNoMemory, "avformat_alloc_context");
        }
        if (!TryInstallInterruptCallback())
        {
            return -1;
        }

        IntPtr dictionary = IntPtr.Zero;
        try
        {
            SetDictionary(ref dictionary, "rtsp_transport",
                Marshal.PtrToStringUTF8(options.Transport) ?? "tcp");
            SetTimeout(ref dictionary, "timeout", options.OpenTimeoutMilliseconds);
            SetTimeout(ref dictionary, "rw_timeout", options.ReadTimeoutMilliseconds);
            foreach (var option in FFmpegInputOptionPolicy.GetLowLatencyOptions(options.LowLatency != 0))
            {
                SetDictionary(ref dictionary, option.Key, option.Value);
            }

            var result = _api.AvFormatOpenInput(
                ref _formatContext,
                options.Url,
                IntPtr.Zero,
                ref dictionary);
            if (result < 0)
            {
                return Fail(result, "avformat_open_input");
            }

            result = _api.AvFormatFindStreamInfo(_formatContext, IntPtr.Zero);
            if (result < 0)
            {
                return Fail(result, "avformat_find_stream_info");
            }

            result = _api.AvFindBestStream(
                _formatContext,
                MediaTypeVideo,
                -1,
                -1,
                out var decoder,
                0);
            if (result < 0)
            {
                return Fail(result, "av_find_best_stream");
            }

            _videoStreamIndex = result;
            _stream = FFmpegAbi.GetStream(_formatContext, _videoStreamIndex);
            _codecParameters = FFmpegAbi.GetCodecParameters(_stream);
            if (_stream == IntPtr.Zero || _codecParameters == IntPtr.Zero)
            {
                return Fail(-1, "read video stream");
            }

            _packet = _api.AvPacketAlloc();
            if (_packet == IntPtr.Zero)
            {
                return Fail(ErrorNoMemory, "av_packet_alloc");
            }

            (_videoTimeBaseNumerator, _videoTimeBaseDenominator) = FFmpegAbi.GetTimeBase(_stream);
            if (options.EnableAudio != 0)
            {
                TryOpenAudioDecoder();
            }

            if (_packetReader)
            {
                return 0;
            }

            return OpenVideoDecoder(decoder, options);
        }
        finally
        {
            _api.AvDictFree(ref dictionary);
        }
    }

    internal NativeStreamInfo GetStreamInfo() =>
        FFmpegAbi.ReadStreamInfo(_stream, _codecParameters, _api.CodecMajorVersion);

    internal int Seek(long timestamp)
    {
        if (_packetReader || _formatContext == IntPtr.Zero || _codecContext == IntPtr.Zero)
        {
            Error = "This FFmpeg session does not support seeking.";
            return -1;
        }

        _api.AvPacketUnref(_packet);
        var result = _api.AvSeekFrame(_formatContext, _videoStreamIndex, timestamp, 1);
        if (result < 0)
        {
            return Fail(result, "av_seek_frame");
        }

        _api.AvCodecFlushBuffers(_codecContext);
        if (_audioCodecContext != IntPtr.Zero)
        {
            _api.AvCodecFlushBuffers(_audioCodecContext);
        }
        _api.AvFrameUnref(_decodeFrame);
        if (_audioDecodeFrame != IntPtr.Zero)
        {
            _api.AvFrameUnref(_audioDecodeFrame);
        }
        _audioFrames.Clear();
        return 0;
    }

    internal bool TryDequeueAudioFrame(out NativeAudioFrame? frame) =>
        _audioFrames.TryDequeue(out frame);

    internal NativeReadResult ReadFrame(out DirectVideoFrame? output)
    {
        output = null;
        if (_packetReader || _codecContext == IntPtr.Zero || _decodeFrame == IntPtr.Zero)
        {
            Error = "This FFmpeg session was not opened as a decoder.";
            return NativeReadResult.Error;
        }

        while (!Volatile.Read(ref _cancelled))
        {
            DrainAudioFrames();
            var receive = ReceiveFrame(out output);
            if (receive != NativeReadResult.Again)
            {
                return receive;
            }

            var result = _api.AvReadFrame(_formatContext, _packet);
            if (Volatile.Read(ref _cancelled))
            {
                return NativeReadResult.End;
            }
            if (result == ErrorEof || result == ErrorExit)
            {
                FlushAudioDecoder();
                _ = _api.AvCodecSendPacket(_codecContext, IntPtr.Zero);
                var flushed = ReceiveFrame(out output);
                return flushed == NativeReadResult.Again ? NativeReadResult.End : flushed;
            }

            if (result < 0)
            {
                return FailRead(result, "av_read_frame");
            }

            if (FFmpegAbi.GetPacketStreamIndex(_packet) != _videoStreamIndex)
            {
                if (FFmpegAbi.GetPacketStreamIndex(_packet) == _audioStreamIndex)
                {
                    SendAudioPacket(_packet);
                }
                _api.AvPacketUnref(_packet);
                continue;
            }

            result = _api.AvCodecSendPacket(_codecContext, _packet);
            _api.AvPacketUnref(_packet);
            if (result < 0 && result != ErrorAgain)
            {
                return FailRead(result, "avcodec_send_packet");
            }
        }

        return NativeReadResult.End;
    }

    internal NativeReadResult ReadPacket(out DirectVideoPacket? output)
    {
        output = null;
        if (!_packetReader)
        {
            Error = "This FFmpeg session was not opened as a packet reader.";
            return NativeReadResult.Error;
        }

        while (!Volatile.Read(ref _cancelled))
        {
            var result = _api.AvReadFrame(_formatContext, _packet);
            if (Volatile.Read(ref _cancelled))
            {
                return NativeReadResult.End;
            }
            if (result == ErrorEof || result == ErrorExit)
            {
                FlushAudioDecoder();
                return NativeReadResult.End;
            }

            if (result < 0)
            {
                return FailRead(result, "av_read_frame");
            }

            if (FFmpegAbi.GetPacketStreamIndex(_packet) != _videoStreamIndex)
            {
                if (FFmpegAbi.GetPacketStreamIndex(_packet) == _audioStreamIndex)
                {
                    SendAudioPacket(_packet);
                }
                _api.AvPacketUnref(_packet);
                continue;
            }

            var clone = _api.AvPacketClone(_packet);
            _api.AvPacketUnref(_packet);
            if (clone == IntPtr.Zero)
            {
                return FailRead(ErrorNoMemory, "av_packet_clone");
            }

            output = new DirectVideoPacket(_api, clone);
            return NativeReadResult.Ok;
        }

        return NativeReadResult.End;
    }

    internal void Cancel() => Volatile.Write(ref _cancelled, true);

    internal unsafe int CopyFrameToBgra(
        DirectVideoFrame frame,
        IntPtr destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int scaleQuality,
        bool forceOpaqueAlpha)
    {
        var sourceFrame = frame.Pointer;
        if (frame.IsDmaBuf)
        {
            var transferResult = _hardwareDecoder!.Transfer(frame.Pointer, out sourceFrame);
            if (transferResult < 0)
            {
                return transferResult;
            }
        }
        var layout = FFmpegAbi.ReadFrame(sourceFrame, _api.UtilMajorVersion);
        var flags = scaleQuality == 0 ? 1 : scaleQuality == 2 ? 4 : 2;
        _scaleContext = _api.SwsGetCachedContext(
            _scaleContext,
            layout.Width,
            layout.Height,
            layout.Format,
            destinationWidth,
            destinationHeight,
            FFmpegAbi.PixelFormatBgra,
            flags,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_scaleContext == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr* sourceData = stackalloc IntPtr[4];
        int* sourceStride = stackalloc int[4];
        IntPtr* destinationData = stackalloc IntPtr[4];
        int* destinationStrides = stackalloc int[4];
        for (var index = 0; index < 4; index++)
        {
            sourceData[index] = layout.Data[index];
            sourceStride[index] = layout.LineSize[index];
            destinationData[index] = IntPtr.Zero;
            destinationStrides[index] = 0;
        }

        destinationData[0] = destination;
        destinationStrides[0] = destinationStride;
        var result = _api.SwsScale(
            _scaleContext,
            (IntPtr)sourceData,
            (IntPtr)sourceStride,
            0,
            layout.Height,
            (IntPtr)destinationData,
            (IntPtr)destinationStrides);
        if (result < 0)
        {
            return result;
        }

        if (forceOpaqueAlpha)
        {
            var pixels = (byte*)destination;
            for (var y = 0; y < destinationHeight; y++)
            {
                var row = pixels + y * destinationStride;
                for (var x = 0; x < destinationWidth; x++)
                {
                    row[x * 4 + 3] = 255;
                }
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _cancelled, true);
        _audioResampler?.Dispose();
        _audioResampler = null;
        if (_scaleContext != IntPtr.Zero)
        {
            _api.SwsFreeContext(_scaleContext);
            _scaleContext = IntPtr.Zero;
        }
        if (_audioDecodeFrame != IntPtr.Zero) _api.AvFrameFree(ref _audioDecodeFrame);
        if (_audioCodecContext != IntPtr.Zero) _api.AvCodecFreeContext(ref _audioCodecContext);
        if (_decodeFrame != IntPtr.Zero) _api.AvFrameFree(ref _decodeFrame);
        if (_packet != IntPtr.Zero) _api.AvPacketFree(ref _packet);
        if (_codecContext != IntPtr.Zero) _api.AvCodecFreeContext(ref _codecContext);
        _hardwareDecoder?.Dispose();
        _hardwareDecoder = null;
        if (_formatContext != IntPtr.Zero) _api.AvFormatCloseInput(ref _formatContext);
        if (_interruptHandle.IsAllocated) _interruptHandle.Free();
    }

    internal static int CalculateInterruptCallbackOffset(
        int fpsProbeSizeOffset,
        int errorRecognitionOffset,
        int pointerSize)
    {
        if (pointerSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        }
        if (fpsProbeSizeOffset < 0 || errorRecognitionOffset != fpsProbeSizeOffset + sizeof(int))
        {
            throw new InvalidOperationException(
                "The loaded FFmpeg AVFormatContext layout is not supported.");
        }

        var firstByteAfterErrorRecognition = errorRecognitionOffset + sizeof(int);
        return (firstByteAfterErrorRecognition + pointerSize - 1) & ~(pointerSize - 1);
    }

    private bool TryInstallInterruptCallback()
    {
        using var fpsProbeSizeName = new NativeUtf8String("fpsprobesize");
        using var errorRecognitionName = new NativeUtf8String("f_err_detect");
        var fpsProbeSizeOption = _api.AvOptFind(
            _formatContext,
            fpsProbeSizeName.Pointer,
            IntPtr.Zero,
            0,
            0);
        var errorRecognitionOption = _api.AvOptFind(
            _formatContext,
            errorRecognitionName.Pointer,
            IntPtr.Zero,
            0,
            0);
        if (fpsProbeSizeOption == IntPtr.Zero || errorRecognitionOption == IntPtr.Zero)
        {
            Error = "Unable to locate the FFmpeg AVFormatContext interrupt callback layout.";
            return false;
        }

        try
        {
            var optionOffsetField = IntPtr.Size * 2;
            var interruptCallbackOffset = CalculateInterruptCallbackOffset(
                Marshal.ReadInt32(fpsProbeSizeOption, optionOffsetField),
                Marshal.ReadInt32(errorRecognitionOption, optionOffsetField),
                IntPtr.Size);
            _interruptHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            Marshal.WriteIntPtr(
                _formatContext,
                interruptCallbackOffset,
                Marshal.GetFunctionPointerForDelegate(InterruptCallback));
            Marshal.WriteIntPtr(
                _formatContext,
                interruptCallbackOffset + IntPtr.Size,
                GCHandle.ToIntPtr(_interruptHandle));
            return true;
        }
        catch (Exception exception)
        {
            if (_interruptHandle.IsAllocated) _interruptHandle.Free();
            Error = $"Unable to configure FFmpeg I/O cancellation: {exception.Message}";
            return false;
        }
    }

    private static int HandleInterrupt(IntPtr opaque)
    {
        try
        {
            return opaque != IntPtr.Zero &&
                   GCHandle.FromIntPtr(opaque).Target is DirectRtspSession session &&
                   Volatile.Read(ref session._cancelled)
                ? 1
                : 0;
        }
        catch
        {
            return 1;
        }
    }

    private NativeReadResult ReceiveFrame(out DirectVideoFrame? output)
    {
        output = null;
        var result = _api.AvCodecReceiveFrame(_codecContext, _decodeFrame);
        if (result == ErrorAgain) return NativeReadResult.Again;
        if (result == ErrorEof) return NativeReadResult.End;
        if (result < 0) return FailRead(result, "avcodec_receive_frame");

        var source = _decodeFrame;
        var isHardwareFrame =
            _hardwareDecoder is not null && _hardwareDecoder.IsHardwareFrame(_decodeFrame);
        var preserveHardwareFrame =
            isHardwareFrame &&
            _preserveHardwareFrames &&
            _hardwareDecoder!.SupportsDirectFrameOutput;
        var isDmaBuf = false;
        if (preserveHardwareFrame && OperatingSystem.IsLinux())
        {
            result = _hardwareDecoder!.Export(_decodeFrame, out source);
            if (result < 0)
            {
                _preserveHardwareFrames = false;
                VideoDecoderDiagnostics +=
                    "; DRM PRIME export unavailable, using CPU frame transfer";
                result = _hardwareDecoder.Transfer(_decodeFrame, out source);
                if (result < 0)
                {
                    _api.AvFrameUnref(_decodeFrame);
                    return FailRead(result, "av_hwframe_transfer_data");
                }
            }
            else
            {
                isDmaBuf = true;
            }
        }
        else if (isHardwareFrame && !preserveHardwareFrame)
        {
            result = _hardwareDecoder!.Transfer(_decodeFrame, out source);
            if (result < 0)
            {
                _api.AvFrameUnref(_decodeFrame);
                return FailRead(result, "av_hwframe_transfer_data");
            }
        }

        var clone = isDmaBuf ? source : _api.AvFrameClone(source);
        _api.AvFrameUnref(_decodeFrame);
        if (clone == IntPtr.Zero)
        {
            return FailRead(ErrorNoMemory, "av_frame_clone");
        }

        output = new DirectVideoFrame(
            _api,
            clone,
            _videoTimeBaseNumerator,
            _videoTimeBaseDenominator,
            preserveHardwareFrame && OperatingSystem.IsWindows(),
            isDmaBuf);
        return NativeReadResult.Ok;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InterruptCallbackDelegate(IntPtr opaque);

    private int OpenVideoDecoder(IntPtr decoder, in NativeRtspOptions options)
    {
        var result = AllocateVideoDecoder(decoder);
        if (result < 0) return result;

        if (options.UseHardwareAcceleration == 0)
        {
            result = _api.AvCodecOpen2(_codecContext, decoder, IntPtr.Zero);
            return result < 0 ? Fail(result, "avcodec_open2") : 0;
        }

        var backendName = HardwareDecoderContextFactory.PlatformBackendName;
        if (!HardwareDecoderContextFactory.TryCreate(
                _api,
                decoder,
                _codecContext,
                out _hardwareDecoder,
                out var hardwareError))
        {
            if (options.FallbackToSoftware == 0)
            {
                Error = $"{backendName} initialization failed: {hardwareError}.";
                return -1;
            }

            VideoDecoderDiagnostics =
                $"{backendName} unavailable: {hardwareError}; software fallback";
            result = _api.AvCodecOpen2(_codecContext, decoder, IntPtr.Zero);
            return result < 0 ? Fail(result, "avcodec_open2") : 0;
        }

        result = _api.AvCodecOpen2(_codecContext, decoder, IntPtr.Zero);
        if (result >= 0)
        {
            VideoDecoderDiagnostics =
                _preserveHardwareFrames && _hardwareDecoder!.SupportsDirectFrameOutput
                    ? $"{_hardwareDecoder.BackendName} active (zero-copy texture output)"
                    : $"{_hardwareDecoder!.BackendName} active (hardware frames transferred to system memory)";
            return 0;
        }

        var openError = _api.FormatError(result);
        if (options.FallbackToSoftware == 0)
        {
            Error = $"{backendName} decoder initialization failed: {openError} ({result}).";
            return result;
        }

        _api.AvFrameFree(ref _decodeFrame);
        _api.AvCodecFreeContext(ref _codecContext);
        _hardwareDecoder!.Dispose();
        _hardwareDecoder = null;
        VideoDecoderDiagnostics =
            $"{backendName} decoder initialization failed: {openError} ({result}); software fallback";

        result = AllocateVideoDecoder(decoder);
        if (result < 0) return result;
        result = _api.AvCodecOpen2(_codecContext, decoder, IntPtr.Zero);
        return result < 0 ? Fail(result, "avcodec_open2 software fallback") : 0;
    }

    private int AllocateVideoDecoder(IntPtr decoder)
    {
        _codecContext = _api.AvCodecAllocContext3(decoder);
        _decodeFrame = _api.AvFrameAlloc();
        if (_codecContext == IntPtr.Zero || _decodeFrame == IntPtr.Zero)
            return Fail(ErrorNoMemory, "allocate decoder state");

        var result = _api.AvCodecParametersToContext(_codecContext, _codecParameters);
        return result < 0 ? Fail(result, "avcodec_parameters_to_context") : 0;
    }

    private void TryOpenAudioDecoder()
    {
        var result = _api.AvFindBestStream(
            _formatContext,
            MediaTypeAudio,
            -1,
            _videoStreamIndex,
            out var decoder,
            0);
        if (result < 0)
        {
            return;
        }

        _audioStreamIndex = result;
        _audioStream = FFmpegAbi.GetStream(_formatContext, result);
        var parameters = FFmpegAbi.GetCodecParameters(_audioStream);
        if (_audioStream == IntPtr.Zero || parameters == IntPtr.Zero)
        {
            _audioStreamIndex = -1;
            return;
        }

        _audioCodecContext = _api.AvCodecAllocContext3(decoder);
        _audioDecodeFrame = _api.AvFrameAlloc();
        if (_audioCodecContext == IntPtr.Zero || _audioDecodeFrame == IntPtr.Zero)
        {
            CloseAudioDecoder();
            return;
        }

        result = _api.AvCodecParametersToContext(_audioCodecContext, parameters);
        if (result < 0 || _api.AvCodecOpen2(_audioCodecContext, decoder, IntPtr.Zero) < 0)
        {
            CloseAudioDecoder();
            return;
        }

        (_audioTimeBaseNumerator, _audioTimeBaseDenominator) = FFmpegAbi.GetTimeBase(_audioStream);
        try
        {
            _audioResampler = new FFmpegAudioResampler(_api, _audioCodecContext);
        }
        catch
        {
            CloseAudioDecoder();
        }
    }

    private void SendAudioPacket(IntPtr packet)
    {
        if (_audioCodecContext == IntPtr.Zero)
        {
            return;
        }

        var result = _api.AvCodecSendPacket(_audioCodecContext, packet);
        if (result >= 0 || result == ErrorAgain)
        {
            DrainAudioFrames();
        }
    }

    private void DrainAudioFrames()
    {
        if (_audioCodecContext == IntPtr.Zero ||
            _audioDecodeFrame == IntPtr.Zero ||
            _audioResampler is null)
        {
            return;
        }

        while (true)
        {
            var result = _api.AvCodecReceiveFrame(_audioCodecContext, _audioDecodeFrame);
            if (result == ErrorAgain || result == ErrorEof)
            {
                return;
            }
            if (result < 0)
            {
                return;
            }

            try
            {
                var layout = FFmpegAbi.ReadFrame(_audioDecodeFrame, _api.UtilMajorVersion);
                if (layout.SampleCount > 0)
                {
                    _audioFrames.Enqueue(_audioResampler.Convert(
                        _audioDecodeFrame,
                        layout,
                        _audioTimeBaseNumerator,
                        _audioTimeBaseDenominator));
                }
            }
            finally
            {
                _api.AvFrameUnref(_audioDecodeFrame);
            }
        }
    }

    private void CloseAudioDecoder()
    {
        _audioResampler?.Dispose();
        _audioResampler = null;
        if (_audioDecodeFrame != IntPtr.Zero) _api.AvFrameFree(ref _audioDecodeFrame);
        if (_audioCodecContext != IntPtr.Zero) _api.AvCodecFreeContext(ref _audioCodecContext);
        _audioStreamIndex = -1;
        _audioStream = IntPtr.Zero;
    }

    private void FlushAudioDecoder()
    {
        if (_audioCodecContext == IntPtr.Zero)
        {
            return;
        }

        var result = _api.AvCodecSendPacket(_audioCodecContext, IntPtr.Zero);
        if (result >= 0 || result == ErrorEof)
        {
            DrainAudioFrames();
        }
    }

    private void SetTimeout(ref IntPtr dictionary, string key, int milliseconds)
    {
        if (milliseconds > 0)
        {
            SetDictionary(
                ref dictionary,
                key,
                checked((long)milliseconds * 1000).ToString(CultureInfo.InvariantCulture));
        }
    }

    private void SetDictionary(ref IntPtr dictionary, string key, string value)
    {
        using var nativeKey = new NativeUtf8String(key);
        using var nativeValue = new NativeUtf8String(value);
        _ = _api.AvDictSet(ref dictionary, nativeKey.Pointer, nativeValue.Pointer, 0);
    }

    private int Fail(int error, string operation)
    {
        Error = $"{operation}: {_api.FormatError(error)} ({error})";
        return error;
    }

    private NativeReadResult FailRead(int error, string operation)
    {
        Fail(error, operation);
        return NativeReadResult.Error;
    }
}

internal sealed class DirectVideoFrame(
    FFmpegApi api,
    IntPtr pointer,
    int timeBaseNumerator,
    int timeBaseDenominator,
    bool isD3D11Texture = false,
    bool isDmaBuf = false) : IDisposable
{
    private readonly FFmpegApi _api = api;
    internal IntPtr Pointer { get; private set; } = pointer;
    internal bool IsDmaBuf => isDmaBuf;

    internal NativeFrameInfo GetInfo()
    {
        var frame = FFmpegAbi.ReadFrame(Pointer, _api.UtilMajorVersion);
        if (isD3D11Texture)
        {
            return new NativeFrameInfo
            {
                Width = frame.Width,
                Height = frame.Height,
                PixelFormat = NativePixelFormat.D3D11Texture,
                Plane0 = frame.Data[0],
                Plane1 = frame.Data[1],
                PresentationTimestamp = frame.PresentationTimestamp,
                TimeBaseNumerator = timeBaseNumerator,
                TimeBaseDenominator = timeBaseDenominator
            };
        }

        if (isDmaBuf)
        {
            return new NativeFrameInfo
            {
                Width = frame.Width,
                Height = frame.Height,
                PixelFormat = NativePixelFormat.DmaBuf,
                DmaBufDescriptor = frame.Data[0],
                PresentationTimestamp = frame.PresentationTimestamp,
                TimeBaseNumerator = timeBaseNumerator,
                TimeBaseDenominator = timeBaseDenominator
            };
        }

        return new NativeFrameInfo
        {
            Width = frame.Width,
            Height = frame.Height,
            PixelFormat = FFmpegAbi.MapPixelFormat(frame.Format),
            Plane0 = frame.Data[0],
            Plane1 = frame.Data[1],
            Plane2 = frame.Data[2],
            Stride0 = frame.LineSize[0],
            Stride1 = frame.LineSize[1],
            Stride2 = frame.LineSize[2],
            PresentationTimestamp = frame.PresentationTimestamp,
            TimeBaseNumerator = timeBaseNumerator,
            TimeBaseDenominator = timeBaseDenominator
        };
    }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            var frame = Pointer;
            _api.AvFrameFree(ref frame);
            Pointer = IntPtr.Zero;
        }
    }
}

internal sealed class DirectVideoPacket(FFmpegApi api, IntPtr pointer) : IDisposable
{
    private readonly FFmpegApi _api = api;
    internal IntPtr Pointer { get; private set; } = pointer;

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            var packet = Pointer;
            _api.AvPacketFree(ref packet);
            Pointer = IntPtr.Zero;
        }
    }
}

internal static class FFmpegAbi
{
    internal const int PixelFormatBgra = 28;

    internal static IntPtr GetStream(IntPtr formatContext, int index)
    {
        if (formatContext == IntPtr.Zero || index < 0)
        {
            return IntPtr.Zero;
        }

        var streamCountOffset = IntPtr.Size * 5 + sizeof(int);
        var streamCount = Marshal.ReadInt32(formatContext, streamCountOffset);
        if ((uint)index >= (uint)streamCount)
        {
            return IntPtr.Zero;
        }

        var streamsOffset = Align(streamCountOffset + sizeof(int), IntPtr.Size);
        var streams = Marshal.ReadIntPtr(formatContext, streamsOffset);
        return streams == IntPtr.Zero
            ? IntPtr.Zero
            : Marshal.ReadIntPtr(streams, index * IntPtr.Size);
    }

    internal static IntPtr GetCodecParameters(IntPtr stream)
    {
        var codecParametersOffset = Align(IntPtr.Size + sizeof(int) * 2, IntPtr.Size);
        return stream == IntPtr.Zero
            ? IntPtr.Zero
            : Marshal.ReadIntPtr(stream, codecParametersOffset);
    }

    internal static NativeStreamInfo ReadStreamInfo(
        IntPtr stream,
        IntPtr codecParameters,
        int codecMajorVersion)
    {
        var extraDataOffset = Align(12, IntPtr.Size);
        var extraData = Marshal.ReadIntPtr(codecParameters, extraDataOffset);
        var extraDataSize = Marshal.ReadInt32(codecParameters, extraDataOffset + IntPtr.Size);
        if (extraDataSize is < 0 or > 16 * 1024 * 1024)
        {
            extraData = IntPtr.Zero;
            extraDataSize = 0;
        }

        var widthOffset = GetCodecParametersWidthOffset(codecMajorVersion);
        var timeBase = GetTimeBase(stream);
        return new NativeStreamInfo
        {
            Width = Marshal.ReadInt32(codecParameters, widthOffset),
            Height = Marshal.ReadInt32(codecParameters, widthOffset + sizeof(int)),
            Codec = Marshal.ReadInt32(codecParameters, sizeof(int)) switch
            {
                27 => NativeVideoCodec.H264,
                173 => NativeVideoCodec.Hevc,
                _ => NativeVideoCodec.Unknown
            },
            CodecExtraData = extraData,
            CodecExtraDataSize = extraDataSize,
            TimeBaseNumerator = timeBase.Numerator,
            TimeBaseDenominator = timeBase.Denominator,
            StartTimestamp = GetStreamStartTimestamp(stream),
            DurationTimestamp = GetStreamDuration(stream)
        };
    }

    internal static (int Numerator, int Denominator) GetTimeBase(IntPtr stream)
    {
        var codecParametersOffset = Align(IntPtr.Size + sizeof(int) * 2, IntPtr.Size);
        var timeBaseOffset = codecParametersOffset + IntPtr.Size * 2;
        return (
            Marshal.ReadInt32(stream, timeBaseOffset),
            Math.Max(1, Marshal.ReadInt32(stream, timeBaseOffset + sizeof(int))));
    }

    internal static long GetStreamDuration(IntPtr stream)
    {
        if (stream == IntPtr.Zero)
        {
            return 0;
        }

        var codecParametersOffset = Align(IntPtr.Size + sizeof(int) * 2, IntPtr.Size);
        var timeBaseOffset = codecParametersOffset + IntPtr.Size * 2;
        return Marshal.ReadInt64(
            stream,
            Align(timeBaseOffset + sizeof(int) * 2, sizeof(long)) + sizeof(long));
    }

    internal static long GetStreamStartTimestamp(IntPtr stream)
    {
        if (stream == IntPtr.Zero)
        {
            return long.MinValue;
        }

        var codecParametersOffset = Align(IntPtr.Size + sizeof(int) * 2, IntPtr.Size);
        var timeBaseOffset = codecParametersOffset + IntPtr.Size * 2;
        return Marshal.ReadInt64(
            stream,
            Align(timeBaseOffset + sizeof(int) * 2, sizeof(long)));
    }

    internal static int GetPacketStreamIndex(IntPtr packet)
    {
        var ptsOffset = GetPacketPtsOffset();
        var dataOffset = ptsOffset + sizeof(long) * 2;
        return Marshal.ReadInt32(packet, dataOffset + IntPtr.Size + sizeof(int));
    }

    internal static NativePacketInfo ReadPacket(IntPtr packet)
    {
        var ptsOffset = GetPacketPtsOffset();
        var dataOffset = ptsOffset + sizeof(long) * 2;
        var sizeOffset = dataOffset + IntPtr.Size;
        return new NativePacketInfo
        {
            Data = Marshal.ReadIntPtr(packet, dataOffset),
            Size = Marshal.ReadInt32(packet, sizeOffset),
            PresentationTimestamp = Marshal.ReadInt64(packet, ptsOffset),
            DecodeTimestamp = Marshal.ReadInt64(packet, ptsOffset + sizeof(long)),
            Flags = Marshal.ReadInt32(packet, sizeOffset + sizeof(int) * 2)
        };
    }

    internal static FrameLayout ReadFrame(IntPtr frame, int utilMajorVersion)
    {
        var data = new IntPtr[4];
        var lineSize = new int[4];
        var lineSizeOffset = IntPtr.Size * 8;
        for (var index = 0; index < 4; index++)
        {
            data[index] = Marshal.ReadIntPtr(frame, index * IntPtr.Size);
            lineSize[index] = Marshal.ReadInt32(frame, lineSizeOffset + index * sizeof(int));
        }

        var extendedDataOffset = lineSizeOffset + sizeof(int) * 8;
        var widthOffset = extendedDataOffset + IntPtr.Size;
        var formatOffset = widthOffset + sizeof(int) * 3;
        var ptsCursor = formatOffset + sizeof(int);
        if (utilMajorVersion <= 58)
        {
            ptsCursor += sizeof(int);
        }
        ptsCursor += sizeof(int) + sizeof(int) * 2;
        var ptsOffset = Align(ptsCursor, NativeInt64Alignment);
        return new FrameLayout(
            Marshal.ReadInt32(frame, widthOffset),
            Marshal.ReadInt32(frame, widthOffset + sizeof(int)),
            Marshal.ReadInt32(frame, formatOffset),
            Marshal.ReadInt32(frame, widthOffset + sizeof(int) * 2),
            Marshal.ReadIntPtr(frame, extendedDataOffset),
            Marshal.ReadInt64(frame, ptsOffset),
            data,
            lineSize);
    }

    internal static void WriteFrameFormat(IntPtr frame, int utilMajorVersion, int format)
    {
        var lineSizeOffset = IntPtr.Size * 8;
        var extendedDataOffset = lineSizeOffset + sizeof(int) * 8;
        var widthOffset = extendedDataOffset + IntPtr.Size;
        var formatOffset = widthOffset + sizeof(int) * 3;
        Marshal.WriteInt32(frame, formatOffset, format);
    }

    internal static bool SupportsHardwareDecoderLayout(int codecMajorVersion) =>
        IntPtr.Size == 8 && codecMajorVersion is 61 or 62;

    internal static int ReadFrameFormat(IntPtr frame, int codecMajorVersion)
    {
        var layout = GetHardwareDecoderLayout(codecMajorVersion);
        return Marshal.ReadInt32(frame, layout.FrameFormatOffset);
    }

    internal static HardwareDecoderConfig ReadHardwareConfig(IntPtr config) =>
        new(
            Marshal.ReadInt32(config, 0),
            Marshal.ReadInt32(config, sizeof(int)),
            Marshal.ReadInt32(config, sizeof(int) * 2));

    internal static void ConfigureHardwareDecoderCodecContext(
        IntPtr codecContext,
        IntPtr hardwareDeviceContext,
        IntPtr getFormatCallback,
        int codecMajorVersion)
    {
        var layout = GetHardwareDecoderLayout(codecMajorVersion);
        Marshal.WriteIntPtr(codecContext, layout.GetFormatOffset, getFormatCallback);
        Marshal.WriteIntPtr(codecContext, layout.HardwareDeviceContextOffset, hardwareDeviceContext);
    }

    private static HardwareDecoderAbiLayout GetHardwareDecoderLayout(
        int codecMajorVersion)
    {
        if (!SupportsHardwareDecoderLayout(codecMajorVersion))
        {
            throw new NotSupportedException(
                $"The hardware decoder ABI is only validated for FFmpeg 7 and 8 x64; " +
                $"found avcodec {codecMajorVersion}.");
        }

        // Generated from the matching FFmpeg 7/8 public headers with offsetof.
        return new HardwareDecoderAbiLayout(
            GetFormatOffset: 192,
            HardwareDeviceContextOffset: 560,
            FrameFormatOffset: 116);
    }

    private readonly record struct HardwareDecoderAbiLayout(
        int GetFormatOffset,
        int HardwareDeviceContextOffset,
        int FrameFormatOffset);

    internal static NativePixelFormat MapPixelFormat(int pixelFormat) => pixelFormat switch
    {
        0 => NativePixelFormat.Yuv420P,
        23 => NativePixelFormat.Nv12,
        24 => NativePixelFormat.Nv21,
        26 => NativePixelFormat.Rgba32,
        PixelFormatBgra => NativePixelFormat.Bgra32,
        _ => NativePixelFormat.Unknown
    };

    private static int GetCodecParametersWidthOffset(int codecMajorVersion)
    {
        var extraDataOffset = Align(12, IntPtr.Size);
        var extraDataSizeOffset = extraDataOffset + IntPtr.Size;
        var formatOffset = extraDataSizeOffset + sizeof(int);
        if (codecMajorVersion >= 61)
        {
            var sideDataOffset = Align(formatOffset, IntPtr.Size);
            var sideDataCountOffset = sideDataOffset + IntPtr.Size;
            formatOffset = sideDataCountOffset + sizeof(int);
        }

        var bitRateOffset = Align(formatOffset + sizeof(int), NativeInt64Alignment);
        return bitRateOffset + sizeof(long) + sizeof(int) * 4;
    }

    private static int GetPacketPtsOffset() => Align(IntPtr.Size, NativeInt64Alignment);

    private static int NativeInt64Alignment =>
        IntPtr.Size == 8 ||
        OperatingSystem.IsWindows() ||
        RuntimeInformation.ProcessArchitecture == Architecture.Arm
            ? 8
            : 4;

    private static int Align(int value, int alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}

internal sealed record FrameLayout(
    int Width,
    int Height,
    int Format,
    int SampleCount,
    IntPtr ExtendedData,
    long PresentationTimestamp,
    IntPtr[] Data,
    int[] LineSize);
