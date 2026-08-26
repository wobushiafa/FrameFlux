using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class LinuxAlsaAudioOutput : IAudioOutput
{
    private IntPtr _handle;
    private long _submittedFrames;

    internal LinuxAlsaAudioOutput(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        ThrowIfFailed(snd_pcm_open(out _handle, "default", 0, 0), "snd_pcm_open");
        try
        {
            ThrowIfFailed(snd_pcm_set_params(_handle, 2, 3, checked((uint)channels), checked((uint)sampleRate), 1, 100000), "snd_pcm_set_params");
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
                    if (result < 0) ThrowIfFailed((int)result, "snd_pcm_writei");
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
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)] private static extern int snd_pcm_close(IntPtr pcm);
}
