using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal interface IAudioOutput : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    long PlayedFrames { get; }
    bool IsOperational { get; }
    bool TrySetVolume(double volume, bool muted);
    void Write(byte[] pcm);
}

internal static class AudioOutputFactory
{
    internal const int SampleRate = 48000;
    internal const int Channels = 2;

    internal static IAudioOutput Create()
    {
        try
        {
#if ANDROID
            return new AndroidAudioOutput(SampleRate, Channels);
#else
            if (OperatingSystem.IsWindows()) return new WindowsWaveOutAudioOutput(SampleRate, Channels);
            if (OperatingSystem.IsLinux()) return new LinuxAlsaAudioOutput(SampleRate, Channels);
            return new NullAudioOutput(SampleRate, Channels);
#endif
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize platform audio output: {exception}");
            return new NullAudioOutput(SampleRate, Channels);
        }
    }
}

internal sealed class AudioPlaybackController : IDisposable
{
    private readonly IAudioOutput _output;
    private readonly double _gainMultiplier;
    private double _volume;
    private bool _muted;
    private bool _outputVolumeActive;
    private double? _mediaStartSeconds;

    internal AudioPlaybackController(
        double volume,
        bool muted,
        double gainDecibels = 0d)
    {
        _output = AudioOutputFactory.Create();
        _gainMultiplier = DecibelsToAmplitude(gainDecibels);
        _volume = volume;
        _muted = muted;
        _outputVolumeActive = _output.TrySetVolume(volume, muted);
    }

    internal bool IsOperational => _output.IsOperational;

    internal double? PositionSeconds => _mediaStartSeconds is { } start
        ? start + (double)_output.PlayedFrames / _output.SampleRate
        : null;

    internal void SetVolume(double volume)
    {
        Volatile.Write(ref _volume, volume);
        Volatile.Write(
            ref _outputVolumeActive,
            _output.TrySetVolume(volume, Volatile.Read(ref _muted)));
    }

    internal void SetMuted(bool muted)
    {
        Volatile.Write(ref _muted, muted);
        Volatile.Write(
            ref _outputVolumeActive,
            _output.TrySetVolume(Volatile.Read(ref _volume), muted));
    }

    internal void Write(NativeAudioFrame frame)
    {
        if (!_output.IsOperational || frame.Data.Length == 0)
        {
            return;
        }

        if (_mediaStartSeconds is null && frame.PresentationSeconds is { } presentation)
        {
            _mediaStartSeconds = presentation;
        }

        ApplyGainMultiplier(frame.Data, _gainMultiplier);
        if (!Volatile.Read(ref _outputVolumeActive))
        {
            ApplyVolume(frame.Data, Volatile.Read(ref _volume), Volatile.Read(ref _muted));
        }
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

internal sealed class NullAudioOutput(int sampleRate, int channels) : IAudioOutput
{
    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public long PlayedFrames => 0;
    public bool IsOperational => false;
    public bool TrySetVolume(double volume, bool muted) => false;
    public void Write(byte[] pcm) { }
    public void Dispose() { }
}
