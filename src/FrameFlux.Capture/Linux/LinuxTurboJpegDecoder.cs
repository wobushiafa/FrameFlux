#if !ANDROID
using System.Runtime.InteropServices;

namespace FrameFlux.Capture;

/// <summary>
/// Runtime-probed libturbojpeg decoder for fast SIMD MJPEG -> BGRA32 decoding on Linux.
/// </summary>
internal sealed unsafe class LinuxTurboJpegDecoder : IDisposable
{
    private const int TjpfBgra = 8;
    private const int TjFlagFastUpsample = 256;
    private const int TjFlagFastDct = 2048;

    private static readonly string[] CandidateLibraries =
    [
        "libturbojpeg.so.0",
        "libturbojpeg.so",
        "libturbojpeg.so.0.2.0"
    ];

    private static readonly Lazy<TurboJpegApi?> LoadedApi = new(TryLoadApi, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly TurboJpegApi _api;
    private IntPtr _handle;
    private bool _disposed;

    private LinuxTurboJpegDecoder(TurboJpegApi api, IntPtr handle)
    {
        _api = api;
        _handle = handle;
    }

    internal static bool IsAvailable => LoadedApi.Value is not null;

    internal static LinuxTurboJpegDecoder? TryCreate()
    {
        var api = LoadedApi.Value;
        if (api is null)
        {
            return null;
        }

        var handle = api.InitDecompress();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        return new LinuxTurboJpegDecoder(api, handle);
    }

    internal bool TryDecodeToBgra(
        IntPtr jpegData,
        int jpegLength,
        IntPtr bgraDest,
        int expectedWidth,
        int expectedHeight,
        int bgraStride,
        out string? error)
    {
        error = null;
        if (_disposed || _handle == IntPtr.Zero || jpegData == IntPtr.Zero || bgraDest == IntPtr.Zero ||
            jpegLength < 2 || expectedWidth <= 0 || expectedHeight <= 0 || bgraStride < expectedWidth * 4)
        {
            error = "Invalid MJPEG decode arguments.";
            return false;
        }

        var bytes = (byte*)jpegData;
        if (bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            error = "Missing JPEG SOI marker.";
            return false;
        }

        var headerRc = _api.DecompressHeader3(
            _handle,
            jpegData,
            (nuint)jpegLength,
            out var width,
            out var height,
            out _,
            out _);
        if (headerRc != 0)
        {
            error = ReadLastError();
            return false;
        }

        if (width != expectedWidth || height != expectedHeight)
        {
            error = $"MJPEG frame dimensions ({width}x{height}) do not match negotiated surface ({expectedWidth}x{expectedHeight}).";
            return false;
        }

        var rc = _api.Decompress2(
            _handle,
            jpegData,
            (nuint)jpegLength,
            bgraDest,
            expectedWidth,
            bgraStride,
            expectedHeight,
            TjpfBgra,
            TjFlagFastDct | TjFlagFastUpsample);
        if (rc != 0)
        {
            error = ReadLastError();
            return false;
        }

        return true;
    }

    private string ReadLastError()
    {
        if (_api.GetErrorStr2 is null)
        {
            return "libturbojpeg decode failed.";
        }

        var ptr = _api.GetErrorStr2(_handle);
        return ptr == IntPtr.Zero
            ? "libturbojpeg decode failed."
            : Marshal.PtrToStringUTF8(ptr) ?? "libturbojpeg decode failed.";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = _api.Destroy(handle);
        }
    }

    private static TurboJpegApi? TryLoadApi()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

#if NET8_0_OR_GREATER
        foreach (var libraryName in CandidateLibraries)
        {
            if (!NativeLibrary.TryLoad(libraryName, out var library))
            {
                continue;
            }

            if (NativeLibrary.TryGetExport(library, "tjInitDecompress", out var initPtr) &&
                NativeLibrary.TryGetExport(library, "tjDestroy", out var destroyPtr) &&
                NativeLibrary.TryGetExport(library, "tjDecompressHeader3", out var headerPtr) &&
                NativeLibrary.TryGetExport(library, "tjDecompress2", out var decompressPtr))
            {
                NativeLibrary.TryGetExport(library, "tjGetErrorStr2", out var errPtr);
                return new TurboJpegApi(
                    Marshal.GetDelegateForFunctionPointer<TjInitDecompressDelegate>(initPtr),
                    Marshal.GetDelegateForFunctionPointer<TjDestroyDelegate>(destroyPtr),
                    Marshal.GetDelegateForFunctionPointer<TjDecompressHeader3Delegate>(headerPtr),
                    Marshal.GetDelegateForFunctionPointer<TjDecompress2Delegate>(decompressPtr),
                    errPtr != IntPtr.Zero
                        ? Marshal.GetDelegateForFunctionPointer<TjGetErrorStr2Delegate>(errPtr)
                        : null);
            }

            NativeLibrary.Free(library);
        }

        return null;
#else
        try
        {
            var probe = TjInitDecompressNative();
            if (probe == IntPtr.Zero)
            {
                return null;
            }

            _ = TjDestroyNative(probe);
            return new TurboJpegApi(
                TjInitDecompressNative,
                TjDestroyNative,
                TjDecompressHeader3Native,
                TjDecompress2Native,
                TjGetErrorStr2Native);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
#endif
    }

#if !NET8_0_OR_GREATER
    [DllImport("libturbojpeg.so.0", EntryPoint = "tjInitDecompress", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TjInitDecompressNative();

    [DllImport("libturbojpeg.so.0", EntryPoint = "tjDestroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TjDestroyNative(IntPtr handle);

    [DllImport("libturbojpeg.so.0", EntryPoint = "tjDecompressHeader3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TjDecompressHeader3Native(
        IntPtr handle,
        IntPtr jpegBuf,
        nuint jpegSize,
        out int width,
        out int height,
        out int jpegSubsamp,
        out int jpegColorspace);

    [DllImport("libturbojpeg.so.0", EntryPoint = "tjDecompress2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TjDecompress2Native(
        IntPtr handle,
        IntPtr jpegBuf,
        nuint jpegSize,
        IntPtr dstBuf,
        int width,
        int pitch,
        int height,
        int pixelFormat,
        int flags);

    [DllImport("libturbojpeg.so.0", EntryPoint = "tjGetErrorStr2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TjGetErrorStr2Native(IntPtr handle);
#endif

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TjInitDecompressDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TjDestroyDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TjDecompressHeader3Delegate(
        IntPtr handle,
        IntPtr jpegBuf,
        nuint jpegSize,
        out int width,
        out int height,
        out int jpegSubsamp,
        out int jpegColorspace);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TjDecompress2Delegate(
        IntPtr handle,
        IntPtr jpegBuf,
        nuint jpegSize,
        IntPtr dstBuf,
        int width,
        int pitch,
        int height,
        int pixelFormat,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TjGetErrorStr2Delegate(IntPtr handle);

    private sealed record TurboJpegApi(
        TjInitDecompressDelegate InitDecompress,
        TjDestroyDelegate Destroy,
        TjDecompressHeader3Delegate DecompressHeader3,
        TjDecompress2Delegate Decompress2,
        TjGetErrorStr2Delegate? GetErrorStr2);
}
#endif
