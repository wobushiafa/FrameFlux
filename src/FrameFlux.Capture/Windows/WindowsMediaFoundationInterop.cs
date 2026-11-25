#if !ANDROID
using System.Runtime.InteropServices;

namespace FrameFlux.Capture;

internal static unsafe class WindowsMediaFoundationInterop
{
    internal const uint MF_VERSION = 0x00020070;
    internal const uint MFSTARTUP_NOSOCKET = 0x1;
    internal const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFCu;
    internal const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFEu;

    internal const uint MF_SOURCE_READERF_ERROR = 0x00000001u;
    internal const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002u;
    internal const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020u;
    internal const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100u;

    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    internal const int MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED = unchecked((int)0xC00D3EA2);
    internal const int MF_E_SHUTDOWN = unchecked((int)0xC00D3E85);
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);

    internal static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    internal static readonly Guid IID_IMFActivate = new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67");
    internal static readonly Guid IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    internal static readonly Guid IID_IMFMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
    internal static readonly Guid IID_IMFSourceReader = new("70ae66f2-c809-4e4f-8915-bdcb406b7993");
    internal static readonly Guid IID_IMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    internal static readonly Guid IID_IMFMediaBuffer = new("045fa593-8799-42b8-bc8d-8968c6453507");
    internal static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
    internal static readonly Guid IID_IMFDXGIDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");
    internal static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");

    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670b50327");
    internal static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    internal static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    internal static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    internal static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    internal static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");

    // MFVideoTransferMatrix values
    internal const uint MFVideoTransferMatrix_BT709 = 1;
    internal const uint MFVideoTransferMatrix_BT601 = 2;

    // MFNominalRange values
    internal const uint MFNominalRange_0_255 = 1;
    internal const uint MFNominalRange_16_235 = 2;

    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB32 = CreateVideoFormatGuid(22);
    internal static readonly Guid MFVideoFormat_ARGB32 = CreateVideoFormatGuid(21);
    internal static readonly Guid MFVideoFormat_NV12 = CreateVideoFormatGuid(0x3231564Eu);
    internal static readonly Guid MFVideoFormat_YUY2 = CreateVideoFormatGuid(0x32595559u);
    internal static readonly Guid MFVideoFormat_MJPG = CreateVideoFormatGuid(0x47504A4Du);
    internal static readonly Guid MFVideoFormat_H264 = CreateVideoFormatGuid(0x34363248u);
    internal static readonly Guid MFVideoFormat_HEVC = CreateVideoFormatGuid(0x43564548u);
    internal static readonly Guid MFVideoFormat_I420 = CreateVideoFormatGuid(0x30323449u);

    private static Guid CreateVideoFormatGuid(uint fourCc) =>
        new((int)fourCc, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    private static readonly object StartupSync = new();
    private static bool _started;

    internal static void EnsureInitialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Media Foundation is only available on Windows.");
        }

        lock (StartupSync)
        {
            if (_started)
            {
                return;
            }

            var hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            _started = true;
        }
    }

    internal static MediaCapturePixelFormat MapSubtypeToPixelFormat(Guid subtype)
    {
        if (subtype == MFVideoFormat_NV12) return MediaCapturePixelFormat.Nv12;
        if (subtype == MFVideoFormat_YUY2) return MediaCapturePixelFormat.Yuy2;
        if (subtype == MFVideoFormat_MJPG) return MediaCapturePixelFormat.Mjpeg;
        if (subtype == MFVideoFormat_H264) return MediaCapturePixelFormat.H264;
        if (subtype == MFVideoFormat_HEVC) return MediaCapturePixelFormat.Hevc;
        if (subtype == MFVideoFormat_I420) return MediaCapturePixelFormat.Yuv420P;
        if (subtype == MFVideoFormat_RGB32 || subtype == MFVideoFormat_ARGB32) return MediaCapturePixelFormat.Bgra32;
        return MediaCapturePixelFormat.Unknown;
    }

    internal static Guid MapPixelFormatToSubtype(MediaCapturePixelFormat format) =>
        format switch
        {
            MediaCapturePixelFormat.Nv12 => MFVideoFormat_NV12,
            MediaCapturePixelFormat.Yuy2 => MFVideoFormat_YUY2,
            MediaCapturePixelFormat.Mjpeg => MFVideoFormat_MJPG,
            MediaCapturePixelFormat.H264 => MFVideoFormat_H264,
            MediaCapturePixelFormat.Hevc => MFVideoFormat_HEVC,
            MediaCapturePixelFormat.Yuv420P => MFVideoFormat_I420,
            MediaCapturePixelFormat.Bgra32 => MFVideoFormat_RGB32,
            _ => Guid.Empty
        };

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr ppDeviceManager);

    [DllImport("mf.dll", ExactSpelling = true)]
    internal static extern int MFEnumDeviceSources(IntPtr pAttributes, out IntPtr pppSourceActivate, out uint pcSourceActivate);

    [DllImport("mf.dll", ExactSpelling = true)]
    internal static extern int MFCreateDeviceSource(IntPtr pAttributes, out IntPtr ppSource);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    internal static extern int MFCreateSourceReaderFromMediaSource(
        IntPtr pMediaSource,
        IntPtr pAttributes,
        out IntPtr ppSourceReader);

    private static IntPtr* GetVtable(IntPtr comObject) => *(IntPtr**)comObject;

    internal static int QueryInterface(IntPtr comObject, in Guid iid, out IntPtr result)
    {
        fixed (Guid* pIid = &iid)
        fixed (IntPtr* pOut = &result)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetVtable(comObject)[0];
            return fn(comObject, pIid, pOut);
        }
    }

    internal static IntPtr AddRef(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            Marshal.AddRef(comObject);
        }

        return comObject;
    }

    internal static void SafeRelease(ref IntPtr comObject)
    {
        if (comObject == IntPtr.Zero)
        {
            return;
        }

        var ptr = comObject;
        comObject = IntPtr.Zero;
        Marshal.Release(ptr);
    }

    // IMFAttributes (slots 3..32)
    internal static int AttributesGetUInt32(IntPtr attrs, in Guid key, out uint value)
    {
        fixed (Guid* pKey = &key)
        fixed (uint* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)GetVtable(attrs)[7];
            return fn(attrs, pKey, pVal);
        }
    }

    internal static int AttributesGetUInt64(IntPtr attrs, in Guid key, out ulong value)
    {
        fixed (Guid* pKey = &key)
        fixed (ulong* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)GetVtable(attrs)[8];
            return fn(attrs, pKey, pVal);
        }
    }

    internal static int AttributesGetGuid(IntPtr attrs, in Guid key, out Guid value)
    {
        fixed (Guid* pKey = &key)
        fixed (Guid* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)GetVtable(attrs)[10];
            return fn(attrs, pKey, pVal);
        }
    }

    internal static string? AttributesGetAllocatedString(IntPtr attrs, in Guid key)
    {
        IntPtr pStr = IntPtr.Zero;
        uint length = 0;
        fixed (Guid* pKey = &key)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)GetVtable(attrs)[13];
            var hr = fn(attrs, pKey, &pStr, &length);
            if (hr < 0 || pStr == IntPtr.Zero)
            {
                return null;
            }
        }

        try
        {
            return Marshal.PtrToStringUni(pStr, (int)length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pStr);
        }
    }

    internal static int AttributesSetUInt32(IntPtr attrs, in Guid key, uint value)
    {
        fixed (Guid* pKey = &key)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)GetVtable(attrs)[21];
            return fn(attrs, pKey, value);
        }
    }

    internal static int AttributesSetGuid(IntPtr attrs, in Guid key, in Guid value)
    {
        fixed (Guid* pKey = &key)
        fixed (Guid* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)GetVtable(attrs)[24];
            return fn(attrs, pKey, pVal);
        }
    }

    internal static int AttributesSetString(IntPtr attrs, in Guid key, string value)
    {
        fixed (Guid* pKey = &key)
        fixed (char* pVal = value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, char*, int>)GetVtable(attrs)[25];
            return fn(attrs, pKey, pVal);
        }
    }

    internal static int AttributesSetUnknown(IntPtr attrs, in Guid key, IntPtr value)
    {
        fixed (Guid* pKey = &key)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)GetVtable(attrs)[27];
            return fn(attrs, pKey, value);
        }
    }

    internal static int AttributesDeleteItem(IntPtr attrs, in Guid key)
    {
        fixed (Guid* pKey = &key)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)GetVtable(attrs)[19];
            return fn(attrs, pKey);
        }
    }

    // IMFDXGIDeviceManager (7 = ResetDevice)
    internal static int DXGIDeviceManagerResetDevice(IntPtr manager, IntPtr d3dDevice, uint resetToken)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)GetVtable(manager)[7];
        return fn(manager, d3dDevice, resetToken);
    }

    // IMFActivate (inherits IMFAttributes: 33 = ActivateObject, 34 = ShutdownObject)
    internal static int ActivateObject(IntPtr activate, in Guid iid, out IntPtr obj)
    {
        fixed (Guid* pIid = &iid)
        fixed (IntPtr* pOut = &obj)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetVtable(activate)[33];
            return fn(activate, pIid, pOut);
        }
    }

    internal static int ShutdownActivateObject(IntPtr activate)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVtable(activate)[34];
        return fn(activate);
    }

    // IMFMediaSource (12 = Shutdown)
    internal static int MediaSourceShutdown(IntPtr mediaSource)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVtable(mediaSource)[12];
        return fn(mediaSource);
    }

    // IMFSourceReader (4 = SetStreamSelection, 5 = GetNativeMediaType, 6 = GetCurrentMediaType, 7 = SetCurrentMediaType, 9 = ReadSample, 10 = Flush)
    internal static int SourceReaderSetStreamSelection(IntPtr reader, uint streamIndex, bool selected)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)GetVtable(reader)[4];
        return fn(reader, streamIndex, selected ? 1 : 0);
    }

    internal static int SourceReaderGetNativeMediaType(IntPtr reader, uint streamIndex, uint mediaTypeIndex, out IntPtr mediaType)
    {
        fixed (IntPtr* pType = &mediaType)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)GetVtable(reader)[5];
            return fn(reader, streamIndex, mediaTypeIndex, pType);
        }
    }

    internal static int SourceReaderGetCurrentMediaType(IntPtr reader, uint streamIndex, out IntPtr mediaType)
    {
        fixed (IntPtr* pType = &mediaType)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)GetVtable(reader)[6];
            return fn(reader, streamIndex, pType);
        }
    }

    internal static int SourceReaderSetCurrentMediaType(IntPtr reader, uint streamIndex, IntPtr mediaType)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)GetVtable(reader)[7];
        return fn(reader, streamIndex, IntPtr.Zero, mediaType);
    }

    internal static int SourceReaderReadSample(
        IntPtr reader,
        uint streamIndex,
        uint controlFlags,
        out uint actualStreamIndex,
        out uint streamFlags,
        out long timestamp,
        out IntPtr sample)
    {
        fixed (uint* pActual = &actualStreamIndex)
        fixed (uint* pFlags = &streamFlags)
        fixed (long* pTs = &timestamp)
        fixed (IntPtr* pSample = &sample)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)GetVtable(reader)[9];
            return fn(reader, streamIndex, controlFlags, pActual, pFlags, pTs, pSample);
        }
    }

    internal static int SourceReaderFlush(IntPtr reader, uint streamIndex)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetVtable(reader)[10];
        return fn(reader, streamIndex);
    }

    // IMFSample (39 = GetBufferCount, 40 = GetBufferByIndex, 41 = ConvertToContiguousBuffer)
    internal static int SampleGetBufferCount(IntPtr sample, out uint count)
    {
        fixed (uint* pCount = &count)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)GetVtable(sample)[39];
            return fn(sample, pCount);
        }
    }

    internal static int SampleGetBufferByIndex(IntPtr sample, uint index, out IntPtr buffer)
    {
        fixed (IntPtr* pBuf = &buffer)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)GetVtable(sample)[40];
            return fn(sample, index, pBuf);
        }
    }

    internal static int SampleConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        fixed (IntPtr* pBuf = &buffer)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetVtable(sample)[41];
            return fn(sample, pBuf);
        }
    }

    // IMFDXGIBuffer (3 = GetResource, 4 = GetSubresourceIndex)
    internal static int DXGIBufferGetResource(IntPtr dxgiBuffer, in Guid iid, out IntPtr resource)
    {
        fixed (Guid* pIid = &iid)
        fixed (IntPtr* pRes = &resource)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetVtable(dxgiBuffer)[3];
            return fn(dxgiBuffer, pIid, pRes);
        }
    }

    internal static int DXGIBufferGetSubresourceIndex(IntPtr dxgiBuffer, out uint subresourceIndex)
    {
        fixed (uint* pIndex = &subresourceIndex)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)GetVtable(dxgiBuffer)[4];
            return fn(dxgiBuffer, pIndex);
        }
    }

    // IMFMediaBuffer (3 = Lock, 4 = Unlock, 5 = GetCurrentLength)
    internal static int BufferLock(IntPtr buffer, out IntPtr data, out uint maxLength, out uint currentLength)
    {
        fixed (IntPtr* pData = &data)
        fixed (uint* pMax = &maxLength)
        fixed (uint* pCurr = &currentLength)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)GetVtable(buffer)[3];
            return fn(buffer, pData, pMax, pCurr);
        }
    }

    internal static int BufferUnlock(IntPtr buffer)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVtable(buffer)[4];
        return fn(buffer);
    }

    // IMF2DBuffer (3 = Lock2D, 4 = Unlock2D)
    internal static int Buffer2DLock(IntPtr buffer2d, out IntPtr scanline0, out int pitch)
    {
        fixed (IntPtr* pScan = &scanline0)
        fixed (int* pPitch = &pitch)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int*, int>)GetVtable(buffer2d)[3];
            return fn(buffer2d, pScan, pPitch);
        }
    }

    internal static int Buffer2DUnlock(IntPtr buffer2d)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVtable(buffer2d)[4];
        return fn(buffer2d);
    }

    internal static void UnpackSize(ulong packed, out int width, out int height)
    {
        width = unchecked((int)(packed >> 32));
        height = unchecked((int)(packed & 0xFFFFFFFFu));
    }

    internal static double UnpackFrameRate(ulong packed)
    {
        var numerator = (uint)(packed >> 32);
        var denominator = (uint)(packed & 0xFFFFFFFFu);
        return denominator > 0 ? (double)numerator / denominator : 0d;
    }

    internal static bool IsDeviceDisconnectHResult(int hr) =>
        hr == MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED ||
        hr == MF_E_SHUTDOWN ||
        hr == unchecked((int)0x8007001F) || // ERROR_GEN_FAILURE
        hr == unchecked((int)0x80070490) || // ERROR_NOT_FOUND
        hr == unchecked((int)0x8007048F);   // ERROR_DEVICE_NOT_CONNECTED

    internal static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            var inner = Marshal.GetExceptionForHR(hr);
            throw new InvalidOperationException(
                $"Windows Media Foundation '{operation}' failed with HRESULT 0x{hr:X8}: {inner?.Message ?? "Unknown error"}",
                inner);
        }
    }
}
#endif
