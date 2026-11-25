using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal static unsafe class DmaBufDescriptorReader
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

        // Direct blittable pointer read avoids Marshal.PtrToStructure and 5 ByValArray allocations per frame.
        var descriptor = (AvDrmFrameDescriptor*)descriptorPointer;
        var objectCount = descriptor->ObjectCount;
        var layerCount = descriptor->LayerCount;
        ValidateCount(objectCount, MaximumObjects, "objects");
        ValidateCount(layerCount, MaximumLayers, "layers");

        var objects = new MediaDmaBufObject[objectCount];
        var objectPtr = &descriptor->Object0;
        for (var index = 0; index < objectCount; index++)
        {
            var item = objectPtr[index];
            if (item.FileDescriptor < 0)
            {
                throw new InvalidOperationException("The DRM PRIME frame contains an invalid file descriptor.");
            }

            objects[index] = new MediaDmaBufObject(
                item.FileDescriptor,
                checked((long)item.Size),
                item.FormatModifier);
        }

        var layers = new MediaDmaBufLayer[layerCount];
        var layerPtr = &descriptor->Layer0;
        for (var layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            var item = &layerPtr[layerIndex];
            var planeCount = item->PlaneCount;
            ValidateCount(planeCount, MaximumPlanes, "planes");
            var planes = new MediaDmaBufPlane[planeCount];
            var planePtr = &item->Plane0;
            for (var planeIndex = 0; planeIndex < planeCount; planeIndex++)
            {
                var plane = planePtr[planeIndex];
                if ((uint)plane.ObjectIndex >= (uint)objectCount)
                {
                    throw new InvalidOperationException(
                        "The DRM PRIME frame references an invalid object index.");
                }

                planes[planeIndex] = new MediaDmaBufPlane(
                    plane.ObjectIndex,
                    checked((int)plane.Offset),
                    checked((int)plane.Pitch));
            }

            layers[layerIndex] = new MediaDmaBufLayer(item->Format, planes);
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
        internal AvDrmPlaneDescriptor Plane0;
        internal AvDrmPlaneDescriptor Plane1;
        internal AvDrmPlaneDescriptor Plane2;
        internal AvDrmPlaneDescriptor Plane3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AvDrmFrameDescriptor
    {
        internal int ObjectCount;
        internal AvDrmObjectDescriptor Object0;
        internal AvDrmObjectDescriptor Object1;
        internal AvDrmObjectDescriptor Object2;
        internal AvDrmObjectDescriptor Object3;
        internal int LayerCount;
        internal AvDrmLayerDescriptor Layer0;
        internal AvDrmLayerDescriptor Layer1;
        internal AvDrmLayerDescriptor Layer2;
        internal AvDrmLayerDescriptor Layer3;
    }
}
