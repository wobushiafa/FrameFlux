using System.Net.Http;
using SIPSorcery.Net;

namespace FrameFlux.WebRtc;

/// <summary>
/// Configuration options for WebRTC media playback.
/// </summary>
public sealed class WebRtcPlayerOptions
{
    /// <summary>
    /// List of STUN/TURN ICE servers to use for WebRTC peer connections.
    /// Default includes public Google STUN server.
    /// </summary>
    public List<RTCIceServer> IceServers { get; init; } =
    [
        new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
    ];

    /// <summary>
    /// HTTP client to use for WHEP / WHIP signaling requests. If null, a default client is used.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Optional authorization header or Bearer token for WHEP / WHIP requests.
    /// </summary>
    public string? AuthToken { get; init; }

    /// <summary>
    /// Additional custom HTTP headers to attach to WHEP requests.
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; init; } = [];

    /// <summary>
    /// Timeout for WHEP/WHIP signaling exchange. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan SignalingTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum time to wait for out-of-order video RTP packets before treating a sequence gap as packet loss.
    /// Set to <see cref="TimeSpan.Zero"/> to disable reordering. Defaults to 80 milliseconds.
    /// </summary>
    public TimeSpan VideoRtpReorderTimeout { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Optional custom video decoder instance.
    /// </summary>
    public IWebRtcVideoDecoder? VideoDecoder { get; init; }

    /// <summary>
    /// Optional custom audio output instance.
    /// </summary>
    public IWebRtcAudioOutput? AudioOutput { get; init; }

    /// <summary>
    /// Maximum buffer count for the unmanaged frame pool. Defaults to 8.
    /// </summary>
    public int MaxPoolBufferCount { get; init; } = WebRtcFrameBufferPool.DefaultMaximumBufferCount;

    /// <summary>
    /// Maximum retained bytes for the unmanaged frame pool. Defaults to 64 MB.
    /// </summary>
    public long MaxPoolRetainedBytes { get; init; } = WebRtcFrameBufferPool.DefaultMaximumRetainedBytes;
}
