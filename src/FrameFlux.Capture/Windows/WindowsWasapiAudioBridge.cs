#if !ANDROID
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameFlux.Capture;

internal sealed record AudioEndpointCandidate(
    string DeviceId,
    string DisplayName,
    string? DeviceFriendlyName,
    string? HardwareHint);

internal sealed partial class WindowsWasapiAudioBridge : ICaptureAudioLoopback
{
    private readonly object _sync = new();
    private readonly string? _captureDeviceId;
    private readonly string? _renderDeviceId;
    private readonly TimeSpan _bufferDuration;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _provider;
    private IWavePlayer? _player;
    private string _backendName = "WASAPI";
    private string? _resolvedCaptureDeviceId;
    private string? _resolvedCaptureDeviceName;
    private int _sampleRate;
    private int _channels;
    private double _volume;
    private bool _isMuted;
    private double _gainDecibels;
    private double _effectiveScale;
    private bool _providerIsFloat32;
    private bool _isOperational;
    private string? _lastError;
    private byte[] _pcmScratch = [];
    private bool _disposed;

    internal WindowsWasapiAudioBridge(
        string? captureDeviceId,
        string? renderDeviceId,
        TimeSpan bufferDuration,
        double volume,
        bool isMuted,
        double gainDecibels)
    {
        _captureDeviceId = captureDeviceId;
        _renderDeviceId = renderDeviceId;
        _bufferDuration = bufferDuration;
        _volume = Math.Clamp(volume, 0d, 1d);
        _isMuted = isMuted;
        _gainDecibels = gainDecibels;
        _effectiveScale = ComputeEffectiveScale(_volume, _isMuted, _gainDecibels);
    }

    public MediaAudioDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new MediaAudioDiagnostics(
                    _backendName,
                    _resolvedCaptureDeviceId,
                    _resolvedCaptureDeviceName,
                    _sampleRate,
                    _channels,
                    _bufferDuration,
                    _provider?.BufferedDuration ?? TimeSpan.Zero,
                    _isOperational,
                    0,
                    _lastError);
            }
        }
    }

    public void SetVolume(double volume, bool isMuted, double gainDecibels)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume, 0d, 1d);
            _isMuted = isMuted;
            _gainDecibels = gainDecibels;
            Volatile.Write(ref _effectiveScale, ComputeEffectiveScale(_volume, _isMuted, _gainDecibels));
        }
    }

    private static double ComputeEffectiveScale(double volume, bool isMuted, double gainDecibels) =>
        isMuted ? 0d : volume * Math.Pow(10d, gainDecibels / 20d);

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _capture is not null)
            {
                return;
            }

            try
            {
                _enumerator = new MMDeviceEnumerator();
                _captureDevice = string.IsNullOrWhiteSpace(_captureDeviceId)
                    ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                    : _enumerator.GetDevice(_captureDeviceId);

                _resolvedCaptureDeviceId = _captureDevice.ID;
                _resolvedCaptureDeviceName = _captureDevice.FriendlyName;

                var capture = new WasapiCapture(_captureDevice);
                var captureFormat = capture.WaveFormat;
                _sampleRate = captureFormat.SampleRate;
                _channels = captureFormat.Channels;

                var isFloat32Capture = IsIeeeFloat(captureFormat);
                var latencyMs = Math.Max(10, checked((int)Math.Ceiling(_bufferDuration.TotalMilliseconds)));
                BufferedWaveProvider provider;
                IWavePlayer player;
                try
                {
                    _renderDevice = string.IsNullOrWhiteSpace(_renderDeviceId)
                        ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : _enumerator.GetDevice(_renderDeviceId);
                    var wasapiFormat = isFloat32Capture
                        ? WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, captureFormat.Channels)
                        : new WaveFormat(captureFormat.SampleRate, 16, captureFormat.Channels);
                    provider = new BufferedWaveProvider(wasapiFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(Math.Max(latencyMs * 4, 400)),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };
                    var wasapiOut = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, useEventSync: true, latencyMs);
                    wasapiOut.Init(provider);
                    _providerIsFloat32 = isFloat32Capture;
                    _backendName = "WASAPI Loopback";
                    player = wasapiOut;
                }
                catch when (string.IsNullOrWhiteSpace(_renderDeviceId))
                {
                    _renderDevice?.Dispose();
                    _renderDevice = null;
                    var pcmFormat = new WaveFormat(captureFormat.SampleRate, 16, captureFormat.Channels);
                    provider = new BufferedWaveProvider(pcmFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(Math.Max(latencyMs * 4, 400)),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };
                    var waveOut = new WaveOutEvent { DesiredLatency = Math.Max(40, latencyMs) };
                    waveOut.Init(provider);
                    _providerIsFloat32 = false;
                    _backendName = "WASAPI->WaveOut";
                    player = waveOut;
                }

                capture.DataAvailable += OnCaptureDataAvailable;
                capture.RecordingStopped += OnCaptureStopped;

                _capture = capture;
                _provider = provider;
                _player = player;
                _lastError = null;
                _isOperational = true;

                player.Play();
                capture.StartRecording();
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                _isOperational = false;
                CleanupUnlocked();
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            CleanupUnlocked();
            _isOperational = false;
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
            CleanupUnlocked();
            _isOperational = false;
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        BufferedWaveProvider? provider;
        WaveFormat? format;
        bool providerIsFloat32;
        lock (_sync)
        {
            if (_disposed || _provider is null || _capture is null)
            {
                return;
            }

            provider = _provider;
            format = _capture.WaveFormat;
            providerIsFloat32 = _providerIsFloat32;
        }

        // Prevent gradual latency drift between USB capture crystal and render endpoint crystal.
        var maxBuffered = TimeSpan.FromMilliseconds(Math.Max(_bufferDuration.TotalMilliseconds * 2.5d, 200d));
        if (provider.BufferedDuration > maxBuffered)
        {
            provider.ClearBuffer();
        }

        var scale = Volatile.Read(ref _effectiveScale);
        if (providerIsFloat32 && IsIeeeFloat(format))
        {
            if (Math.Abs(scale - 1d) <= 1e-6d)
            {
                provider.AddSamples(e.Buffer, 0, e.BytesRecorded);
                return;
            }

            EnsureScratchCapacity(e.BytesRecorded);
            ScaleFloat32Samples(e.Buffer, _pcmScratch, e.BytesRecorded, (float)scale);
            provider.AddSamples(_pcmScratch, 0, e.BytesRecorded);
            return;
        }

        var pcmBytes = ConvertToScaledPcm16(e.Buffer, e.BytesRecorded, format, scale, out var outCount);
        if (outCount > 0)
        {
            provider.AddSamples(pcmBytes, 0, outCount);
        }
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private static bool IsIeeeFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

    private static void ScaleFloat32Samples(byte[] source, byte[] dest, int byteCount, float scale)
    {
        var sampleCount = byteCount / sizeof(float);
        if (scale == 0f)
        {
            Array.Clear(dest, 0, byteCount);
            return;
        }

        var srcFloats = MemoryMarshal.Cast<byte, float>(source.AsSpan(0, sampleCount * sizeof(float)));
        var dstFloats = MemoryMarshal.Cast<byte, float>(dest.AsSpan(0, sampleCount * sizeof(float)));
        var i = 0;
        if (Vector.IsHardwareAccelerated && sampleCount >= Vector<float>.Count)
        {
            var vScale = new Vector<float>(scale);
            var vMin = new Vector<float>(-1f);
            var vMax = new Vector<float>(1f);
            var vecWidth = Vector<float>.Count;
            var vecCount = sampleCount / vecWidth;
            var vecByteLength = vecCount * vecWidth * sizeof(float);
            var srcVectors = MemoryMarshal.Cast<byte, Vector<float>>(source.AsSpan(0, vecByteLength));
            var dstVectors = MemoryMarshal.Cast<byte, Vector<float>>(dest.AsSpan(0, vecByteLength));
            for (var vi = 0; vi < vecCount; vi++)
            {
                dstVectors[vi] = Vector.Min(vMax, Vector.Max(vMin, srcVectors[vi] * vScale));
            }
            i = vecCount * vecWidth;
        }

        for (; i < sampleCount; i++)
        {
            dstFloats[i] = Math.Clamp(srcFloats[i] * scale, -1f, 1f);
        }
    }

    private byte[] ConvertToScaledPcm16(
        byte[] source,
        int bytesRecorded,
        WaveFormat format,
        double scale,
        out int outputBytes)
    {
        outputBytes = 0;
        if (IsIeeeFloat(format))
        {
            var sampleCount = bytesRecorded / sizeof(float);
            outputBytes = sampleCount * sizeof(short);
            EnsureScratchCapacity(outputBytes);

            var srcFloats = MemoryMarshal.Cast<byte, float>(source.AsSpan(0, sampleCount * sizeof(float)));
            var dstShorts = MemoryMarshal.Cast<byte, short>(_pcmScratch.AsSpan(0, outputBytes));
            var fScale = (float)(scale * 32767d);
            for (var i = 0; i < sampleCount; i++)
            {
                var scaled = Math.Clamp(srcFloats[i] * fScale, short.MinValue, short.MaxValue);
                dstShorts[i] = (short)MathF.Round(scaled);
            }

            return _pcmScratch;
        }

        if (format.BitsPerSample == 16)
        {
            if (Math.Abs(scale - 1d) <= 1e-6d)
            {
                outputBytes = bytesRecorded;
                return source;
            }

            var sampleCount = bytesRecorded / sizeof(short);
            outputBytes = sampleCount * sizeof(short);
            EnsureScratchCapacity(outputBytes);

            var srcShorts = MemoryMarshal.Cast<byte, short>(source.AsSpan(0, outputBytes));
            var dstShorts = MemoryMarshal.Cast<byte, short>(_pcmScratch.AsSpan(0, outputBytes));
            for (var i = 0; i < sampleCount; i++)
            {
                var scaled = Math.Clamp(srcShorts[i] * scale, short.MinValue, short.MaxValue);
                dstShorts[i] = (short)Math.Round(scaled);
            }

            return _pcmScratch;
        }

        return [];
    }

    private void EnsureScratchCapacity(int requiredBytes)
    {
        if (_pcmScratch.Length < requiredBytes)
        {
            _pcmScratch = new byte[requiredBytes];
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        lock (_sync)
        {
            _lastError = e.Exception.Message;
            _isOperational = false;
        }
    }

    private void CleanupUnlocked()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnCaptureDataAvailable;
            _capture.RecordingStopped -= OnCaptureStopped;
            try
            {
                _capture.StopRecording();
            }
            catch
            {
                // Ignore stop errors during teardown.
            }

            _capture.Dispose();
            _capture = null;
        }

        if (_player is not null)
        {
            try
            {
                _player.Stop();
            }
            catch
            {
                // Ignore stop errors during teardown.
            }

            _player.Dispose();
            _player = null;
        }

        _provider = null;
        _captureDevice?.Dispose();
        _captureDevice = null;
        _renderDevice?.Dispose();
        _renderDevice = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    internal static IReadOnlyList<MediaCaptureAudioDeviceInfo> GetAudioCaptureDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var list = new List<MediaCaptureAudioDeviceInfo>(devices.Count);
            foreach (var device in devices)
            {
                using (device)
                {
                    list.Add(new MediaCaptureAudioDeviceInfo(device.ID, device.FriendlyName));
                }
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    internal static string? MatchPairedAudioDevice(
        string videoSymbolicLink,
        string videoFriendlyName,
        IReadOnlyList<MediaCaptureAudioDeviceInfo> audioDevices)
    {
        if (audioDevices.Count == 0)
        {
            return null;
        }

        var candidates = new List<AudioEndpointCandidate>(audioDevices.Count);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var item in audioDevices)
            {
                string? devFriendly = null;
                string? hwHint = null;
                try
                {
                    using var mm = enumerator.GetDevice(item.DeviceId);
                    devFriendly = mm.DeviceFriendlyName;
                    hwHint = mm.InstanceId;
                }
                catch
                {
                    // Fallback to basic name matching when MMDevice lookup fails.
                }

                candidates.Add(new AudioEndpointCandidate(item.DeviceId, item.DisplayName, devFriendly, hwHint));
            }
        }
        catch
        {
            foreach (var item in audioDevices)
            {
                candidates.Add(new AudioEndpointCandidate(item.DeviceId, item.DisplayName, null, null));
            }
        }

        return MatchPairedAudioDevice(videoSymbolicLink, videoFriendlyName, candidates);
    }

    internal static string? MatchPairedAudioDevice(
        string videoSymbolicLink,
        string videoFriendlyName,
        IReadOnlyList<AudioEndpointCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var vidPidMatch = VidPidRegex().Match(videoSymbolicLink ?? string.Empty);
        if (vidPidMatch.Success)
        {
            var token = vidPidMatch.Value;
            foreach (var candidate in candidates)
            {
                if ((!string.IsNullOrEmpty(candidate.HardwareHint) &&
                     candidate.HardwareHint.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                    candidate.DeviceId.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    candidate.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.DeviceId;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(videoFriendlyName) &&
            videoFriendlyName.Trim().Length >= 4)
        {
            var normalizedVideoName = videoFriendlyName.Trim();
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate.DeviceFriendlyName) &&
                    (candidate.DeviceFriendlyName.Equals(normalizedVideoName, StringComparison.OrdinalIgnoreCase) ||
                     candidate.DeviceFriendlyName.Contains(normalizedVideoName, StringComparison.OrdinalIgnoreCase) ||
                     normalizedVideoName.Contains(candidate.DeviceFriendlyName, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate.DeviceId;
                }

                if (candidate.DisplayName.Contains($"({normalizedVideoName})", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.DeviceId;
                }
            }
        }

        return null;
    }

#if NET7_0_OR_GREATER
    [GeneratedRegex(@"vid_[0-9a-f]{4}&pid_[0-9a-f]{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();
#else
    private static readonly Regex VidPidRegexInstance = new(
        @"vid_[0-9a-f]{4}&pid_[0-9a-f]{4}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static Regex VidPidRegex() => VidPidRegexInstance;
#endif
}
#endif
