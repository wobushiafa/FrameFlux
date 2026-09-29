#if ANDROID
namespace FrameFlux.WebRtc;

/// <summary>
/// Compatibility audio output for applications that construct the desktop output
/// type on Android. Audio is routed to the platform AudioTrack backend.
/// </summary>
public sealed class WebRtcWaveOutAudioOutput : IWebRtcAudioOutput
{
    private readonly WebRtcAudioTrackOutput _output = new();

    public bool IsSupported => _output.IsSupported;
    public void EnsureFormat(int sampleRate, int channels) => _output.EnsureFormat(sampleRate, channels);
    public void WriteSamples(ReadOnlySpan<short> samples) => _output.WriteSamples(samples);
    public void SetVolume(double volume, bool isMuted) => _output.SetVolume(volume, isMuted);
    public void Pause() => _output.Pause();
    public void Resume() => _output.Resume();
    public void Reset() => _output.Reset();
    public void Dispose() => _output.Dispose();
}
#endif
