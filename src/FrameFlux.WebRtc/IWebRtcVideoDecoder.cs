using SIPSorceryMedia.Abstractions;

namespace FrameFlux.WebRtc;

/// <summary>
/// Interface for pluggable WebRTC video frame decoders.
/// Translates encoded RTP frame payloads into <see cref="WebRtcMediaFrameLease"/> instances.
/// </summary>
public interface IWebRtcVideoDecoder : IDisposable
{
    /// <summary>
    /// Gets or sets the decoding policy (SoftwareOnly, HardwarePreferred, HardwareRequired).
    /// </summary>
    MediaVideoDecodingPolicy DecodingPolicy { get; set; }

    /// <summary>
    /// Gets a value indicating whether hardware acceleration is actively being used.
    /// </summary>
    bool IsHardwareAccelerated { get; }

    /// <summary>
    /// Gets or sets a value indicating whether D3D11 hardware texture output is preferred and supported by the current presenter.
    /// </summary>
    bool CanOutputD3D11Texture { get; set; }

    /// <summary>
    /// Checks whether this decoder supports the given video format.
    /// </summary>
    bool CanDecode(VideoFormat format);

    /// <summary>
    /// Attempts to decode the encoded frame payload into a reusable frame lease.
    /// </summary>
    bool TryDecode(
        ReadOnlySpan<byte> encodedPayload,
        VideoFormat format,
        WebRtcFrameBufferPool pool,
        out WebRtcMediaFrameLease? decodedFrame);
}

/// <summary>
/// Fallback decoder used when no native video decoder is available.
/// It does not advertise codecs it cannot actually decode.
/// </summary>
public sealed class DefaultWebRtcVideoDecoder : IWebRtcVideoDecoder
{
    public MediaVideoDecodingPolicy DecodingPolicy { get; set; } = MediaVideoDecodingPolicy.SoftwareOnly;

    public bool IsHardwareAccelerated => false;

    public bool CanOutputD3D11Texture { get; set; }

    public bool CanDecode(VideoFormat format) => false;

    public bool TryDecode(
        ReadOnlySpan<byte> encodedPayload,
        VideoFormat format,
        WebRtcFrameBufferPool pool,
        out WebRtcMediaFrameLease? decodedFrame)
    {
        decodedFrame = null;
        return false;
    }

    public void Dispose()
    {
    }

}
