#if ANDROID
using Android.Media;

namespace FrameFlux.FFmpeg;

internal sealed class AndroidAudioOutput : IAudioOutput
{
    private AudioTrack? _track;
    private uint _lastHead;
    private long _headWraps;

    internal AndroidAudioOutput(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var channelMask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
        var minimum = AudioTrack.GetMinBufferSize(sampleRate, channelMask, Encoding.Pcm16bit);
        if (minimum <= 0) throw new InvalidOperationException($"AudioTrack buffer query failed with code {minimum}.");
#pragma warning disable CS0618, CA1422
        _track = new AudioTrack(
            Android.Media.Stream.Music,
            sampleRate,
            channels == 1 ? ChannelConfiguration.Mono : ChannelConfiguration.Stereo,
            Encoding.Pcm16bit,
            minimum * 2,
            AudioTrackMode.Stream);
#pragma warning restore CS0618, CA1422
        _track.Play();
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public bool IsOperational => _track is not null;
    public bool TrySetVolume(double volume, bool muted) => false;
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
        if (written < 0) throw new InvalidOperationException($"AudioTrack.Write failed with code {written}.");
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
