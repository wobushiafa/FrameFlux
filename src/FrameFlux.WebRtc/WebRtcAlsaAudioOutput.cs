using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FrameFlux.WebRtc;

/// <summary>Buffered ALSA output for WebRTC PCM on Linux.</summary>
public sealed class WebRtcAlsaAudioOutput : IWebRtcAudioOutput
{
    private const int MaximumQueuedChunks = 12;
    private readonly object _sync = new();
    private readonly BlockingCollection<(int Generation, short[] Buffer, int Count)> _queue = new(MaximumQueuedChunks);
    private readonly ManualResetEventSlim _wakeEvent = new(false);
    private readonly Thread _writer;
    private IntPtr _pcm;
    private int _sampleRate;
    private int _channels;
    private double _volume = 1d;
    private bool _muted;
    private bool _paused;
    private bool _disposed;
    private int _formatGeneration;

    public WebRtcAlsaAudioOutput()
    {
        _writer = new Thread(WriteQueuedSamples)
        {
            IsBackground = true,
            Name = "FrameFlux WebRTC ALSA audio"
        };
        _writer.Start();
    }

    public bool IsSupported => OperatingSystem.IsLinux() && AlsaAvailable.Value;

    private static readonly Lazy<bool> AlsaAvailable = new(() =>
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libasound.so.2", out var library)) return false;
        NativeLibrary.Free(library);
        return true;
    });

    public void EnsureFormat(int sampleRate, int channels)
    {
        if (!IsSupported) return;
        RuntimeCompat.AtLeast(sampleRate, 4000, nameof(sampleRate));
        RuntimeCompat.AtLeast(channels, 1, nameof(channels));
        RuntimeCompat.AtMost(channels, 2, nameof(channels));

        lock (_sync)
        {
            if (_disposed || _pcm != IntPtr.Zero && _sampleRate == sampleRate && _channels == channels) return;
            _formatGeneration++;
            ClearQueue();
            CloseDevice();
            if (snd_pcm_open(out var pcm, "default", 0, 1) < 0) return;
            if (snd_pcm_set_params(pcm, 2, 3, (uint)channels, (uint)sampleRate, 1, 100_000) < 0)
            {
                _ = snd_pcm_close(pcm);
                return;
            }

            _pcm = pcm;
            _sampleRate = sampleRate;
            _channels = channels;
            _wakeEvent.Set();
        }
    }

    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        if (!IsSupported || samples.IsEmpty) return;
        lock (_sync)
        {
            if (_disposed || _paused || _pcm == IntPtr.Zero) return;
            var volume = _muted ? 0d : _volume;
            var count = samples.Length;
            var buffer = ArrayPool<short>.Shared.Rent(count);
            if (volume <= 0d)
            {
                buffer.AsSpan(0, count).Clear();
            }
            else if (volume >= 1d)
            {
                samples.CopyTo(buffer.AsSpan(0, count));
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    buffer[i] = (short)Math.Clamp(Math.Round(samples[i] * volume), short.MinValue, short.MaxValue);
                }
            }

            var queued = (_formatGeneration, buffer, count);
            if (!_queue.TryAdd(queued))
            {
                if (_queue.TryTake(out var dropped))
                {
                    ArrayPool<short>.Shared.Return(dropped.Buffer);
                }
                if (!_queue.TryAdd(queued))
                {
                    ArrayPool<short>.Shared.Return(buffer);
                }
            }
        }
    }

    public void SetVolume(double volume, bool isMuted)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume, 0d, 1d);
            _muted = isMuted;
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _paused = true;
            ClearQueue();
            if (_pcm != IntPtr.Zero) _ = snd_pcm_drop(_pcm);
            _wakeEvent.Set();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            if (_pcm != IntPtr.Zero) _ = snd_pcm_prepare(_pcm);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ClearQueue();
            if (_pcm != IntPtr.Zero)
            {
                _ = snd_pcm_drop(_pcm);
                _ = snd_pcm_prepare(_pcm);
            }
            _wakeEvent.Set();
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
            if (_pcm != IntPtr.Zero) _ = snd_pcm_drop(_pcm);
            _wakeEvent.Set();
        }

        _writer.Join();
        lock (_sync) CloseDevice();
        _queue.Dispose();
        _wakeEvent.Dispose();
    }

    private unsafe void WriteQueuedSamples()
    {
        foreach (var queued in _queue.GetConsumingEnumerable())
        {
            var (generation, samples, count) = queued;
            try
            {
                var totalBytes = count * sizeof(short);
                var offset = 0;
                fixed (short* basePtr = samples)
                {
                    var bytePtr = (byte*)basePtr;
                    while (offset < totalBytes)
                    {
                        nint written;
                        lock (_sync)
                        {
                            if (_disposed || _paused || _pcm == IntPtr.Zero || generation != _formatGeneration) break;
                            var frameSize = _channels * sizeof(short);
                            written = snd_pcm_writei(_pcm, (IntPtr)(bytePtr + offset),
                                (nuint)((totalBytes - offset) / frameSize));
                            if (written < 0 && written != -11 && snd_pcm_recover(_pcm, (int)written, 1) < 0) break;
                            if (written > 0) offset += (int)written * frameSize;
                        }
                        if (written <= 0)
                        {
                            _wakeEvent.Reset();
                            _wakeEvent.Wait(5);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<short>.Shared.Return(samples);
            }
        }
    }

    private void ClearQueue()
    {
        while (_queue.TryTake(out var dropped))
        {
            ArrayPool<short>.Shared.Return(dropped.Buffer);
        }
    }

    private void CloseDevice()
    {
        if (_pcm == IntPtr.Zero) return;
        _ = snd_pcm_drop(_pcm);
        _ = snd_pcm_close(_pcm);
        _pcm = IntPtr.Zero;
    }

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern nint snd_pcm_writei(IntPtr pcm, IntPtr buffer, nuint frames);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_recover(IntPtr pcm, int error, int silent);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_drop(IntPtr pcm);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_prepare(IntPtr pcm);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_close(IntPtr pcm);
}
