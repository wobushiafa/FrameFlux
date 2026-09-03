namespace FrameFlux.WebRtc;

/// <summary>
/// Defines an audio sink/output capable of playing decoded 16-bit linear PCM audio samples.
/// </summary>
public interface IWebRtcAudioOutput : IDisposable
{
    /// <summary>
    /// Gets whether this audio output is supported on the current platform/hardware.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Configures the audio output device format (sample rate and channels).
    /// </summary>
    void EnsureFormat(int sampleRate, int channels);

    /// <summary>
    /// Queues and plays 16-bit signed linear PCM samples.
    /// </summary>
    void WriteSamples(ReadOnlySpan<short> samples);

    /// <summary>
    /// Sets volume (0.0 to 1.0) and mute status.
    /// </summary>
    void SetVolume(double volume, bool isMuted);

    /// <summary>
    /// Pauses audio playback.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes audio playback.
    /// </summary>
    void Resume();

    /// <summary>
    /// Flushes queued audio buffers.
    /// </summary>
    void Reset();
}

/// <summary>
/// Null audio output used as a fallback on headless or unsupported systems.
/// </summary>
public sealed class NullWebRtcAudioOutput : IWebRtcAudioOutput
{
    public static NullWebRtcAudioOutput Instance { get; } = new();

    public bool IsSupported => false;

    public void EnsureFormat(int sampleRate, int channels) { }

    public void WriteSamples(ReadOnlySpan<short> samples) { }

    public void SetVolume(double volume, bool isMuted) { }

    public void Pause() { }

    public void Resume() { }

    public void Reset() { }

    public void Dispose() { }
}
