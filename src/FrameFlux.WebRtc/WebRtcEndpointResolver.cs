using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;

namespace FrameFlux.WebRtc;

public enum WebRtcEndpointKind
{
    Whep,
    Whip,
    DirectSdp,
    JsonConfig,
    Go2RtcWebSocket
}

public sealed record WebRtcResolvedEndpoint(
    WebRtcEndpointKind Kind,
    Uri? EndpointUri,
    string? RawSdp,
    List<RTCIceServer> IceServers,
    string SdpType = "offer");

/// <summary>
/// Resolves and negotiates WebRTC endpoints from various URI formats (webrtc://, WHEP/WHIP HTTP, SDP files/strings, JSON).
/// </summary>
public static class WebRtcEndpointResolver
{
    /// <summary>
    /// Resolves a <see cref="MediaSource"/> into a structured WebRTC endpoint.
    /// </summary>
    public static WebRtcResolvedEndpoint Resolve(MediaSource source, WebRtcPlayerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var uri = source.Uri;
        var uriString = uri.ToString();
        var defaultIceServers = options?.IceServers ?? [];

        // 1. Direct SDP in string (e.g. starts with v=0)
        if (uriString.StartsWith("v=0", StringComparison.OrdinalIgnoreCase))
        {
            return new WebRtcResolvedEndpoint(WebRtcEndpointKind.DirectSdp, null, uriString, defaultIceServers);
        }

        // 2. Data URI (data:application/sdp;..., data:application/json;...)
        if (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDataUri(uri, defaultIceServers);
        }

        // 3. Local file (file://...)
        if (uri.IsFile)
        {
            var localPath = uri.LocalPath;
            if (File.Exists(localPath))
            {
                var content = File.ReadAllText(localPath);
                if (localPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseJsonConfig(content, defaultIceServers);
                }

                return new WebRtcResolvedEndpoint(WebRtcEndpointKind.DirectSdp, uri, content, defaultIceServers);
            }
        }

        // 4. webrtc:// protocol scheme
        if (uri.Scheme.Equals("webrtc", StringComparison.OrdinalIgnoreCase))
        {
            var httpScheme = uri.Port == 443 ? "https" : "http";
            var builder = new UriBuilder(uri)
            {
                Scheme = httpScheme
            };

            // If path doesn't contain whep, we keep path or append whep
            var targetUri = builder.Uri;
            return new WebRtcResolvedEndpoint(WebRtcEndpointKind.Whep, targetUri, null, defaultIceServers);
        }

        // 5. WebSocket protocol scheme (ws:// or wss://)
        if (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            return new WebRtcResolvedEndpoint(WebRtcEndpointKind.Go2RtcWebSocket, uri, null, defaultIceServers);
        }

        // 6. HTTP / HTTPS (WHEP, WHIP, or go2rtc)
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            // go2rtc web pages or direct api/ws -> use ultra-fast WebSocket signaling
            if (uri.AbsolutePath.EndsWith("/stream.html", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith("/links.html", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Equals("/api/ws", StringComparison.OrdinalIgnoreCase))
            {
                var wsScheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
                var builder = new UriBuilder(uri)
                {
                    Scheme = wsScheme,
                    Path = "/api/ws"
                };
                return new WebRtcResolvedEndpoint(WebRtcEndpointKind.Go2RtcWebSocket, builder.Uri, null, defaultIceServers);
            }

            var kind = uri.ToString().Contains("/whip", StringComparison.OrdinalIgnoreCase)
                ? WebRtcEndpointKind.Whip
                : WebRtcEndpointKind.Whep;

            return new WebRtcResolvedEndpoint(kind, uri, null, defaultIceServers);
        }

        throw new NotSupportedException($"Unsupported WebRTC media source URI scheme: '{uri.Scheme}'.");
    }

    /// <summary>
    /// Executes WHEP HTTP SDP offer/answer exchange with the remote endpoint.
    /// </summary>
    public static async Task<(string AnswerSdp, Uri? SessionResourceUri)> ExchangeWhepOfferAsync(
        Uri whepUrl,
        string offerSdp,
        WebRtcPlayerOptions options,
        HttpClient? customClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(whepUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);

        var client = customClient ?? options.HttpClient ?? SharedHttpClient;
        using var request = new HttpRequestMessage(HttpMethod.Post, whepUrl)
        {
            Content = new StringContent(offerSdp, Encoding.UTF8, "application/sdp")
        };

        if (!string.IsNullOrWhiteSpace(options.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        }

        foreach (var (header, value) in options.CustomHeaders)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.SignalingTimeout);

        var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var answerSdp = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        var sessionResourceUri = response.Headers.Location;
        if (sessionResourceUri is not null && !sessionResourceUri.IsAbsoluteUri)
        {
            sessionResourceUri = new Uri(whepUrl, sessionResourceUri);
        }

        return (answerSdp, sessionResourceUri);
    }

    /// <summary>
    /// Sends a DELETE request to terminate the WHEP session on the remote server.
    /// </summary>
    public static async Task TerminateWhepSessionAsync(
        Uri sessionResourceUri,
        WebRtcPlayerOptions options,
        HttpClient? customClient = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionResourceUri is null)
        {
            return;
        }

        try
        {
            var client = customClient ?? options.HttpClient ?? SharedHttpClient;
            using var request = new HttpRequestMessage(HttpMethod.Delete, sessionResourceUri);
            if (!string.IsNullOrWhiteSpace(options.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort session cleanup
        }
    }

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private static WebRtcResolvedEndpoint ParseDataUri(Uri uri, List<RTCIceServer> defaultIceServers)
    {
        var original = uri.OriginalString;
        var colonIndex = original.IndexOf(':');
        var data = colonIndex >= 0 ? original[(colonIndex + 1)..] : original;
        var commaIndex = data.IndexOf(',');
        if (commaIndex < 0)
        {
            throw new FormatException("Invalid data URI format.");
        }

        var header = data[..commaIndex];
        var payload = data[(commaIndex + 1)..];
        var isBase64 = header.Contains(";base64", StringComparison.OrdinalIgnoreCase);

        var content = isBase64
            ? Encoding.UTF8.GetString(Convert.FromBase64String(payload))
            : Uri.UnescapeDataString(payload);

        if (header.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseJsonConfig(content, defaultIceServers);
        }

        return new WebRtcResolvedEndpoint(WebRtcEndpointKind.DirectSdp, uri, content, defaultIceServers);
    }

    private static WebRtcResolvedEndpoint ParseJsonConfig(string json, List<RTCIceServer> defaultIceServers)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sdp = root.TryGetProperty("sdp", out var sdpProp) ? sdpProp.GetString() : null;
        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "offer" : "offer";

        var iceServers = new List<RTCIceServer>(defaultIceServers);
        if (root.TryGetProperty("iceServers", out var iceServersProp) &&
            iceServersProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in iceServersProp.EnumerateArray())
            {
                if (server.TryGetProperty("urls", out var urlsProp))
                {
                    iceServers.Add(new RTCIceServer { urls = urlsProp.GetString() });
                }
            }
        }

        return new WebRtcResolvedEndpoint(WebRtcEndpointKind.JsonConfig, null, sdp, iceServers, type);
    }
}
