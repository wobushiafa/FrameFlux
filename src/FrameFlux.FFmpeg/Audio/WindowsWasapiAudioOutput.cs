#if !ANDROID
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameFlux.FFmpeg;

internal sealed class WindowsWasapiAudioOutput : IAudioOutput, ISlicedAudioOutput
{
    private const int MaxWasapiEndpointLatencyMilliseconds = 50;
    private readonly object _sync = new();
    private readonly object _recoverySync = new();
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
    private volatile bool _isOperational;

    internal WindowsWasapiAudioOutput(
        int sampleRate,
        int channels,
        AudioOutputConfiguration configuration)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _configuration = configuration;
        var backend = CreateBackend();
        AttachBackendLocked(backend);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long PlayedFrames
    {
        get
        {
            var provider = Volatile.Read(ref _provider);
            var bufferedFrames = (provider?.BufferedBytes ?? 0) /
                Math.Max(1, Channels * sizeof(short));
            return Math.Max(0, Interlocked.Read(ref _submittedFrames) - bufferedFrames);
        }
    }

    public bool IsOperational => _isOperational;

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
                    _isOperational,
                    _recoveryCount,
                    _lastError);
            }
        }
    }

    public void Write(byte[] pcm) => Write(pcm, 0, pcm.Length);

    public void Write(byte[] pcm, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (NeedsRecovery())
        {
            Recover();
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var provider = _provider;
            if (_needsRecovery || _output is null || provider is null)
            {
                return;
            }

            try
            {
                var availableBytes = Math.Max(0, provider.BufferLength - provider.BufferedBytes);
                var acceptedBytes = Math.Min(count, availableBytes);
                provider.AddSamples(pcm, offset, count);
                if (acceptedBytes > 0)
                {
                    Interlocked.Add(
                        ref _submittedFrames,
                        acceptedBytes / (Channels * sizeof(short)));
                }
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                _needsRecovery = true;
                _isOperational = false;
                throw;
            }
        }
    }

    public void Reset()
    {
        if (NeedsRecovery())
        {
            Recover();
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_provider is not null && !_needsRecovery && _output is not null)
            {
                _provider.ClearBuffer();
                Interlocked.Exchange(ref _submittedFrames, 0);
            }
        }
    }

    public void Dispose()
    {
        BackendResources detached;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isOperational = false;
            detached = DetachBackendLocked();
        }

        DisposeBackendResources(detached);
    }

    private bool NeedsRecovery()
    {
        lock (_sync)
        {
            return !_disposed && (_needsRecovery || _output is null || _provider is null);
        }
    }

    private void Recover()
    {
        lock (_recoverySync)
        {
            BackendResources oldBackend;
            lock (_sync)
            {
                if (_disposed || (!_needsRecovery && _output is not null && _provider is not null))
                {
                    return;
                }

                oldBackend = DetachBackendLocked();
                _isOperational = false;
            }

            DisposeBackendResources(oldBackend);

            BackendResources newBackend;
            try
            {
                newBackend = CreateBackend();
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _lastError = exception.Message;
                    _needsRecovery = true;
                    _isOperational = false;
                }
                throw new InvalidOperationException("WASAPI audio output recovery failed.", exception);
            }

            var disposedMeanwhile = false;
            lock (_sync)
            {
                if (_disposed)
                {
                    disposedMeanwhile = true;
                }
                else
                {
                    AttachBackendLocked(newBackend);
                    _recoveryCount++;
                }
            }

            if (disposedMeanwhile)
            {
                DisposeBackendResources(newBackend);
            }
        }
    }

    private BackendResources CreateBackend()
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = _configuration.OutputDeviceId is { } deviceId
                ? enumerator.GetDevice(deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var resolvedId = device.ID;
            var resolvedName = device.FriendlyName;

            var requestedMs = Math.Max(10, checked((int)Math.Ceiling(
                _configuration.BufferDuration.TotalMilliseconds)));
            var wasapiLatency = Math.Min(requestedMs, MaxWasapiEndpointLatencyMilliseconds);
            var provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(Math.Max(requestedMs * 4, 500)),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            output = new WasapiOut(
                device,
                AudioClientShareMode.Shared,
                useEventSync: true,
                wasapiLatency);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(provider);
            RestoreApplicationSessionVolume(device);
            output.Play();
            return new BackendResources(enumerator, device, output, provider, resolvedId, resolvedName);
        }
        catch
        {
            if (output is not null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                output.Dispose();
            }
            device?.Dispose();
            enumerator?.Dispose();
            throw;
        }
    }

    private void AttachBackendLocked(BackendResources backend)
    {
        _enumerator = backend.Enumerator;
        _device = backend.Device;
        _output = backend.Output;
        Volatile.Write(ref _provider, backend.Provider);
        _deviceId = backend.DeviceId;
        _deviceName = backend.DeviceName;
        _needsRecovery = false;
        _isOperational = !_disposed;
        Interlocked.Exchange(ref _submittedFrames, 0);
    }

    private BackendResources DetachBackendLocked()
    {
        var detached = new BackendResources(_enumerator, _device, _output, _provider, _deviceId, _deviceName);
        _output = null;
        Volatile.Write(ref _provider, null);
        _device = null;
        _enumerator = null;
        return detached;
    }

    private void DisposeBackendResources(BackendResources resources)
    {
        if (resources.Output is not null)
        {
            resources.Output.PlaybackStopped -= OnPlaybackStopped;
            resources.Output.Dispose();
        }

        resources.Device?.Dispose();
        resources.Enumerator?.Dispose();
    }

    private void RestoreApplicationSessionVolume(MMDevice device)
    {
        AudioSessionManager? sessionManager = null;
        try
        {
            sessionManager = device.AudioSessionManager;
            sessionManager.SimpleAudioVolume.Volume = 1f;
        }
        catch (Exception exception)
        {
            var message = $"Unable to restore the application audio session volume: {exception.Message}";
            lock (_sync)
            {
                _lastError = message;
            }
            System.Diagnostics.Debug.WriteLine(message);
        }
        finally
        {
            sessionManager?.Dispose();
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
            _isOperational = false;
        }
    }

    private readonly record struct BackendResources(
        MMDeviceEnumerator? Enumerator,
        MMDevice? Device,
        WasapiOut? Output,
        BufferedWaveProvider? Provider,
        string? DeviceId,
        string? DeviceName);
}
#endif
