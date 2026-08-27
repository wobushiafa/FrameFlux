#if ANDROID
using Android.Media;

namespace FrameFlux.FFmpeg;

internal sealed class AndroidAudioOutput : IAudioOutput
{
    private AudioTrack? _track;
    private readonly AudioOutputConfiguration _configuration;
    private readonly int _bufferSizeBytes;
    private long _submittedFrames;
    private string? _lastError;
    private uint _lastHead;
    private long _headWraps;

    internal AndroidAudioOutput(
        int sampleRate,
        int channels,
        AudioOutputConfiguration? configuration = null)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _configuration = configuration ?? new AudioOutputConfiguration(
            null,
            TimeSpan.FromMilliseconds(100));
        var channelMask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
        var minimum = AudioTrack.GetMinBufferSize(sampleRate, channelMask, Encoding.Pcm16bit);
        if (minimum <= 0) throw new InvalidOperationException($"AudioTrack buffer query failed with code {minimum}.");
        var requested = checked((int)Math.Ceiling(
            sampleRate * channels * sizeof(short) * _configuration.BufferDuration.TotalSeconds));
        _bufferSizeBytes = Math.Max(minimum, requested);
#pragma warning disable CS0618, CA1422
        _track = new AudioTrack(
            Android.Media.Stream.Music,
            sampleRate,
            channels == 1 ? ChannelConfiguration.Mono : ChannelConfiguration.Stereo,
            Encoding.Pcm16bit,
            _bufferSizeBytes,
            AudioTrackMode.Stream);
#pragma warning restore CS0618, CA1422
        _track.Play();
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public bool IsOperational => _track is not null;
    public bool TrySetVolume(double volume, bool muted) => false;
    public MediaAudioDiagnostics Diagnostics
    {
        get
        {
            var queuedFrames = Math.Max(0, Interlocked.Read(ref _submittedFrames) - PlayedFrames);
            return new MediaAudioDiagnostics(
                "AudioTrack",
                null,
                "Android system audio output",
                SampleRate,
                Channels,
                _configuration.BufferDuration,
                TimeSpan.FromSeconds((double)queuedFrames / SampleRate),
                _track is not null,
                0,
                _lastError);
        }
    }
    public long PlayedFrames
    {
        get
        {
            var track = _track;
            if (track is null) return 0;
            var head = unchecked((uint)track.PlaybackHeadPosition);
            if (head < _lastHead) _headWraps += 1L << 32;
            _lastHead = head;
            return _headWraps + head;
        }
    }

    public void Write(byte[] pcm)
    {
        var track = _track;
        if (track is null || pcm.Length == 0) return;
        var written = OperatingSystem.IsAndroidVersionAtLeast(23)
            ? track.Write(pcm, 0, pcm.Length, WriteMode.Blocking)
            : track.Write(pcm, 0, pcm.Length);
        if (written < 0)
        {
            _lastError = $"AudioTrack.Write failed with code {written}.";
            throw new InvalidOperationException(_lastError);
        }
        Interlocked.Add(ref _submittedFrames, written / (Channels * sizeof(short)));
    }

    public void Reset()
    {
        var track = _track;
        if (track is null)
        {
            return;
        }

        track.Pause();
        track.Flush();
        Interlocked.Exchange(ref _submittedFrames, 0);
        _lastHead = 0;
        _headWraps = 0;
        track.Play();
    }

    public void Dispose()
    {
        var track = Interlocked.Exchange(ref _track, null);
        if (track is null) return;
        track.Stop();
        track.Flush();
        track.Release();
        track.Dispose();
    }
}
#endif
