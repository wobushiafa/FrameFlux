using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace FrameFlux.WebRtc;

/// <summary>
/// Buffered Windows audio output for decoded WebRTC PCM samples.
/// </summary>
public sealed class WebRtcWaveOutAudioOutput : IWebRtcAudioOutput
{
    private static readonly TimeSpan MaximumBufferedDuration = TimeSpan.FromMilliseconds(250);
    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _provider;
    private int _sampleRate;
    private int _channels;
    private double _volume = 1d;
    private bool _isMuted;
    private bool _disposed;

    public bool IsSupported => OperatingSystem.IsWindows();

    public void EnsureFormat(int sampleRate, int channels)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 4000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_output is not null && _sampleRate == sampleRate && _channels == channels)
            {
                return;
            }

            ReleaseBackendUnsafe();

            try
            {
                var provider = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                var output = new WaveOutEvent
                {
                    DesiredLatency = 100,
                    NumberOfBuffers = 3
                };
                output.Init(provider);
                output.Volume = GetEffectiveVolumeUnsafe();

                _sampleRate = sampleRate;
                _channels = channels;
                _provider = provider;
                _output = output;
                output.Play();
            }
            catch
            {
                ReleaseBackendUnsafe();
            }
        }
    }

    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        if (!OperatingSystem.IsWindows() || samples.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            var provider = _provider;
            if (_disposed || provider is null)
            {
                return;
            }

            // Sender and sound-device clocks drift independently. Drop accumulated
            // latency before the bounded buffer overflows instead of retaining stale audio.
            if (provider.BufferedDuration >= MaximumBufferedDuration)
            {
                provider.ClearBuffer();
            }

            var byteLength = checked(samples.Length * sizeof(short));
            var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
            try
            {
                MemoryMarshal.AsBytes(samples).CopyTo(buffer);
                provider.AddSamples(buffer, 0, byteLength);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void SetVolume(double volume, bool isMuted)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume, 0d, 1d);
            _isMuted = isMuted;
            if (_output is not null)
            {
                _output.Volume = GetEffectiveVolumeUnsafe();
            }
        }
    }

    public void Pause()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                _output?.Pause();
            }
        }
    }

    public void Resume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                _output?.Play();
            }
        }
    }

    public void Reset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                _provider?.ClearBuffer();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseBackendUnsafe();
        }
    }

    private float GetEffectiveVolumeUnsafe() =>
        _isMuted ? 0f : (float)_volume;

    private void ReleaseBackendUnsafe()
    {
        var output = _output;
        _output = null;
        _provider = null;
        _sampleRate = 0;
        _channels = 0;

        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
        }
        catch
        {
            // The audio device may already be unavailable.
        }

        output.Dispose();
    }
}
