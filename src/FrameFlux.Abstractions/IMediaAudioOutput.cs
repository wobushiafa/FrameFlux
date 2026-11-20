namespace FrameFlux;

/// <summary>A player-owned sink for interleaved signed 16-bit PCM. All members must be thread safe.</summary>
/// <remarks>Write must enqueue or consume PCM before returning. Reset discards buffered audio;
/// PlayedFrames counts frames actually rendered (excluding silence from underruns).</remarks>
public interface IMediaAudioOutput : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    long PlayedFrames { get; }
    bool IsOperational { get; }
    MediaAudioDiagnostics Diagnostics { get; }
    void Reset();
    void Write(ReadOnlySpan<byte> pcm);
}
