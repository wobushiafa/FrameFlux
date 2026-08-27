#if !ANDROID
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameFlux.FFmpeg;

internal sealed class WindowsWasapiAudioOutput : IAudioOutput
{
    private readonly object _sync = new();
    private readonly AudioOutputConfiguration _configuration;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiOut? _output;
    private BufferedWaveProvider? _provider;
    private string? _deviceId;
    private string? _deviceName;
    private string? _lastError;
    private long _submittedFrames;
    private int _recoveryCount;
    private bool _needsRecovery;
    private bool _disposed;
    private float _volume = 1f;

    internal WindowsWasapiAudioOutput(
        int sampleRate,
        int channels,
        AudioOutputConfiguration configuration)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _configuration = configuration;
        Initialize();
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long PlayedFrames
    {
        get
        {
            lock (_sync)
            {
                var bufferedFrames = (_provider?.BufferedBytes ?? 0) /
                    Math.Max(1, Channels * sizeof(short));
                return Math.Max(0, Interlocked.Read(ref _submittedFrames) - bufferedFrames);
            }
        }
    }

    public bool IsOperational
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _output is not null && !_needsRecovery;
            }
        }
    }

    public MediaAudioDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new MediaAudioDiagnostics(
                    "WASAPI",
                    _deviceId,
                    _deviceName,
                    SampleRate,
                    Channels,
                    _configuration.BufferDuration,
                    _provider?.BufferedDuration ?? TimeSpan.Zero,
                    !_disposed && _output is not null && !_needsRecovery,
                    _recoveryCount,
                    _lastError);
            }
        }
    }

    public bool TrySetVolume(double volume, bool muted)
    {
        lock (_sync)
        {
            _volume = (float)(muted ? 0d : Math.Clamp(volume, 0d, 1d));
            if (_output is null)
            {
                return false;
            }

            _output.Volume = _volume;
            return true;
        }
    }

    public void Write(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_needsRecovery || _output is null || _provider is null)
            {
                Recover();
            }

            try
            {
                _provider!.AddSamples(pcm, 0, pcm.Length);
                Interlocked.Add(
                    ref _submittedFrames,
                    pcm.Length / (Channels * sizeof(short)));
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                _needsRecovery = true;
                throw;
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
            ReleaseBackend();
        }
    }

    private void Initialize()
    {
        _enumerator = new MMDeviceEnumerator();
        _device = _configuration.OutputDeviceId is { } deviceId
            ? _enumerator.GetDevice(deviceId)
            : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _deviceId = _device.ID;
        _deviceName = _device.FriendlyName;

        var latency = Math.Max(10, checked((int)Math.Ceiling(
            _configuration.BufferDuration.TotalMilliseconds)));
        var provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(latency * 4, 500)),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        var output = new WasapiOut(
            _device,
            AudioClientShareMode.Shared,
            useEventSync: true,
            latency);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(provider);
        output.Volume = _volume;
        output.Play();
        _provider = provider;
        _output = output;
        _needsRecovery = false;
    }

    private void Recover()
    {
        ReleaseBackend();
        try
        {
            Initialize();
            _recoveryCount++;
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _needsRecovery = true;
            throw new InvalidOperationException("WASAPI audio output recovery failed.", exception);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is null)
        {
            return;
        }

        lock (_sync)
        {
            _lastError = args.Exception.Message;
            _needsRecovery = true;
        }
    }

    private void ReleaseBackend()
    {
        var output = _output;
        _output = null;
        _provider = null;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
        }

        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
#endif
