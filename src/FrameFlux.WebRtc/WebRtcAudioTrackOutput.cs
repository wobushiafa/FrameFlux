#if ANDROID
using System.Collections.Concurrent;
using Android.Media;

namespace FrameFlux.WebRtc;

/// <summary>Buffered Android AudioTrack output for WebRTC PCM.</summary>
public sealed class WebRtcAudioTrackOutput : IWebRtcAudioOutput
{
    private readonly object _sync = new();
    private readonly BlockingCollection<(int Generation, short[] Samples)> _queue = new(12);
    private readonly Thread _writer;
    private AudioTrack? _track;
    private int _sampleRate;
    private int _channels;
    private double _volume = 1d;
    private bool _muted;
    private volatile bool _paused;
    private volatile bool _disposed;
    private int _formatGeneration;

    public WebRtcAudioTrackOutput()
    {
        _writer = new Thread(WriteQueuedSamples)
        {
            IsBackground = true,
            Name = "FrameFlux WebRTC AudioTrack"
        };
        _writer.Start();
    }

    public bool IsSupported => OperatingSystem.IsAndroid();

    public void EnsureFormat(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 4000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 2);
        lock (_sync)
        {
            if (_disposed || _track is not null && _sampleRate == sampleRate && _channels == channels) return;
            _formatGeneration++;
            ClearQueue();
            ReleaseTrack();
            var channelMask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
            var minimum = AudioTrack.GetMinBufferSize(sampleRate, channelMask, Encoding.Pcm16bit);
            if (minimum <= 0) return;

#pragma warning disable CS0618, CA1422
            var track = new AudioTrack(Android.Media.Stream.Music, sampleRate,
                channels == 1 ? ChannelConfiguration.Mono : ChannelConfiguration.Stereo,
                Encoding.Pcm16bit, Math.Max(minimum, sampleRate * channels * sizeof(short) / 10),
                AudioTrackMode.Stream);
#pragma warning restore CS0618, CA1422
            if (track.State != AudioTrackState.Initialized)
            {
                track.Dispose();
                return;
            }
            _sampleRate = sampleRate;
            _channels = channels;
            _track = track;
            track.SetVolume(_muted ? 0f : (float)_volume);
            track.Play();
        }
    }

    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        lock (_sync)
        {
            if (_disposed || _paused || _track is null) return;
            var copy = samples.ToArray();
            var queued = (_formatGeneration, copy);
            if (!_queue.TryAdd(queued))
            {
                _queue.TryTake(out _);
                _queue.TryAdd(queued);
            }
        }
    }

    public void SetVolume(double volume, bool isMuted)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume, 0d, 1d);
            _muted = isMuted;
            _track?.SetVolume(_muted ? 0f : (float)_volume);
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _paused = true;
            ClearQueue();
            _track?.Pause();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            _track?.Play();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ClearQueue();
            _track?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            ClearQueue();
            try { _track?.Stop(); } catch (Exception) { }
        }
        _writer.Join();
        lock (_sync) ReleaseTrack();
        _queue.Dispose();
    }

    private void WriteQueuedSamples()
    {
        foreach (var queued in _queue.GetConsumingEnumerable())
        {
            var (generation, samples) = queued;
            AudioTrack? track;
            lock (_sync)
            {
                if (_disposed || _paused || generation != _formatGeneration) continue;
                track = _track;
            }
            if (track is null) continue;
            try
            {
                var offset = 0;
                while (offset < samples.Length && !_disposed && !_paused && generation == Volatile.Read(ref _formatGeneration))
                {
                    var written = track.Write(samples, offset, samples.Length - offset, WriteMode.Blocking);
                    if (written <= 0) break;
                    offset += written;
                }
            }
            catch (Exception)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_track, track)) ReleaseTrack();
                }
            }
        }
    }

    private void ClearQueue()
    {
        while (_queue.TryTake(out _)) { }
    }

    private void ReleaseTrack()
    {
        var track = _track;
        _track = null;
        if (track is null) return;
        try { track.Stop(); } catch (Exception) { }
        track.Dispose();
    }
}
#endif
