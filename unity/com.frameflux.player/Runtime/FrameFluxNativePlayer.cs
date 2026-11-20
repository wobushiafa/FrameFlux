using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrameFlux.Unity
{
    public enum FrameFluxPlaybackState
    {
        Idle, Opening, Ready, Playing, Paused, Reconnecting, Stopping, Stopped, Faulted, Buffering
    }

    /// <summary>Windows x64 native player. Commands run off the Unity main thread.</summary>
    public sealed class FrameFluxNativePlayer : IAsyncDisposable
    {
        private readonly SemaphoreSlim commands = new SemaphoreSlim(1, 1);
        private long handle;
        private long sequence;
        private bool disposed;

        private FrameFluxNativePlayer(long handle) { this.handle = handle; }

        public static Task<FrameFluxNativePlayer> CreateAsync(string nativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(nativeDirectory)) throw new ArgumentException("Native directory is required.");
            return Task.Run(() =>
            {
                if (Native.AbiVersion() != 1) throw new NotSupportedException("Unsupported FrameFlux native ABI.");
                long created;
                Check(Native.Create(Utf8(nativeDirectory), out created));
                return new FrameFluxNativePlayer(created);
            });
        }

        public Task OpenAsync(string source, bool enableAudio = true)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Media source is required.");
            return ExecuteAsync(() => Native.Open(handle, Utf8(source), enableAudio ? 1 : 0));
        }

        public Task PlayAsync() => ExecuteAsync(() => Native.Command(handle, 0, 0));
        public Task PauseAsync() => ExecuteAsync(() => Native.Command(handle, 1, 0));
        public Task StopAsync() => ExecuteAsync(() => Native.Command(handle, 2, 0));
        public Task SeekAsync(double seconds) => ExecuteAsync(() => Native.Command(handle, 3, seconds));
        public Task SetVolumeAsync(double volume) => ExecuteAsync(() => Native.Command(handle, 4, volume));
        public Task SetMutedAsync(bool muted) => ExecuteAsync(() => Native.Command(handle, 5, muted ? 1 : 0));
        public Task SetPlaybackRateAsync(double rate) => ExecuteAsync(() => Native.Command(handle, 6, rate));

        // Returns false while a command is running. Poll only on the Unity main thread.
        public bool TryGetStatus(out FrameFluxPlaybackState state, out double position, out double duration)
        {
            state = FrameFluxPlaybackState.Idle;
            position = 0;
            duration = -1;
            if (!commands.Wait(0)) return false;
            try
            {
                ThrowIfDisposed();
                int nativeState;
                var result = Native.Status(handle, out nativeState, out position, out duration);
                Check(result);
                if (result == 3) return false;
                state = (FrameFluxPlaybackState)nativeState;
                return true;
            }
            finally { commands.Release(); }
        }

        public bool TryCopyFrame(ref byte[] rgba, out int width, out int height)
        {
            width = height = 0;
            if (!commands.Wait(0)) return false;
            try
            {
                ThrowIfDisposed();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var result = Native.CopyFrame(handle, rgba, rgba == null ? 0 : rgba.Length,
                        out width, out height, ref sequence);
                    Check(result);
                    if (result == 1)
                    {
                        var exactLength = checked(width * height * 4);
                        if (rgba!.Length != exactLength) Array.Resize(ref rgba, exactLength);
                        return true;
                    }
                    if (result != 2) return false;
                    var length = checked(width * height * 4);
                    if (length <= 0 || length > 256 * 1024 * 1024)
                        throw new InvalidOperationException("Video frame exceeds the 256 MiB upload limit.");
                    rgba = new byte[length];
                }
                return false;
            }
            finally { commands.Release(); }
        }

        public string GetPlaybackError()
        {
            if (!commands.Wait(0)) return string.Empty;
            try { ThrowIfDisposed(); return ReadError(handle); }
            finally { commands.Release(); }
        }

        public bool TryGetAudioStatus(out string backend, out bool operational)
        {
            backend = "None";
            operational = false;
            if (!commands.Wait(0)) return false;
            try
            {
                ThrowIfDisposed();
                int nativeBackend, active;
                var result = Native.AudioStatus(handle, out nativeBackend, out active);
                Check(result);
                if (result == 3) return false;
                backend = nativeBackend == 1 ? "waveOut" : nativeBackend == 2 ? "WASAPI" : "None";
                operational = active != 0;
                return true;
            }
            finally { commands.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                disposed = true;
                var released = handle;
                handle = 0;
                await Task.Run(() => Check(Native.Destroy(released))).ConfigureAwait(false);
            }
            finally { commands.Release(); }
        }

        private async Task ExecuteAsync(Func<int> action)
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                // Error retrieval must happen on the same OS thread as the failed call.
                await Task.Run(() => Check(action())).ConfigureAwait(false);
            }
            finally { commands.Release(); }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(FrameFluxNativePlayer));
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

        private static void Check(int result)
        {
            if (result < 0) throw new InvalidOperationException(ReadError(0));
        }

        private static string ReadError(long player)
        {
            var bytes = new byte[4096];
            Native.Error(player, bytes, bytes.Length);
            var length = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
        }

        private static class Native
        {
            private const string Library = "FrameFluxUnity";
            [DllImport(Library, EntryPoint = "ffu_abi_version", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AbiVersion();
            [DllImport(Library, EntryPoint = "ffu_create", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Create(byte[] directory, out long player);
            [DllImport(Library, EntryPoint = "ffu_destroy", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Destroy(long player);
            [DllImport(Library, EntryPoint = "ffu_open", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Open(long player, byte[] source, int audioEnabled);
            [DllImport(Library, EntryPoint = "ffu_command", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Command(long player, int command, double value);
            [DllImport(Library, EntryPoint = "ffu_status", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Status(long player, out int state, out double position, out double duration);
            [DllImport(Library, EntryPoint = "ffu_audio_status", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AudioStatus(long player, out int backend, out int operational);
            [DllImport(Library, EntryPoint = "ffu_copy_frame", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int CopyFrame(long player, [Out] byte[] rgba, int capacity,
                out int width, out int height, ref long sequence);
            [DllImport(Library, EntryPoint = "ffu_error", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Error(long player, [Out] byte[] destination, int capacity);
        }
    }
}
