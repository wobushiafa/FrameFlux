namespace FrameFlux.FFmpeg;

internal enum RtspNativePixelFormat
{
    Bgra32,
    Yuv420P,
    Nv12,
    /// <summary>
    /// NV21: semi-planar YUV 4:2:0 with interleaved V-U chroma plane (common on Android MediaCodec).
    /// </summary>
    Nv21,
    D3D11Texture,
    DmaBuf
}
