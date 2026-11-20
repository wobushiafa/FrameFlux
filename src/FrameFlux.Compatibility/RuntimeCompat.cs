global using FrameFlux.Compatibility;
#if NETSTANDARD2_1
global using OperatingSystem = FrameFlux.Compatibility.Platform;
global using NativeLibrary = FrameFlux.Compatibility.NativeLibraryCompat;
#endif

namespace FrameFlux.Compatibility
{
    internal static class RuntimeCompat
    {
        internal static void NotNull(object? value, string name)
        {
            if (value is null) throw new System.ArgumentNullException(name);
        }

        internal static void NotBlank(string? value, string name)
        {
            NotNull(value, name);
            if (string.IsNullOrWhiteSpace(value)) throw new System.ArgumentException("The value cannot be empty or whitespace.", name);
        }

        internal static void NotDisposed(bool disposed, object instance)
        {
            if (disposed) throw new System.ObjectDisposedException(instance.GetType().FullName);
        }

        internal static void AtLeast(long value, long minimum, string name)
        {
            if (value < minimum) throw new System.ArgumentOutOfRangeException(name, value, $"Must be at least {minimum}.");
        }

        internal static void AtMost(long value, long maximum, string name)
        {
            if (value > maximum) throw new System.ArgumentOutOfRangeException(name, value, $"Must be at most {maximum}.");
        }

        internal static bool IsDefined<T>(T value) where T : struct, System.Enum => System.Enum.IsDefined(typeof(T), value);
        internal static byte[] AllocateBytes(int length)
        {
#if NETSTANDARD2_1
            return new byte[length];
#else
            return System.GC.AllocateUninitializedArray<byte>(length);
#endif
        }
        internal static System.Threading.Tasks.ValueTask<T> Result<T>(T value) => new(value);
        internal static System.Threading.Tasks.ValueTask Failure(System.Exception exception) => new(System.Threading.Tasks.Task.FromException(exception));
        internal static string Invariant(System.FormattableString value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal static string TrimDirectorySeparator(string path)
        {
            var root = System.IO.Path.GetPathRoot(path);
            if (path.Length > (root?.Length ?? 0) && (path[path.Length - 1] == System.IO.Path.DirectorySeparatorChar || path[path.Length - 1] == System.IO.Path.AltDirectorySeparatorChar))
                return path.Substring(0, path.Length - 1);
            return path;
        }
        internal static System.TimeSpan Elapsed(long start) => Elapsed(start, System.Diagnostics.Stopwatch.GetTimestamp());
        internal static System.TimeSpan Elapsed(long start, long end)
        {
#if NETSTANDARD2_1
            return System.TimeSpan.FromSeconds((double)(end - start) / System.Diagnostics.Stopwatch.Frequency);
#else
            return System.Diagnostics.Stopwatch.GetElapsedTime(start, end);
#endif
        }
        private static readonly System.Random Jitter = new();
        internal static int NextRandom(int maximum)
        {
#if NETSTANDARD2_1
            lock (Jitter) return Jitter.Next(maximum);
#else
            return System.Random.Shared.Next(maximum);
#endif
        }
        internal static string RuntimeIdentifier =>
#if NETSTANDARD2_1
            (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "win" :
             System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "osx" : "linux") + "-" +
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
#else
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
#endif

#if NETSTANDARD2_1
        // Cancel the wait, never the underlying operation. Dispose the registration when either wins.
        internal static async System.Threading.Tasks.Task WaitAsync(this System.Threading.Tasks.Task task, System.Threading.CancellationToken token)
        {
            if (!token.CanBeCanceled || task.IsCompleted) { await task.ConfigureAwait(false); return; }
            token.ThrowIfCancellationRequested();
            var cancelled = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await System.Threading.Tasks.Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new System.OperationCanceledException(token);
                await task.ConfigureAwait(false);
            }
        }
        internal static async System.Threading.Tasks.Task<T> WaitAsync<T>(this System.Threading.Tasks.Task<T> task, System.Threading.CancellationToken token)
        {
            await ((System.Threading.Tasks.Task)task).WaitAsync(token).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
#endif
    }
}

#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
#endif
