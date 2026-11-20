using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FrameFlux.FFmpeg;

namespace FrameFlux.Unity.Native;

// ABI v1: Cdecl, UTF-8 strings, opaque monotonically increasing 64-bit handles.
// Every exported boundary catches managed exceptions. No callbacks enter Unity.
public static unsafe class UnityNativeExports
{
    private static readonly ConcurrentDictionary<long, PlayerEntry> Players = new();
    private static long _nextHandle;
    [ThreadStatic] private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "ffu_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int AbiVersion() => 1;

    [UnmanagedCallersOnly(EntryPoint = "ffu_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int Create(byte* nativeDirectory, long* handle)
    {
        try
        {
            if (handle == null) throw new ArgumentNullException(nameof(handle));
            *handle = 0;
            FFmpegHelper.RegisterFFmpeg(ReadUtf8(nativeDirectory));
            var entry = new PlayerEntry();
            var id = Interlocked.Increment(ref _nextHandle);
            Players[id] = entry;
            *handle = id;
            _lastError = null;
            return 0;
        }
        catch (Exception exception) { return Fail(exception); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ffu_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int Destroy(long handle)
    {
        try
        {
            if (!Players.TryRemove(handle, out var entry)) throw new ArgumentException("Invalid player handle.");
            lock (entry.Commands)
            {
                entry.Disposed = true;
                try { entry.Player.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally { entry.Frames.Clear(); }
            }
            return 0;
        }
        catch (Exception exception) { return Fail(exception); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ffu_open", CallConvs = [typeof(CallConvCdecl)])]
    public static int Open(long handle, byte* source, int audioEnabled)
    {
        try
        {
            var mediaSource = MediaSource.Parse(ReadUtf8(source));
            return Invoke(handle, entry =>
            {
                entry.Player.OpenAsync(mediaSource, new MediaOpenOptions
                {
                    Video = new MediaVideoOptions { DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly },
                    Audio = new MediaAudioOptions { IsEnabled = audioEnabled != 0 }
                }).AsTask().GetAwaiter().GetResult();
                entry.Frames.Clear();
                entry.Error = null;
                return 0;
            });
        }
        catch (Exception exception) { return Fail(exception); }
    }

    // command: 0=play, 1=pause, 2=stop, 3=seek seconds, 4=volume, 5=mute, 6=rate.
    [UnmanagedCallersOnly(EntryPoint = "ffu_command", CallConvs = [typeof(CallConvCdecl)])]
    public static int Command(long handle, int command, double value) => Invoke(handle, entry =>
    {
        switch (command)
        {
            case 0: entry.Player.PlayAsync().AsTask().GetAwaiter().GetResult(); break;
            case 1: entry.Player.PauseAsync().AsTask().GetAwaiter().GetResult(); break;
            case 2:
                entry.Player.StopAsync().AsTask().GetAwaiter().GetResult();
                entry.Frames.Clear();
                break;
            case 3:
                if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                entry.Player.SeekAsync(TimeSpan.FromSeconds(value)).AsTask().GetAwaiter().GetResult();
                entry.Frames.Clear();
                break;
            case 4: entry.Player.Volume = value; break;
            case 5: entry.Player.IsMuted = value != 0; break;
            case 6: entry.Player.PlaybackRate = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        return 0;
    });

    // Polling never waits behind open/stop/seek. 3 means busy; try again next Update.
    [UnmanagedCallersOnly(EntryPoint = "ffu_status", CallConvs = [typeof(CallConvCdecl)])]
    public static int Status(long handle, int* state, double* position, double* duration) => Invoke(handle, entry =>
    {
        if (state == null || position == null || duration == null) throw new ArgumentException("Invalid status arguments.");
        *state = (int)entry.Player.State;
        *position = entry.Player.Position.TotalSeconds;
        *duration = entry.Player.Duration?.TotalSeconds ?? -1;
        return 0;
    }, poll: true);

    // backend: 0=none/other, 1=waveOut, 2=WASAPI. A silent/video-only source reports 0.
    [UnmanagedCallersOnly(EntryPoint = "ffu_audio_status", CallConvs = [typeof(CallConvCdecl)])]
    public static int AudioStatus(long handle, int* backend, int* operational) => Invoke(handle, entry =>
    {
        if (backend == null || operational == null) throw new ArgumentException("Invalid audio status arguments.");
        var audio = entry.Player.Diagnostics.Audio;
        *backend = audio.Backend switch { "waveOut" => 1, "WASAPI" => 2, _ => 0 };
        *operational = audio.IsOperational ? 1 : 0;
        return 0;
    }, poll: true);

    [UnmanagedCallersOnly(EntryPoint = "ffu_copy_frame", CallConvs = [typeof(CallConvCdecl)])]
    public static int CopyFrame(long handle, byte* rgba, int capacity, int* width, int* height, long* sequence) =>
        Invoke(handle, entry => entry.Frames.CopyRgba(rgba, capacity, width, height, sequence), poll: true);

    // Returns the required UTF-8 buffer size INCLUDING NUL. handle=0 reads the last
    // command error on this thread; nonzero reads an asynchronous playback error.
    [UnmanagedCallersOnly(EntryPoint = "ffu_error", CallConvs = [typeof(CallConvCdecl)])]
    public static int Error(long handle, byte* destination, int capacity)
    {
        try
        {
            var message = handle != 0 && Players.TryGetValue(handle, out var entry)
                ? entry.Error ?? string.Empty : _lastError ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(message);
            if (destination != null && capacity > 0)
            {
                var count = Math.Min(bytes.Length, capacity - 1);
                bytes.AsSpan(0, count).CopyTo(new Span<byte>(destination, count));
                destination[count] = 0;
            }
            return checked(bytes.Length + 1);
        }
        catch (Exception exception) { return Fail(exception); }
    }

    private static int Invoke(long handle, Func<PlayerEntry, int> action, bool poll = false)
    {
        var entered = false;
        PlayerEntry? entry = null;
        try
        {
            if (!Players.TryGetValue(handle, out entry)) throw new ArgumentException("Invalid player handle.");
            if (poll) entered = Monitor.TryEnter(entry.Commands);
            else Monitor.Enter(entry.Commands, ref entered);
            if (!entered) return 3;
            ObjectDisposedException.ThrowIf(entry.Disposed, entry);
            var result = action(entry);
            _lastError = null;
            return result;
        }
        catch (Exception exception) { return Fail(exception); }
        finally { if (entered) Monitor.Exit(entry!.Commands); }
    }

    private static string ReadUtf8(byte* value) =>
        value == null ? throw new ArgumentNullException(nameof(value)) : Marshal.PtrToStringUTF8((nint)value)!;

    private static int Fail(Exception exception)
    {
        _lastError = exception.Message;
        return -1;
    }

    private sealed class PlayerEntry
    {
        internal readonly object Commands = new();
        internal readonly UnityFrameMailbox Frames = new();
        internal readonly FfmpegMediaPlayer Player = new();
        internal volatile string? Error;
        internal bool Disposed;

        internal PlayerEntry()
        {
            Player.FrameReceived += (_, frame) => Frames.Present(frame);
            Player.Error += (_, args) => Error = args.Error.Message;
        }
    }
}
