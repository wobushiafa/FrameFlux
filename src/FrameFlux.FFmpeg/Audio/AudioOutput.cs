using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal interface IAudioOutput : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    long PlayedFrames { get; }
    bool IsOperational { get; }
    MediaAudioDiagnostics Diagnostics { get; }
    void Reset();
    void Write(byte[] pcm);
}

internal static class AudioOutputFactory
{
    internal const int SampleRate = 48000;
    internal const int Channels = 2;

    internal static IAudioOutput Create(AudioOutputConfiguration configuration)
    {
        try
        {
#if ANDROID
            return new AndroidAudioOutput(SampleRate, Channels, configuration);
#else
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return new WindowsWasapiAudioOutput(SampleRate, Channels, configuration);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize WASAPI output: {exception}");
                    if (configuration.OutputDeviceId is not null)
                    {
                        throw;
                    }
                    return new WindowsWaveOutAudioOutput(SampleRate, Channels, configuration, exception.Message);
                }
            }
            if (OperatingSystem.IsLinux()) return new LinuxAlsaAudioOutput(SampleRate, Channels, configuration);
            return new NullAudioOutput(SampleRate, Channels);
#endif
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize platform audio output: {exception}");
            return new NullAudioOutput(SampleRate, Channels, configuration, exception.Message);
        }
    }
}

internal sealed record AudioOutputConfiguration(
    string? OutputDeviceId,
    TimeSpan BufferDuration);

internal sealed class AudioPlaybackController : IDisposable
{
    private const double TimestampDiscontinuitySeconds = 0.5d;
    private readonly IAudioOutput _output;
    private readonly object _clockSync = new();
    private readonly double _gainMultiplier;
    private double _volume;
    private bool _muted;
    private double? _mediaStartSeconds;
    private double? _lastSubmittedEndSeconds;
    private long _outputFramesAtMediaStart;
    private int _clockResetCount;

    internal AudioPlaybackController(
        double volume,
        bool muted,
        double gainDecibels = 0d,
        string? outputDeviceId = null,
        TimeSpan? bufferDuration = null,
        IAudioOutput? output = null)
    {
        _output = output ?? AudioOutputFactory.Create(new AudioOutputConfiguration(
            outputDeviceId,
            bufferDuration ?? TimeSpan.FromMilliseconds(100)));
        _gainMultiplier = DecibelsToAmplitude(gainDecibels);
        _volume = volume;
        _muted = muted;
    }

    internal bool IsOperational => _output.IsOperational;

    internal MediaAudioDiagnostics Diagnostics => _output.Diagnostics;

    internal double? PositionSeconds
    {
        get
        {
            lock (_clockSync)
            {
                return _mediaStartSeconds is { } start
                    ? start + (double)(_output.PlayedFrames - _outputFramesAtMediaStart) /
                        _output.SampleRate
                    : null;
            }
        }
    }

    internal int ClockResetCount => Volatile.Read(ref _clockResetCount);

    internal void SetVolume(double volume)
    {
        Volatile.Write(ref _volume, volume);
    }

    internal void SetMuted(bool muted)
    {
        Volatile.Write(ref _muted, muted);
    }

    internal void Write(NativeAudioFrame frame)
    {
        if (frame.Data.Length == 0)
        {
            return;
        }

        if (frame.PresentationSeconds is { } presentation)
        {
            lock (_clockSync)
            {
                if (_lastSubmittedEndSeconds is { } expected &&
                    Math.Abs(presentation - expected) > TimestampDiscontinuitySeconds)
                {
                    _output.Reset();
                    _mediaStartSeconds = presentation;
                    _outputFramesAtMediaStart = _output.PlayedFrames;
                    Interlocked.Increment(ref _clockResetCount);
                }
                else if (_mediaStartSeconds is null)
                {
                    _mediaStartSeconds = presentation;
                    _outputFramesAtMediaStart = _output.PlayedFrames;
                }

                var frameDuration = frame.SampleRate > 0 && frame.Channels > 0
                    ? (double)frame.Data.Length /
                        (frame.SampleRate * frame.Channels * sizeof(short))
                    : 0d;
                _lastSubmittedEndSeconds = presentation + frameDuration;
            }
        }

        ApplyGainMultiplier(frame.Data, _gainMultiplier);
        ApplyVolume(frame.Data, Volatile.Read(ref _volume), Volatile.Read(ref _muted));
        _output.Write(frame.Data);
    }

    public void Dispose() => _output.Dispose();

    internal static void ApplyGain(Span<byte> pcm, double gainDecibels) =>
        ApplyGainMultiplier(pcm, DecibelsToAmplitude(gainDecibels));

    internal static void ApplyVolume(Span<byte> pcm, double volume, bool muted)
    {
        if (muted || volume <= 0d)
        {
            pcm.Clear();
            return;
        }

        if (volume >= 1d)
        {
            return;
        }

        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (short)Math.Clamp(
                (int)Math.Round(samples[index] * volume),
                short.MinValue,
                short.MaxValue);
        }
    }

    private static void ApplyGainMultiplier(Span<byte> pcm, double multiplier)
    {
        if (multiplier == 1d)
        {
            return;
        }

        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (short)Math.Clamp(
                (int)Math.Round(samples[index] * multiplier),
                short.MinValue,
                short.MaxValue);
        }
    }

    private static double DecibelsToAmplitude(double gainDecibels) =>
        Math.Pow(10d, gainDecibels / 20d);
}

internal sealed class NullAudioOutput : IAudioOutput
{
    private readonly AudioOutputConfiguration? _configuration;
    private readonly string? _lastError;

    internal NullAudioOutput(
        int sampleRate,
        int channels,
        AudioOutputConfiguration? configuration = null,
        string? lastError = null)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _configuration = configuration;
        _lastError = lastError;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long PlayedFrames => 0;
    public bool IsOperational => false;
    public MediaAudioDiagnostics Diagnostics => new(
        "None",
        _configuration?.OutputDeviceId,
        null,
        SampleRate,
        Channels,
        _configuration?.BufferDuration ?? TimeSpan.Zero,
        TimeSpan.Zero,
        false,
        0,
        _lastError);
    public void Reset() { }
    public void Write(byte[] pcm) { }
    public void Dispose() { }
}
