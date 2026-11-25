#if !ANDROID
using System.Globalization;
using System.Runtime.InteropServices;

namespace FrameFlux.Capture;

internal sealed record LinuxAudioEndpointCandidate(
    string DeviceId,
    string DisplayName,
    string? UsbSysfsPath,
    string? UsbVidPid,
    string? CardName);

internal sealed unsafe class LinuxAlsaAudioBridge : ICaptureAudioLoopback
{
    private const string LibAsound = "libasound.so.2";
    private const string LibC = "libc";
    private const string SoundSysfsRoot = "/sys/class/sound";
    private const int SndPcmStreamPlayback = 0;
    private const int SndPcmStreamCapture = 1;
    private const int SndPcmFormatS16Le = 2;
    private const int SndPcmAccessRwInterleaved = 3;

    private readonly object _sync = new();
    private readonly string? _captureDeviceId;
    private readonly string? _renderDeviceId;
    private readonly TimeSpan _bufferDuration;
    private Thread? _loopThread;
    private IntPtr _captureHandle;
    private IntPtr _playbackHandle;
    private string? _resolvedCaptureDeviceName;
    private int _sampleRate;
    private int _channels;
    private double _volume;
    private bool _isMuted;
    private double _gainDecibels;
    private double _effectiveScale;
    private bool _isOperational;
    private string? _lastError;
    private int _stopping;
    private bool _disposed;

    internal LinuxAlsaAudioBridge(
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
                    "ALSA",
                    _captureDeviceId ?? "default",
                    _resolvedCaptureDeviceName ?? (_captureDeviceId ?? "default"),
                    _sampleRate,
                    _channels,
                    _bufferDuration,
                    TimeSpan.Zero,
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
            if (_disposed || _loopThread is not null)
            {
                return;
            }

            Volatile.Write(ref _stopping, 0);
            var captureName = string.IsNullOrWhiteSpace(_captureDeviceId) ? "default" : _captureDeviceId!;
            var playbackName = string.IsNullOrWhiteSpace(_renderDeviceId) ? "default" : _renderDeviceId!;
            var latencyUs = checked((uint)Math.Clamp(Math.Ceiling(_bufferDuration.TotalMilliseconds * 1000d), 20_000d, 500_000d));

            IntPtr captureHandle = IntPtr.Zero;
            IntPtr playbackHandle = IntPtr.Zero;
            try
            {
                var openCapture = snd_pcm_open(out captureHandle, captureName, SndPcmStreamCapture, 0);
                if (openCapture < 0)
                {
                    throw new InvalidOperationException($"snd_pcm_open(capture '{captureName}') failed with ALSA code {openCapture}.");
                }

                var openPlayback = snd_pcm_open(out playbackHandle, playbackName, SndPcmStreamPlayback, 0);
                if (openPlayback < 0)
                {
                    throw new InvalidOperationException($"snd_pcm_open(playback '{playbackName}') failed with ALSA code {openPlayback}.");
                }

                var (sampleRate, channels) = ConfigureMatchingParams(captureHandle, playbackHandle, latencyUs);

                _captureHandle = captureHandle;
                _playbackHandle = playbackHandle;
                _resolvedCaptureDeviceName = captureName;
                _sampleRate = sampleRate;
                _channels = channels;
                _isOperational = true;
                _lastError = null;

                _loopThread = new Thread(() => RunLoopbackThread(captureHandle, playbackHandle, sampleRate, channels))
                {
                    IsBackground = true,
                    Name = $"FrameFlux.Capture.ALSA ({captureName})"
                };
                _loopThread.Start();
            }
            catch (Exception exception)
            {
                if (captureHandle != IntPtr.Zero)
                {
                    _ = snd_pcm_close(captureHandle);
                }

                if (playbackHandle != IntPtr.Zero)
                {
                    _ = snd_pcm_close(playbackHandle);
                }

                _isOperational = false;
                _lastError = exception.Message;
            }
        }
    }

    private static (int SampleRate, int Channels) ConfigureMatchingParams(
        IntPtr captureHandle,
        IntPtr playbackHandle,
        uint latencyUs)
    {
        var profiles = new (uint Rate, uint Channels)[]
        {
            (48000, 2),
            (48000, 1),
            (44100, 2),
            (44100, 1),
            (16000, 1)
        };

        var lastError = 0;
        foreach (var (rate, channels) in profiles)
        {
            var capRc = snd_pcm_set_params(
                captureHandle,
                SndPcmFormatS16Le,
                SndPcmAccessRwInterleaved,
                channels,
                rate,
                1,
                latencyUs);
            if (capRc < 0)
            {
                lastError = capRc;
                continue;
            }

            var playRc = snd_pcm_set_params(
                playbackHandle,
                SndPcmFormatS16Le,
                SndPcmAccessRwInterleaved,
                channels,
                rate,
                1,
                latencyUs);
            if (playRc >= 0)
            {
                return ((int)rate, (int)channels);
            }

            lastError = playRc;
        }

        throw new InvalidOperationException($"Unable to negotiate ALSA PCM parameters (last ALSA code {lastError}).");
    }

    private void RunLoopbackThread(IntPtr captureHandle, IntPtr playbackHandle, int sampleRate, int channels)
    {
        var chunkFrames = Math.Max(128, sampleRate / 50); // ~20ms slices
        var sampleCount = checked(chunkFrames * channels);
        var buffer = new short[sampleCount];

        fixed (short* bufferPtr = buffer)
        {
            while (Volatile.Read(ref _stopping) == 0)
            {
                var readFrames = snd_pcm_readi(captureHandle, (IntPtr)bufferPtr, (nuint)chunkFrames);
                if (Volatile.Read(ref _stopping) != 0)
                {
                    break;
                }

                if (readFrames < 0)
                {
                    var recovered = snd_pcm_recover(captureHandle, (int)readFrames, 1);
                    if (recovered < 0)
                    {
                        lock (_sync)
                        {
                            _isOperational = false;
                            _lastError = $"ALSA capture read failed ({recovered}).";
                        }
                        break;
                    }
                    continue;
                }

                if (readFrames == 0)
                {
                    continue;
                }

                var frames = checked((int)readFrames);
                var totalSamples = frames * channels;
                ApplyGainS16(bufferPtr, totalSamples, Volatile.Read(ref _effectiveScale));

                var remaining = frames;
                var offsetSamples = 0;
                while (remaining > 0 && Volatile.Read(ref _stopping) == 0)
                {
                    var written = snd_pcm_writei(playbackHandle, (IntPtr)(bufferPtr + offsetSamples), (nuint)remaining);
                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        break;
                    }

                    if (written < 0)
                    {
                        var recovered = snd_pcm_recover(playbackHandle, (int)written, 1);
                        if (recovered < 0)
                        {
                            lock (_sync)
                            {
                                _isOperational = false;
                                _lastError = $"ALSA playback write failed ({recovered}).";
                            }
                            return;
                        }
                        continue;
                    }

                    var writtenInt = checked((int)written);
                    remaining -= writtenInt;
                    offsetSamples += writtenInt * channels;
                }
            }
        }
    }

    internal static void ApplyGainS16(short* samples, int count, double scale)
    {
        if (count <= 0 || Math.Abs(scale - 1d) <= 1e-6)
        {
            return;
        }

        if (scale <= 1e-6)
        {
            new Span<short>(samples, count).Clear();
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var scaled = Math.Round(samples[i] * scale);
            samples[i] = scaled switch
            {
                >= short.MaxValue => short.MaxValue,
                <= short.MinValue => short.MinValue,
                _ => (short)scaled
            };
        }
    }

    public void Stop()
    {
        Thread? thread;
        IntPtr captureHandle;
        IntPtr playbackHandle;

        lock (_sync)
        {
            Volatile.Write(ref _stopping, 1);
            thread = _loopThread;
            _loopThread = null;
            captureHandle = _captureHandle;
            _captureHandle = IntPtr.Zero;
            playbackHandle = _playbackHandle;
            _playbackHandle = IntPtr.Zero;
            _isOperational = false;
        }

        AbortHandle(captureHandle);
        AbortHandle(playbackHandle);

        if (thread is not null && thread != Thread.CurrentThread)
        {
            try
            {
                thread.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort join on shutdown.
            }
        }

        if (captureHandle != IntPtr.Zero)
        {
            _ = snd_pcm_drop(captureHandle);
            _ = snd_pcm_close(captureHandle);
        }

        if (playbackHandle != IntPtr.Zero)
        {
            _ = snd_pcm_drop(playbackHandle);
            _ = snd_pcm_close(playbackHandle);
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
        }

        Stop();
    }

    private static void AbortHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (snd_pcm_abort(handle) >= 0)
            {
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
            return;
        }

        try
        {
            _ = snd_pcm_drop(handle);
        }
        catch
        {
        }
    }

    internal static IReadOnlyList<MediaCaptureAudioDeviceInfo> GetAudioCaptureDevices()
    {
        var candidates = GetAudioCaptureCandidates();
        if (candidates.Count == 0)
        {
            return [];
        }

        var result = new List<MediaCaptureAudioDeviceInfo>(candidates.Count);
        foreach (var candidate in candidates)
        {
            result.Add(new MediaCaptureAudioDeviceInfo(candidate.DeviceId, candidate.DisplayName));
        }

        return result;
    }

    internal static IReadOnlyList<LinuxAudioEndpointCandidate> GetAudioCaptureCandidates()
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        var results = new List<LinuxAudioEndpointCandidate>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // 1. Scan /sys/class/sound/card* for hardware capture cards (provides exact USB topology for auto-pairing).
        if (Directory.Exists(SoundSysfsRoot))
        {
            try
            {
                var cardDirs = Directory.GetDirectories(SoundSysfsRoot, "card*")
                    .Select(static path => (Path: path, Number: TryParseCardNumber(Path.GetFileName(path))))
                    .Where(static item => item.Number.HasValue)
                    .OrderBy(static item => item.Number!.Value);

                foreach (var (cardPath, cardNumber) in cardDirs)
                {
                    var captureSubdevice = FindFirstCapturePcmDeviceNumber(cardPath, cardNumber!.Value);
                    if (!captureSubdevice.HasValue)
                    {
                        continue;
                    }

                    var cardId = TryReadTrimmedFile(Path.Combine(cardPath, "id"));
                    var pcmId = !string.IsNullOrWhiteSpace(cardId)
                        ? $"plughw:CARD={cardId},DEV={captureSubdevice.Value.ToString(CultureInfo.InvariantCulture)}"
                        : $"plughw:{cardNumber.Value.ToString(CultureInfo.InvariantCulture)},{captureSubdevice.Value.ToString(CultureInfo.InvariantCulture)}";

                    var cardName = TryReadAlsaCardFriendlyName(cardNumber.Value) ?? cardId ?? $"ALSA Card {cardNumber.Value}";
                    var (usbPath, vidPid) = LinuxV4l2DeviceEnumerator.TryReadSysfsUsbIdentity(Path.Combine(cardPath, "device"));

                    if (seenIds.Add(pcmId))
                    {
                        results.Add(new LinuxAudioEndpointCandidate(pcmId, cardName, usbPath, vidPid, cardName));
                    }
                }
            }
            catch
            {
                // Fall back to ALSA hint enumeration if sysfs is inaccessible.
            }
        }

        // 2. Also query ALSA PCM hints for software/virtual input endpoints (e.g. pulse / pipewire / default).
        try
        {
            if (snd_device_name_hint(-1, "pcm", out var hints) == 0 && hints != IntPtr.Zero)
            {
                try
                {
                    var ptrs = (IntPtr*)hints;
                    for (var i = 0; ptrs[i] != IntPtr.Zero; i++)
                    {
                        var ioid = GetAndFreeHint(ptrs[i], "IOID");
                        if (!string.Equals(ioid, "Input", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var name = GetAndFreeHint(ptrs[i], "NAME");
                        if (string.IsNullOrWhiteSpace(name) ||
                            name.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                            !seenIds.Add(name))
                        {
                            continue;
                        }

                        var desc = GetAndFreeHint(ptrs[i], "DESC");
                        var label = !string.IsNullOrWhiteSpace(desc)
                            ? desc.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim()
                            : name;
                        results.Add(new LinuxAudioEndpointCandidate(name, label, null, null, label));
                    }
                }
                finally
                {
                    _ = snd_device_name_free_hint(hints);
                }
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return results;
    }

    internal static string? MatchPairedAudioDevice(
        string? videoUsbSysfsPath,
        string? videoVidPid,
        string videoDisplayName,
        IReadOnlyList<LinuxAudioEndpointCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        // 1. Exact physical USB device directory match (disambiguates multiple identical capture cards).
        if (!string.IsNullOrWhiteSpace(videoUsbSysfsPath))
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate.UsbSysfsPath) &&
                    candidate.UsbSysfsPath.Equals(videoUsbSysfsPath, StringComparison.Ordinal))
                {
                    return candidate.DeviceId;
                }
            }
        }

        // 2. USB VID/PID match.
        if (!string.IsNullOrWhiteSpace(videoVidPid))
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate.UsbVidPid) &&
                    candidate.UsbVidPid.Equals(videoVidPid, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.DeviceId;
                }
            }
        }

        // 3. Friendly card name match (only for non-generic names).
        if (!string.IsNullOrWhiteSpace(videoDisplayName))
        {
            var normalized = videoDisplayName.Trim();
            if (normalized.Length >= 4 &&
                !normalized.Equals("USB Camera", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Equals("Integrated Camera", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidate in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(candidate.CardName) &&
                        (candidate.CardName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                         candidate.CardName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains(candidate.CardName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return candidate.DeviceId;
                    }
                }
            }
        }

        return null;
    }

    private static int? TryParseCardNumber(string? name)
    {
        if (string.IsNullOrEmpty(name) ||
            !name.StartsWith("card", StringComparison.Ordinal) ||
            name.Length <= 4)
        {
            return null;
        }

        return int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var num)
            ? num
            : null;
    }

    private static int? FindFirstCapturePcmDeviceNumber(string cardSysfsPath, int cardNumber)
    {
        var prefix = $"pcmC{cardNumber.ToString(CultureInfo.InvariantCulture)}D";
        int? lowest = null;
        foreach (var entry in Directory.GetDirectories(cardSysfsPath, $"{prefix}*c"))
        {
            var name = Path.GetFileName(entry);
            if (name.Length > prefix.Length + 1 &&
                name.EndsWith("c", StringComparison.Ordinal))
            {
                var devSpan = name.AsSpan(prefix.Length, name.Length - prefix.Length - 1);
                if (int.TryParse(devSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var devNum) &&
                    (!lowest.HasValue || devNum < lowest.Value))
                {
                    lowest = devNum;
                }
            }
        }

        return lowest;
    }

    private static string? TryReadAlsaCardFriendlyName(int cardNumber)
    {
        try
        {
            var procPath = $"/proc/asound/card{cardNumber.ToString(CultureInfo.InvariantCulture)}/usbid";
            var idPath = $"/proc/asound/card{cardNumber.ToString(CultureInfo.InvariantCulture)}/id";
            _ = procPath;
            if (File.Exists("/proc/asound/cards"))
            {
                var prefix = $"{cardNumber.ToString(CultureInfo.InvariantCulture)} [";
                foreach (var line in File.ReadLines("/proc/asound/cards"))
                {
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var dashIndex = trimmed.IndexOf(" - ", StringComparison.Ordinal);
                    if (dashIndex >= 0 && dashIndex + 3 < trimmed.Length)
                    {
                        return trimmed.Substring(dashIndex + 3).Trim();
                    }
                }
            }

            return TryReadTrimmedFile(idPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadTrimmedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetAndFreeHint(IntPtr hint, string id)
    {
        var ptr = snd_device_name_get_hint(hint, id);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            free(ptr);
        }
    }

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint snd_pcm_readi(IntPtr pcm, IntPtr buffer, nuint frames);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint snd_pcm_writei(IntPtr pcm, IntPtr buffer, nuint frames);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_recover(IntPtr pcm, int error, int silent);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_abort(IntPtr pcm);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_drop(IntPtr pcm);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_close(IntPtr pcm);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_device_name_hint(int card, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, out IntPtr hints);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr snd_device_name_get_hint(IntPtr hint, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);

    [DllImport(LibAsound, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_device_name_free_hint(IntPtr hints);

    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free(IntPtr ptr);
}
#endif
