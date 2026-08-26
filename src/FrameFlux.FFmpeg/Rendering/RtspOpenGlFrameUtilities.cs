using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static unsafe class RtspOpenGlFrameUtilities
{
    public static void CopyFrameRows(IntPtr sourceBuffer, IntPtr destinationBuffer, int height, int sourceStride, int destinationStride)
    {
        var source = (byte*)sourceBuffer.ToPointer();
        var destination = (byte*)destinationBuffer.ToPointer();
        var rowBytes = Math.Min(sourceStride, destinationStride);

        for (var y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(
                source + (y * sourceStride),
                destination + (y * destinationStride),
                destinationStride,
                rowBytes);
        }
    }

    public static void CopyBgraToRgba(IntPtr sourceBuffer, IntPtr destinationBuffer, int width, int height, int sourceStride, int destinationStride)
    {
        var source = (byte*)sourceBuffer.ToPointer();
        var destination = (byte*)destinationBuffer.ToPointer();
        var pixelWidth = width * 4;

        for (var y = 0; y < height; y++)
        {
            var sourceRow = source + (y * sourceStride);
            var destinationRow = destination + (y * destinationStride);
            for (var x = 0; x < pixelWidth; x += 4)
            {
                destinationRow[x] = sourceRow[x + 2];
                destinationRow[x + 1] = sourceRow[x + 1];
                destinationRow[x + 2] = sourceRow[x];
                destinationRow[x + 3] = sourceRow[x + 3];
            }
        }
    }

    public static void ApplyOpaqueAlpha(IntPtr buffer, int width, int height, int stride)
    {
        var pointer = (byte*)buffer.ToPointer();
        for (var y = 0; y < height; y++)
        {
            var row = pointer + (y * stride);
            for (var x = 0; x < width; x++)
            {
                row[(x * 4) + 3] = 255;
            }
        }
    }

    public static bool CanUploadBgraDirectly() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        OperatingSystem.IsAndroid() ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool UsesVertexArrayObject() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || OperatingSystem.IsAndroid();
}
