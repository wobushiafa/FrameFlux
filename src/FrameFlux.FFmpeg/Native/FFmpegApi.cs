using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class FFmpegApi
{
    private static readonly Lazy<FFmpegApi> LazyInstance = new(() => new FFmpegApi());

    private FFmpegApi()
    {
        _hardwareApi = new Lazy<FFmpegHardwareApi>(
            static () => new FFmpegHardwareApi(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        AvCodecVersion = Load<VersionDelegate>("avcodec", "avcodec_version");
        AvFormatVersion = Load<VersionDelegate>("avformat", "avformat_version");
        AvUtilVersion = Load<VersionDelegate>("avutil", "avutil_version");
        SwScaleVersion = Load<VersionDelegate>("swscale", "swscale_version");
        SwResampleVersion = Load<VersionDelegate>("swresample", "swresample_version");
        AvFilterVersion = Load<VersionDelegate>("avfilter", "avfilter_version");
        AvFormatNetworkInit = Load<NetworkInitDelegate>("avformat", "avformat_network_init");
        AvGuessFrameRate = Load<GuessFrameRateDelegate>("avformat", "av_guess_frame_rate");
        AvFormatAllocContext = Load<AllocDelegate>("avformat", "avformat_alloc_context");
        AvFormatOpenInput = Load<FormatOpenInputDelegate>("avformat", "avformat_open_input");
        AvFormatFindStreamInfo = Load<FormatFindStreamInfoDelegate>("avformat", "avformat_find_stream_info");
        AvFindBestStream = Load<FindBestStreamDelegate>("avformat", "av_find_best_stream");
        AvReadFrame = Load<ReadFrameDelegate>("avformat", "av_read_frame");
        AvSeekFrame = Load<SeekFrameDelegate>("avformat", "av_seek_frame");
        AvFormatCloseInput = Load<FormatCloseInputDelegate>("avformat", "avformat_close_input");
        AvDictSet = Load<DictSetDelegate>("avutil", "av_dict_set");
        AvDictFree = Load<DictFreeDelegate>("avutil", "av_dict_free");
        AvStrError = Load<StrErrorDelegate>("avutil", "av_strerror");
        AvOptFind = Load<OptFindDelegate>("avutil", "av_opt_find");
        AvOptGetInt = Load<OptGetIntDelegate>("avutil", "av_opt_get_int");
        AvOptGetChannelLayout = Load<OptGetChannelLayoutDelegate>("avutil", "av_opt_get_chlayout");
        AvChannelLayoutDefault = Load<ChannelLayoutDefaultDelegate>("avutil", "av_channel_layout_default");
        AvChannelLayoutUninit = Load<ChannelLayoutUninitDelegate>("avutil", "av_channel_layout_uninit");
        AvCodecAllocContext3 = Load<AllocContextDelegate>("avcodec", "avcodec_alloc_context3");
        AvCodecParametersToContext = Load<ParametersToContextDelegate>("avcodec", "avcodec_parameters_to_context");
        AvCodecOpen2 = Load<CodecOpenDelegate>("avcodec", "avcodec_open2");
        AvCodecSendPacket = Load<CodecPacketDelegate>("avcodec", "avcodec_send_packet");
        AvCodecReceiveFrame = Load<CodecFrameDelegate>("avcodec", "avcodec_receive_frame");
        AvCodecFlushBuffers = Load<CodecFlushBuffersDelegate>("avcodec", "avcodec_flush_buffers");
        AvCodecFreeContext = Load<FreeContextDelegate>("avcodec", "avcodec_free_context");
        AvPacketAlloc = Load<AllocDelegate>("avcodec", "av_packet_alloc");
        AvPacketClone = Load<CloneDelegate>("avcodec", "av_packet_clone");
        AvPacketUnref = Load<UnrefDelegate>("avcodec", "av_packet_unref");
        AvPacketFree = Load<FreeDelegate>("avcodec", "av_packet_free");
        AvFrameAlloc = Load<AllocDelegate>("avutil", "av_frame_alloc");
        AvFrameClone = Load<CloneDelegate>("avutil", "av_frame_clone");
        AvFrameCopyProperties = Load<FrameCopyPropertiesDelegate>("avutil", "av_frame_copy_props");
        AvFrameUnref = Load<UnrefDelegate>("avutil", "av_frame_unref");
        AvFrameFree = Load<FreeDelegate>("avutil", "av_frame_free");
        AvGetSampleFormatName = Load<GetSampleFormatNameDelegate>("avutil", "av_get_sample_fmt_name");
        AvChannelLayoutDescribe = Load<ChannelLayoutDescribeDelegate>("avutil", "av_channel_layout_describe");
        SwsGetCachedContext = Load<SwsGetCachedContextDelegate>("swscale", "sws_getCachedContext");
        SwsScale = Load<SwsScaleDelegate>("swscale", "sws_scale");
        SwsFreeContext = Load<SwsFreeContextDelegate>("swscale", "sws_freeContext");
        SwrAllocSetOptions2 = Load<SwrAllocSetOptions2Delegate>("swresample", "swr_alloc_set_opts2");
        SwrInit = Load<SwrInitDelegate>("swresample", "swr_init");
        SwrGetDelay = Load<SwrGetDelayDelegate>("swresample", "swr_get_delay");
        SwrConvert = Load<SwrConvertDelegate>("swresample", "swr_convert");
        SwrFree = Load<SwrFreeDelegate>("swresample", "swr_free");
        AvFilterGraphAlloc = Load<AllocDelegate>("avfilter", "avfilter_graph_alloc");
        AvFilterGraphFree = Load<FreeDelegate>("avfilter", "avfilter_graph_free");
        AvFilterGetByName = Load<FilterGetByNameDelegate>("avfilter", "avfilter_get_by_name");
        AvFilterGraphCreateFilter =
            Load<FilterGraphCreateFilterDelegate>("avfilter", "avfilter_graph_create_filter");
        AvFilterLink = Load<FilterLinkDelegate>("avfilter", "avfilter_link");
        AvFilterGraphConfig = Load<FilterGraphConfigDelegate>("avfilter", "avfilter_graph_config");
        AvBufferSourceAddFrameFlags =
            Load<BufferSourceAddFrameFlagsDelegate>("avfilter", "av_buffersrc_add_frame_flags");
        AvBufferSinkGetFrame =
            Load<BufferSinkGetFrameDelegate>("avfilter", "av_buffersink_get_frame");

        CodecMajorVersion = (int)(AvCodecVersion() >> 16);
        FormatMajorVersion = (int)(AvFormatVersion() >> 16);
        UtilMajorVersion = (int)(AvUtilVersion() >> 16);
        ScaleMajorVersion = (int)(SwScaleVersion() >> 16);
        ResampleMajorVersion = (int)(SwResampleVersion() >> 16);
        FilterMajorVersion = (int)(AvFilterVersion() >> 16);
        ValidateVersionFamily(
            CodecMajorVersion,
            FormatMajorVersion,
            UtilMajorVersion,
            ScaleMajorVersion,
            ResampleMajorVersion,
            FilterMajorVersion);

        _ = AvFormatNetworkInit();
    }

    internal static FFmpegApi Instance => LazyInstance.Value;
    private readonly Lazy<FFmpegHardwareApi> _hardwareApi;
    internal FFmpegHardwareApi Hardware => _hardwareApi.Value;
    internal int CodecMajorVersion { get; }
    internal int FormatMajorVersion { get; }
    internal int UtilMajorVersion { get; }
    internal int ScaleMajorVersion { get; }
    internal int ResampleMajorVersion { get; }
    internal int FilterMajorVersion { get; }
    internal VersionDelegate AvCodecVersion { get; }
    internal VersionDelegate AvFormatVersion { get; }
    internal VersionDelegate AvUtilVersion { get; }
    internal VersionDelegate SwScaleVersion { get; }
    internal VersionDelegate SwResampleVersion { get; }
    internal VersionDelegate AvFilterVersion { get; }
    internal NetworkInitDelegate AvFormatNetworkInit { get; }
    internal GuessFrameRateDelegate AvGuessFrameRate { get; }
    internal AllocDelegate AvFormatAllocContext { get; }
    internal FormatOpenInputDelegate AvFormatOpenInput { get; }
    internal FormatFindStreamInfoDelegate AvFormatFindStreamInfo { get; }
    internal FindBestStreamDelegate AvFindBestStream { get; }
    internal ReadFrameDelegate AvReadFrame { get; }
    internal SeekFrameDelegate AvSeekFrame { get; }
    internal FormatCloseInputDelegate AvFormatCloseInput { get; }
    internal DictSetDelegate AvDictSet { get; }
    internal DictFreeDelegate AvDictFree { get; }
    internal StrErrorDelegate AvStrError { get; }
    internal OptFindDelegate AvOptFind { get; }
    internal OptGetIntDelegate AvOptGetInt { get; }
    internal OptGetChannelLayoutDelegate AvOptGetChannelLayout { get; }
    internal ChannelLayoutDefaultDelegate AvChannelLayoutDefault { get; }
    internal ChannelLayoutUninitDelegate AvChannelLayoutUninit { get; }
    internal AllocContextDelegate AvCodecAllocContext3 { get; }
    internal ParametersToContextDelegate AvCodecParametersToContext { get; }
    internal CodecOpenDelegate AvCodecOpen2 { get; }
    internal CodecPacketDelegate AvCodecSendPacket { get; }
    internal CodecFrameDelegate AvCodecReceiveFrame { get; }
    internal CodecFlushBuffersDelegate AvCodecFlushBuffers { get; }
    internal FreeContextDelegate AvCodecFreeContext { get; }
    internal AllocDelegate AvPacketAlloc { get; }
    internal CloneDelegate AvPacketClone { get; }
    internal UnrefDelegate AvPacketUnref { get; }
    internal FreeDelegate AvPacketFree { get; }
    internal AllocDelegate AvFrameAlloc { get; }
    internal CloneDelegate AvFrameClone { get; }
    internal FrameCopyPropertiesDelegate AvFrameCopyProperties { get; }
    internal UnrefDelegate AvFrameUnref { get; }
    internal FreeDelegate AvFrameFree { get; }
    internal GetSampleFormatNameDelegate AvGetSampleFormatName { get; }
    internal ChannelLayoutDescribeDelegate AvChannelLayoutDescribe { get; }
    internal SwsGetCachedContextDelegate SwsGetCachedContext { get; }
    internal SwsScaleDelegate SwsScale { get; }
    internal SwsFreeContextDelegate SwsFreeContext { get; }
    internal SwrAllocSetOptions2Delegate SwrAllocSetOptions2 { get; }
    internal SwrInitDelegate SwrInit { get; }
    internal SwrGetDelayDelegate SwrGetDelay { get; }
    internal SwrConvertDelegate SwrConvert { get; }
    internal SwrFreeDelegate SwrFree { get; }
    internal AllocDelegate AvFilterGraphAlloc { get; }
    internal FreeDelegate AvFilterGraphFree { get; }
    internal FilterGetByNameDelegate AvFilterGetByName { get; }
    internal FilterGraphCreateFilterDelegate AvFilterGraphCreateFilter { get; }
    internal FilterLinkDelegate AvFilterLink { get; }
    internal FilterGraphConfigDelegate AvFilterGraphConfig { get; }
    internal BufferSourceAddFrameFlagsDelegate AvBufferSourceAddFrameFlags { get; }
    internal BufferSinkGetFrameDelegate AvBufferSinkGetFrame { get; }

    internal string FormatError(int error)
    {
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            return AvStrError(error, buffer, 1024) >= 0
                ? Marshal.PtrToStringUTF8(buffer) ?? $"FFmpeg error {error}"
                : $"FFmpeg error {error}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void ValidateVersionFamily(
        int codecMajor,
        int formatMajor,
        int utilMajor,
        int scaleMajor,
        int resampleMajor,
        int filterMajor)
    {
        var expected = codecMajor switch
        {
            60 => (Format: 60, Util: 58, Scale: 7, Resample: 4, Filter: 9, Release: 6),
            61 => (Format: 61, Util: 59, Scale: 8, Resample: 5, Filter: 10, Release: 7),
            62 => (Format: 62, Util: 60, Scale: 9, Resample: 6, Filter: 11, Release: 8),
            _ => throw new NotSupportedException(
                $"This FrameFlux build supports FFmpeg avcodec major versions 60, 61 and 62; found {codecMajor}.")
        };

        if (formatMajor == expected.Format &&
            utilMajor == expected.Util &&
            scaleMajor == expected.Scale &&
            resampleMajor == expected.Resample &&
            filterMajor == expected.Filter)
        {
            return;
        }

        throw new NotSupportedException(
            $"The loaded FFmpeg components do not form a supported FFmpeg {expected.Release} ABI family. " +
            $"Expected avcodec/avformat/avutil/swscale/swresample/avfilter " +
            $"{codecMajor}/{expected.Format}/{expected.Util}/{expected.Scale}/{expected.Resample}/{expected.Filter}, " +
            $"but found {codecMajor}/{formatMajor}/{utilMajor}/{scaleMajor}/{resampleMajor}/{filterMajor}. " +
            "Use all shared libraries from one FFmpeg build.");
    }

    private static T Load<T>(string component, string exportName) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(FFmpegLibraryLoader.GetExport(component, exportName));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint VersionDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NetworkInitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NativeRational GuessFrameRateDelegate(IntPtr formatContext, IntPtr stream, IntPtr frame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FormatOpenInputDelegate(ref IntPtr context, IntPtr url, IntPtr format, ref IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FormatFindStreamInfoDelegate(IntPtr context, IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FindBestStreamDelegate(IntPtr context, int mediaType, int wantedStream, int relatedStream, out IntPtr decoder, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadFrameDelegate(IntPtr context, IntPtr packet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SeekFrameDelegate(IntPtr context, int streamIndex, long timestamp, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FormatCloseInputDelegate(ref IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DictSetDelegate(ref IntPtr dictionary, IntPtr key, IntPtr value, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DictFreeDelegate(ref IntPtr dictionary);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StrErrorDelegate(int error, IntPtr buffer, nuint bufferSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr OptFindDelegate(IntPtr value, IntPtr name, IntPtr unit, int optionFlags, int searchFlags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OptGetIntDelegate(IntPtr value, IntPtr name, int searchFlags, out long result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OptGetChannelLayoutDelegate(IntPtr value, IntPtr name, int searchFlags, IntPtr layout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ChannelLayoutDefaultDelegate(IntPtr layout, int channels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ChannelLayoutUninitDelegate(IntPtr layout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr AllocContextDelegate(IntPtr codec);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ParametersToContextDelegate(IntPtr codecContext, IntPtr codecParameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CodecOpenDelegate(IntPtr codecContext, IntPtr codec, IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CodecPacketDelegate(IntPtr codecContext, IntPtr packet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CodecFrameDelegate(IntPtr codecContext, IntPtr frame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CodecFlushBuffersDelegate(IntPtr codecContext);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeContextDelegate(ref IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr AllocDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CloneDelegate(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FrameCopyPropertiesDelegate(IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void UnrefDelegate(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeDelegate(ref IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetSampleFormatNameDelegate(int sampleFormat);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ChannelLayoutDescribeDelegate(IntPtr layout, IntPtr buffer, nuint bufferSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetFormatDelegate(IntPtr codecContext, IntPtr formats);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr SwsGetCachedContextDelegate(IntPtr context, int sourceWidth, int sourceHeight, int sourceFormat, int destinationWidth, int destinationHeight, int destinationFormat, int flags, IntPtr sourceFilter, IntPtr destinationFilter, IntPtr parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SwsScaleDelegate(IntPtr context, IntPtr sourceData, IntPtr sourceStride, int sourceSliceY, int sourceSliceHeight, IntPtr destinationData, IntPtr destinationStride);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SwsFreeContextDelegate(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SwrAllocSetOptions2Delegate(ref IntPtr context, IntPtr outputLayout, int outputFormat, int outputRate, IntPtr inputLayout, int inputFormat, int inputRate, int logOffset, IntPtr logContext);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SwrInitDelegate(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long SwrGetDelayDelegate(IntPtr context, long timeBase);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SwrConvertDelegate(IntPtr context, IntPtr output, int outputSamples, IntPtr input, int inputSamples);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SwrFreeDelegate(ref IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr FilterGetByNameDelegate(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FilterGraphCreateFilterDelegate(out IntPtr filterContext, IntPtr filter, IntPtr name, IntPtr arguments, IntPtr opaque, IntPtr graphContext);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FilterLinkDelegate(IntPtr source, uint sourcePad, IntPtr destination, uint destinationPad);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FilterGraphConfigDelegate(IntPtr graphContext, IntPtr logContext);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int BufferSourceAddFrameFlagsDelegate(IntPtr sourceContext, IntPtr frame, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int BufferSinkGetFrameDelegate(IntPtr sinkContext, IntPtr frame);
}
