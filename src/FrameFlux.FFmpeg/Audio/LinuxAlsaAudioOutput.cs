using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class LinuxAlsaAudioOutput : IAudioOutput
{
    private IntPtr _handle;
    private long _submittedFrames;
    private readonly AudioOutputConfiguration _configuration;
    private readonly string _deviceName;
    private string? _lastError;

    internal LinuxAlsaAudioOutput(
        int sampleRate,
        int channels,
        AudioOutputConfiguration? configuration = null)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _configuration = configuration ?? new AudioOutputConfiguration(
            null,
            TimeSpan.FromMilliseconds(100));
        _deviceName = _configuration.OutputDeviceId ?? "default";
        ThrowIfFailed(snd_pcm_open(out _handle, _deviceName, 0, 0), "snd_pcm_open");
        try
        {
            var latencyMicroseconds = checked((uint)Math.Ceiling(
                _configuration.BufferDuration.TotalMilliseconds * 1000d));
            ThrowIfFailed(
                snd_pcm_set_params(
                    _handle,
                    2,
                    3,
                    checked((uint)channels),
                    checked((uint)sampleRate),
                    1,
                    latencyMicroseconds),
                "snd_pcm_set_params");
        }
        catch
        {
            _ = snd_pcm_close(_handle);
            _handle = IntPtr.Zero;
            throw;
        }
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public bool IsOperational => _handle != IntPtr.Zero;
    public bool TrySetVolume(double volume, bool muted) => false;
    public MediaAudioDiagnostics Diagnostics
    {
        get
        {
            var queuedFrames = GetQueuedFrames();
            return new MediaAudioDiagnostics(
                "ALSA",
                _configuration.OutputDeviceId,
                _deviceName,
                SampleRate,
                Channels,
                _configuration.BufferDuration,
                TimeSpan.FromSeconds((double)queuedFrames / SampleRate),
                _handle != IntPtr.Zero,
                0,
                _lastError);
        }
    }
    public long PlayedFrames
    {
        get
        {
            var submitted = Interlocked.Read(ref _submittedFrames);
            return _handle != IntPtr.Zero && snd_pcm_delay(_handle, out var delay) >= 0
                ? Math.Max(0, submitted - Math.Max(0, delay))
                : submitted;
        }
    }

    public void Write(byte[] pcm)
    {
        if (_handle == IntPtr.Zero || pcm.Length == 0) return;
        var frameSize = Channels * sizeof(short);
        var remainingFrames = pcm.Length / frameSize;
        var offset = 0;
        var pinned = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            while (remainingFrames > 0)
            {
                var result = snd_pcm_writei(_handle, pinned.AddrOfPinnedObject() + offset, (nuint)remainingFrames);
                if (result < 0)
                {
                    result = snd_pcm_recover(_handle, (int)result, 1);
                    if (result < 0)
                    {
                        _lastError = $"snd_pcm_writei failed with ALSA code {result}.";
                        ThrowIfFailed((int)result, "snd_pcm_writei");
                    }
                    continue;
                }
                var written = checked((int)result);
                remainingFrames -= written;
                offset += written * frameSize;
                Interlocked.Add(ref _submittedFrames, written);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    public void Reset()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        ThrowIfFailed(snd_pcm_drop(_handle), "snd_pcm_drop");
        ThrowIfFailed(snd_pcm_prepare(_handle), "snd_pcm_prepare");
        Interlocked.Exchange(ref _submittedFrames, 0);
    }

    private long GetQueuedFrames()
    {
        return _handle != IntPtr.Zero && snd_pcm_delay(_handle, out var delay) >= 0
            ? Math.Max(0, delay)
            : 0;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        _ = snd_pcm_drop(handle);
        _ = snd_pcm_close(handle);
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation} failed with ALSA code {result}.");
    }

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern nint snd_pcm_writei(IntPtr pcm, IntPtr buffer, nuint frames);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_recover(IntPtr pcm, int error, int silent);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_delay(IntPtr pcm, out long delay);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_drop(IntPtr pcm);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_prepare(IntPtr pcm);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_close(IntPtr pcm);
}
