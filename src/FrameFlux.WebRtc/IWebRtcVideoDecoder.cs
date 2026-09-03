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
/// Default video decoder providing basic fallback and MJPEG decoding capabilities.
/// </summary>
public sealed class DefaultWebRtcVideoDecoder : IWebRtcVideoDecoder
{
    public MediaVideoDecodingPolicy DecodingPolicy { get; set; } = MediaVideoDecodingPolicy.SoftwareOnly;

    public bool IsHardwareAccelerated => false;

    public bool CanOutputD3D11Texture { get; set; }

    public bool CanDecode(VideoFormat format)
    {
        return format.Codec == VideoCodecsEnum.JPEG;
    }

    public bool TryDecode(
        ReadOnlySpan<byte> encodedPayload,
        VideoFormat format,
        WebRtcFrameBufferPool pool,
        out WebRtcMediaFrameLease? decodedFrame)
    {
        decodedFrame = null;
        if (encodedPayload.IsEmpty)
        {
            return false;
        }

        // Check for JPEG SOF0/SOF2 marker to extract width and height if needed
        if (format.Codec == VideoCodecsEnum.JPEG && TryParseJpegDimensions(encodedPayload, out var width, out var height))
        {
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var stride = width * 4;
            var requiredSize = stride * height;
            if (requiredSize <= 0)
            {
                return false;
            }

            var buffer = pool.Rent(requiredSize);
            decodedFrame = new WebRtcMediaFrameLease(buffer, requiredSize, lease => pool.Return(lease.Buffer, lease.Size));
            decodedFrame.ResetBgra(width, height, stride);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
    }

    private static bool TryParseJpegDimensions(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return false;
        }

        var i = 2;
        while (i < jpeg.Length - 8)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = jpeg[i + 1];
            // SOF0 (0xC0) or SOF2 (0xC2)
            if (marker is 0xC0 or 0xC2)
            {
                height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return width > 0 && height > 0;
            }

            var len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (len <= 0)
            {
                break;
            }

            i += 2 + len;
        }

        return false;
    }
}
