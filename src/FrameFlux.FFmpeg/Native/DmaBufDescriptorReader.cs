using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static class DmaBufDescriptorReader
{
    private const int MaximumObjects = 4;
    private const int MaximumLayers = 4;
    private const int MaximumPlanes = 4;

    internal static MediaDmaBufFrameBuffer Read(IntPtr descriptorPointer)
    {
        if (descriptorPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("The DRM PRIME frame has no descriptor.");
        }

        var descriptor = Marshal.PtrToStructure<AvDrmFrameDescriptor>(descriptorPointer);
        ValidateCount(descriptor.ObjectCount, MaximumObjects, "objects");
        ValidateCount(descriptor.LayerCount, MaximumLayers, "layers");

        var objects = new MediaDmaBufObject[descriptor.ObjectCount];
        for (var index = 0; index < objects.Length; index++)
        {
            var item = descriptor.Objects[index];
            if (item.FileDescriptor < 0)
            {
                throw new InvalidOperationException("The DRM PRIME frame contains an invalid file descriptor.");
            }
            objects[index] = new MediaDmaBufObject(
                item.FileDescriptor,
                checked((long)item.Size),
                item.FormatModifier);
        }

        var layers = new MediaDmaBufLayer[descriptor.LayerCount];
        for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
        {
            var item = descriptor.Layers[layerIndex];
            ValidateCount(item.PlaneCount, MaximumPlanes, "planes");
            var planes = new MediaDmaBufPlane[item.PlaneCount];
            for (var planeIndex = 0; planeIndex < planes.Length; planeIndex++)
            {
                var plane = item.Planes[planeIndex];
                if ((uint)plane.ObjectIndex >= (uint)objects.Length)
                {
                    throw new InvalidOperationException(
                        "The DRM PRIME frame references an invalid object index.");
                }
                planes[planeIndex] = new MediaDmaBufPlane(
                    plane.ObjectIndex,
                    checked((int)plane.Offset),
                    checked((int)plane.Pitch));
            }
            layers[layerIndex] = new MediaDmaBufLayer(item.Format, planes);
        }

        return new MediaDmaBufFrameBuffer(objects, layers);
    }

    private static void ValidateCount(int count, int maximum, string name)
    {
        if (count is < 1 || count > maximum)
        {
            throw new InvalidOperationException(
                $"The DRM PRIME frame reports an invalid number of {name}: {count}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AvDrmObjectDescriptor
    {
        internal int FileDescriptor;
        internal nuint Size;
        internal ulong FormatModifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AvDrmPlaneDescriptor
    {
        internal int ObjectIndex;
        internal nint Offset;
        internal nint Pitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AvDrmLayerDescriptor
    {
        internal uint Format;
        internal int PlaneCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaximumPlanes)]
        internal AvDrmPlaneDescriptor[] Planes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AvDrmFrameDescriptor
    {
        internal int ObjectCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaximumObjects)]
        internal AvDrmObjectDescriptor[] Objects;

        internal int LayerCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaximumLayers)]
        internal AvDrmLayerDescriptor[] Layers;
    }
}
