using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class FFmpegAudioResampler : IDisposable
{
    private const int SampleFormatS16 = 1;
    private readonly FFmpegApi _api;
    private readonly int _inputRate;
    private IntPtr _inputLayout;
    private IntPtr _outputLayout;
    private IntPtr _context;
    private readonly ReusableUnmanagedBuffer _outputBuffer = new();
    private int _inputFormat = int.MinValue;

    internal FFmpegAudioResampler(FFmpegApi api, IntPtr codecContext)
    {
        _api = api;
        // AVCodecContext exposes the sample rate through the standard "ar" AVOption.
        // "sample_rate" is not an AVOption and silently made low-rate camera audio
        // fall back to 48 kHz, producing very short, effectively inaudible chunks.
        _inputRate = checked((int)GetIntOption(codecContext, "ar", AudioOutputFactory.SampleRate));
        try
        {
            _inputLayout = Marshal.AllocHGlobal(32);
            ClearLayout(_inputLayout);
            _outputLayout = Marshal.AllocHGlobal(32);
            ClearLayout(_outputLayout);

            using var name = new NativeUtf8String("ch_layout");
            if (_api.AvOptGetChannelLayout(codecContext, name.Pointer, 0, _inputLayout) < 0 ||
                Marshal.ReadInt32(_inputLayout, sizeof(int)) <= 0)
            {
                var channels = checked((int)GetIntOption(codecContext, "channels", 2));
                ClearLayout(_inputLayout);
                _api.AvChannelLayoutDefault(_inputLayout, Math.Max(1, channels));
            }
            _api.AvChannelLayoutDefault(_outputLayout, AudioOutputFactory.Channels);
        }
        catch
        {
            ReleaseLayouts();
            throw;
        }
    }

    internal NativeAudioFrame Convert(
        IntPtr frame,
        FrameLayout layout,
        int timeBaseNumerator,
        int timeBaseDenominator)
    {
        EnsureInitialized(layout.Format);
        var delayed = _api.SwrGetDelay(_context, _inputRate);
        var outputSamples = checked((int)Math.Ceiling(
            (delayed + layout.SampleCount) * (double)AudioOutputFactory.SampleRate / _inputRate));
        outputSamples = Math.Max(outputSamples, layout.SampleCount);
        var byteCount = checked(outputSamples * AudioOutputFactory.Channels * sizeof(short));
        _outputBuffer.EnsureCapacity(byteCount);
        unsafe
        {
            IntPtr* outputs = stackalloc IntPtr[1];
            outputs[0] = _outputBuffer.Pointer;
            var converted = _api.SwrConvert(
                _context,
                (IntPtr)outputs,
                outputSamples,
                layout.ExtendedData,
                layout.SampleCount);
            if (converted < 0)
            {
                throw new ApplicationException($"swr_convert: {_api.FormatError(converted)} ({converted})");
            }

            var data = GC.AllocateUninitializedArray<byte>(
                checked(converted * AudioOutputFactory.Channels * sizeof(short)));
            Marshal.Copy(_outputBuffer.Pointer, data, 0, data.Length);
            return new NativeAudioFrame(
                data,
                AudioOutputFactory.SampleRate,
                AudioOutputFactory.Channels,
                layout.PresentationTimestamp,
                timeBaseNumerator,
                timeBaseDenominator);
        }
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero) _api.SwrFree(ref _context);
        _outputBuffer.Dispose();
        ReleaseLayouts();
    }

    private void EnsureInitialized(int inputFormat)
    {
        if (_context != IntPtr.Zero && inputFormat == _inputFormat) return;
        if (_context != IntPtr.Zero) _api.SwrFree(ref _context);
        var result = _api.SwrAllocSetOptions2(
            ref _context,
            _outputLayout,
            SampleFormatS16,
            AudioOutputFactory.SampleRate,
            _inputLayout,
            inputFormat,
            _inputRate,
            0,
            IntPtr.Zero);
        if (result < 0 || _context == IntPtr.Zero)
        {
            throw new ApplicationException($"swr_alloc_set_opts2: {_api.FormatError(result)} ({result})");
        }
        result = _api.SwrInit(_context);
        if (result < 0)
        {
            throw new ApplicationException($"swr_init: {_api.FormatError(result)} ({result})");
        }
        _inputFormat = inputFormat;
    }

    private long GetIntOption(IntPtr target, string option, long fallback)
    {
        using var nativeOption = new NativeUtf8String(option);
        return _api.AvOptGetInt(target, nativeOption.Pointer, 0, out var value) >= 0
            ? value
            : fallback;
    }

    private static void ClearLayout(IntPtr layout)
    {
        for (var offset = 0; offset < 32; offset += sizeof(long))
        {
            Marshal.WriteInt64(layout, offset, 0);
        }
    }

    private void ReleaseLayouts()
    {
        if (_inputLayout != IntPtr.Zero)
        {
            _api.AvChannelLayoutUninit(_inputLayout);
            Marshal.FreeHGlobal(_inputLayout);
            _inputLayout = IntPtr.Zero;
        }
        if (_outputLayout != IntPtr.Zero)
        {
            _api.AvChannelLayoutUninit(_outputLayout);
            Marshal.FreeHGlobal(_outputLayout);
            _outputLayout = IntPtr.Zero;
        }
    }
}
