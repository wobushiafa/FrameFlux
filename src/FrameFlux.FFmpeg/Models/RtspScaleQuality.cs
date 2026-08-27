namespace FrameFlux.FFmpeg;

internal enum RtspScaleQuality
{
    /// <summary>
    /// 多路预览推荐
    /// </summary>
    FastBilinear,

    /// <summary>
    /// 默认，质量/性能平衡
    /// </summary>
    Bilinear,

    /// <summary>
    /// 质量更高       
    /// </summary>
    Bicubic
}
