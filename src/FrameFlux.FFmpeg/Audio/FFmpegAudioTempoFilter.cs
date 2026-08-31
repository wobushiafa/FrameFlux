using System.Globalization;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class FFmpegAudioTempoFilter : IDisposable
{
    private const int ErrorAgain = -11;
    private const int ErrorEof = -541478725;
    private const int KeepFrameReference = 8;
    private readonly FFmpegApi _api;
    private readonly int _sampleRate;
    private readonly int _timeBaseNumerator;
    private readonly int _timeBaseDenominator;
    private IntPtr _inputLayout;
    private IntPtr _graph;
    private IntPtr _source;
    private IntPtr _sink;
    private IntPtr _outputFrame;
    private int _inputFormat = int.MinValue;
    private double _playbackRate = 1d;
    private bool _disposed;

    internal FFmpegAudioTempoFilter(
        FFmpegApi api,
        IntPtr codecContext,
        int timeBaseNumerator,
        int timeBaseDenominator)
    {
        _api = api;
        _sampleRate = checked((int)GetIntOption(
            codecContext,
            "ar",
            AudioOutputFactory.SampleRate));
        _timeBaseNumerator = Math.Max(1, timeBaseNumerator);
        _timeBaseDenominator = Math.Max(1, timeBaseDenominator);
        try
        {
            _inputLayout = Marshal.AllocHGlobal(32);
            ClearLayout(_inputLayout);
            using var name = new NativeUtf8String("ch_layout");
            if (_api.AvOptGetChannelLayout(codecContext, name.Pointer, 0, _inputLayout) < 0 ||
                Marshal.ReadInt32(_inputLayout, sizeof(int)) <= 0)
            {
                var channels = checked((int)GetIntOption(codecContext, "channels", 2));
                ClearLayout(_inputLayout);
                _api.AvChannelLayoutDefault(_inputLayout, Math.Max(1, channels));
            }

            _outputFrame = _api.AvFrameAlloc();
            if (_outputFrame == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Unable to allocate the FFmpeg audio filter output frame.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Process(
        IntPtr frame,
        FrameLayout layout,
        double playbackRate,
        Action<IntPtr, FrameLayout> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        if (playbackRate == 1d)
        {
            ResetGraph();
            output(frame, layout);
            return;
        }

        EnsureGraph(playbackRate, layout.Format);
        var result = _api.AvBufferSourceAddFrameFlags(_source, frame, KeepFrameReference);
        if (result < 0)
        {
            throw CreateException("av_buffersrc_add_frame_flags", result);
        }

        Drain(output);
    }

    internal void Flush(Action<IntPtr, FrameLayout> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        if (_graph == IntPtr.Zero)
        {
            return;
        }

        var result = _api.AvBufferSourceAddFrameFlags(_source, IntPtr.Zero, 0);
        if (result < 0 && result != ErrorEof)
        {
            throw CreateException("flush av_buffersrc", result);
        }

        Drain(output);
        ResetGraph();
    }

    internal void Reset() => ResetGraph();

    internal static IReadOnlyList<double> CreateTempoFactors(double playbackRate)
    {
        MediaPlaybackClock.ValidateRate(playbackRate);
        var factors = new List<double>(2);
        var remaining = playbackRate;
        while (remaining < 0.5d)
        {
            factors.Add(0.5d);
            remaining /= 0.5d;
        }
        while (remaining > 2d)
        {
            factors.Add(2d);
            remaining /= 2d;
        }
        if (Math.Abs(remaining - 1d) > 1e-9)
        {
            factors.Add(remaining);
        }

        return factors;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetGraph();
        if (_outputFrame != IntPtr.Zero)
        {
            _api.AvFrameFree(ref _outputFrame);
        }
        if (_inputLayout != IntPtr.Zero)
        {
            _api.AvChannelLayoutUninit(_inputLayout);
            Marshal.FreeHGlobal(_inputLayout);
            _inputLayout = IntPtr.Zero;
        }
    }

    private void EnsureGraph(double playbackRate, int inputFormat)
    {
        if (_graph != IntPtr.Zero &&
            playbackRate == _playbackRate &&
            inputFormat == _inputFormat)
        {
            return;
        }

        ResetGraph();
        _graph = _api.AvFilterGraphAlloc();
        if (_graph == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Unable to allocate the FFmpeg audio filter graph.");
        }

        try
        {
            var sampleFormat = Marshal.PtrToStringUTF8(
                _api.AvGetSampleFormatName(inputFormat));
            if (string.IsNullOrWhiteSpace(sampleFormat))
            {
                throw new NotSupportedException(
                    $"FFmpeg audio sample format {inputFormat} is not supported.");
            }

            var channelLayout = DescribeChannelLayout();
            var sourceArguments = string.Create(
                CultureInfo.InvariantCulture,
                $"time_base={_timeBaseNumerator}/{_timeBaseDenominator}:" +
                $"sample_rate={_sampleRate}:sample_fmt={sampleFormat}:" +
                $"channel_layout={channelLayout}");
            _source = CreateFilter("abuffer", "tempo_input", sourceArguments);
            _sink = CreateFilter("abuffersink", "tempo_output", null);

            var previous = _source;
            var factors = CreateTempoFactors(playbackRate);
            for (var index = 0; index < factors.Count; index++)
            {
                var arguments = string.Create(
                    CultureInfo.InvariantCulture,
                    $"tempo={factors[index]:0.########}");
                var tempo = CreateFilter("atempo", $"tempo_{index}", arguments);
                Link(previous, tempo);
                previous = tempo;
            }

            Link(previous, _sink);
            var result = _api.AvFilterGraphConfig(_graph, IntPtr.Zero);
            if (result < 0)
            {
                throw CreateException("avfilter_graph_config", result);
            }

            _playbackRate = playbackRate;
            _inputFormat = inputFormat;
        }
        catch
        {
            ResetGraph();
            throw;
        }
    }

    private IntPtr CreateFilter(string filterName, string instanceName, string? arguments)
    {
        using var nativeFilterName = new NativeUtf8String(filterName);
        var filter = _api.AvFilterGetByName(nativeFilterName.Pointer);
        if (filter == IntPtr.Zero)
        {
            throw new NotSupportedException(
                $"The loaded FFmpeg avfilter library does not provide '{filterName}'.");
        }

        using var nativeInstanceName = new NativeUtf8String(instanceName);
        using var nativeArguments = new NativeUtf8String(arguments);
        var result = _api.AvFilterGraphCreateFilter(
            out var context,
            filter,
            nativeInstanceName.Pointer,
            nativeArguments.Pointer,
            IntPtr.Zero,
            _graph);
        if (result < 0 || context == IntPtr.Zero)
        {
            throw CreateException($"create {filterName} filter", result);
        }

        return context;
    }

    private void Link(IntPtr source, IntPtr destination)
    {
        var result = _api.AvFilterLink(source, 0, destination, 0);
        if (result < 0)
        {
            throw CreateException("avfilter_link", result);
        }
    }

    private void Drain(Action<IntPtr, FrameLayout> output)
    {
        while (true)
        {
            var result = _api.AvBufferSinkGetFrame(_sink, _outputFrame);
            if (result is ErrorAgain or ErrorEof)
            {
                return;
            }
            if (result < 0)
            {
                throw CreateException("av_buffersink_get_frame", result);
            }

            try
            {
                output(
                    _outputFrame,
                    FFmpegAbi.ReadFrame(_outputFrame, _api.UtilMajorVersion));
            }
            finally
            {
                _api.AvFrameUnref(_outputFrame);
            }
        }
    }

    private string DescribeChannelLayout()
    {
        var buffer = Marshal.AllocHGlobal(128);
        try
        {
            var result = _api.AvChannelLayoutDescribe(_inputLayout, buffer, 128);
            var value = result >= 0 ? Marshal.PtrToStringUTF8(buffer) : null;
            return string.IsNullOrWhiteSpace(value)
                ? throw CreateException("av_channel_layout_describe", result)
                : value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private long GetIntOption(IntPtr target, string option, long fallback)
    {
        using var nativeOption = new NativeUtf8String(option);
        return _api.AvOptGetInt(target, nativeOption.Pointer, 0, out var value) >= 0
            ? value
            : fallback;
    }

    private ApplicationException CreateException(string operation, int error) =>
        new($"{operation}: {_api.FormatError(error)} ({error})");

    private void ResetGraph()
    {
        if (_outputFrame != IntPtr.Zero)
        {
            _api.AvFrameUnref(_outputFrame);
        }
        if (_graph != IntPtr.Zero)
        {
            _api.AvFilterGraphFree(ref _graph);
        }

        _source = IntPtr.Zero;
        _sink = IntPtr.Zero;
        _inputFormat = int.MinValue;
        _playbackRate = 1d;
    }

    private static void ClearLayout(IntPtr layout)
    {
        for (var offset = 0; offset < 32; offset += sizeof(long))
        {
            Marshal.WriteInt64(layout, offset, 0);
        }
    }
}
