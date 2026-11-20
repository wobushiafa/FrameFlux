namespace FrameFlux.FFmpeg;

internal sealed class InjectedAudioOutput : IAudioOutput
{
    private readonly IMediaAudioOutput _output;
    internal InjectedAudioOutput(IMediaAudioOutput output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (output.SampleRate != AudioOutputFactory.SampleRate || output.Channels != AudioOutputFactory.Channels)
        {
            output.Dispose();
            throw new ArgumentException("The injected audio output must accept 48000 Hz stereo PCM.", nameof(output));
        }
    }
    public int SampleRate => _output.SampleRate;
    public int Channels => _output.Channels;
    public long PlayedFrames => _output.PlayedFrames;
    public bool IsOperational => _output.IsOperational;
    public MediaAudioDiagnostics Diagnostics => _output.Diagnostics;
    public void Reset() => _output.Reset();
    public void Write(byte[] pcm) => _output.Write(pcm);
    public void Dispose() => _output.Dispose();
}
