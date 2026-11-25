#if NETSTANDARD2_1
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameFlux.Compatibility
{
    internal static class Platform
    {
        internal static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        internal static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        internal static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        internal static bool IsAndroid() => RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"));
    }

    // Windows netstandard2.1 loader for explicit native library directories. Handles stay owned by the caller.
    internal static class NativeLibraryCompat
    {
        internal static IntPtr Load(string path)
        {
            if (!Platform.IsWindows()) throw new PlatformNotSupportedException("This native loader supports Windows only.");
            var handle = LoadLibraryExW(path, IntPtr.Zero, System.IO.Path.IsPathRooted(path) ? 0x00001100u : 0x00001000u);
            if (handle == IntPtr.Zero) throw new DllNotFoundException($"Unable to load {path} (Win32 {Marshal.GetLastWin32Error()}).");
            return handle;
        }
        internal static bool TryLoad(string path, out IntPtr handle)
        {
            try { handle = Load(path); return true; }
            catch (DllNotFoundException) { handle = IntPtr.Zero; return false; }
        }
        internal static bool TryLoad(string path, Assembly assembly, DllImportSearchPath? searchPath, out IntPtr handle) => TryLoad(path, out handle);
        internal static IntPtr GetExport(IntPtr handle, string name)
        {
            var address = GetProcAddress(handle, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return address;
        }
        internal static bool TryGetExport(IntPtr handle, string name, out IntPtr address) { address = GetProcAddress(handle, name); return address != IntPtr.Zero; }
        internal static void Free(IntPtr handle) { if (handle != IntPtr.Zero) FreeLibrary(handle); }
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    }
}
#endif
