using System.Buffers;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;

namespace FrameFlux.WebRtc;

/// <summary>
/// Handles ultrafast real-time WebRTC signaling and Trickle ICE candidate exchange
/// over go2rtc's native WebSocket endpoint (/api/ws?src=...).
/// </summary>
public sealed class Go2RtcWebSocketSignaling : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _answerTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _earlyCandidates = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, Task> _tcpCandidates = [];
    private readonly List<IceTcpCandidateBridge> _tcpBridges = [];
    private RTCPeerConnection? _pc;
    private Task? _receiveLoopTask;
    private bool _remoteDescriptionSet;
    private bool _disposed;

    public static async Task<Go2RtcWebSocketSignaling> ConnectAndExchangeAsync(
        Uri wsUri,
        RTCPeerConnection pc,
        string offerSdp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wsUri);
        ArgumentNullException.ThrowIfNull(pc);
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);

        var signaling = new Go2RtcWebSocketSignaling();
        try
        {
            await signaling.InitializeAsync(wsUri, pc, offerSdp, cancellationToken).ConfigureAwait(false);
            return signaling;
        }
        catch
        {
            await signaling.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(
        Uri wsUri,
        RTCPeerConnection pc,
        string offerSdp,
        CancellationToken cancellationToken)
    {
        _pc = pc;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        await _ws.ConnectAsync(wsUri, linkedCts.Token).ConfigureAwait(false);

        // Start background message loop
        _receiveLoopTask = Task.Run(ReceiveLoopAsync);

        // Send webrtc/offer
        var offerPayload = JsonSerializer.Serialize(new Go2RtcSignalingMessage
        {
            Type = "webrtc/offer",
            Value = offerSdp
        }, WebRtcJsonSerializerContext.Default.Go2RtcSignalingMessage);

        await SendTextAsync(offerPayload, linkedCts.Token).ConfigureAwait(false);

        // SIPSorcery replays gathered candidates when the first handler subscribes.
        // Subscribe only after the socket is open and the offer has been sent.
        _pc.onicecandidate += OnLocalIceCandidate;
    }

    public async Task<string> WaitForAnswerAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        return await _answerTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
    }

    public void OnRemoteDescriptionSet()
    {
        List<string> candidatesToApply;
        lock (_sync)
        {
            _remoteDescriptionSet = true;
            candidatesToApply = [.. _earlyCandidates];
            _earlyCandidates.Clear();
        }

        if (_pc is not null)
        {
            foreach (var candidate in candidatesToApply)
            {
                try
                {
                    ApplyRemoteCandidate(candidate);
                }
                catch
                {
                    // Ignore malformed or stale candidate
                }
            }
        }
    }

    private void OnLocalIceCandidate(RTCIceCandidate? candidate)
    {
        if (_disposed || candidate is null)
        {
            return;
        }

        var candidateStr = candidate?.candidate ?? string.Empty;
#if ANDROID && DEBUG
        global::Android.Util.Log.Info("FrameFluxICE", $"Sending local candidate: {candidateStr}");
#endif
        var payload = JsonSerializer.Serialize(new Go2RtcSignalingMessage
        {
            Type = "webrtc/candidate",
            Value = candidateStr
        }, WebRtcJsonSerializerContext.Default.Go2RtcSignalingMessage);

        _ = SendCandidateAsync(payload);
    }

    private async Task SendCandidateAsync(string payload)
    {
        try
        {
            await SendTextAsync(payload, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed || _cts.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _answerTcs.TrySetException(exception);
            System.Diagnostics.Trace.TraceWarning("WebRTC candidate send failed: {0}", exception.Message);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        var ms = new MemoryStream();

        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _answerTcs.TrySetException(new InvalidOperationException(
                        "WebRTC signaling closed before an SDP answer was received."));
                    break;
                }

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _answerTcs.TrySetException(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ms.Dispose();
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize(json, WebRtcJsonSerializerContext.Default.Go2RtcSignalingMessage);
            if (msg is null || string.IsNullOrEmpty(msg.Type))
            {
                return;
            }

            if (msg.Type == "webrtc/answer")
            {
#if ANDROID && DEBUG
                foreach (var line in (msg.Value ?? string.Empty).Split('\n'))
                {
                    if (line.StartsWith("a=candidate:", StringComparison.Ordinal))
                        global::Android.Util.Log.Info("FrameFluxICE", $"Answer candidate: {line.Trim()}");
                }
#endif
                _answerTcs.TrySetResult(msg.Value ?? string.Empty);
            }
            else if (msg.Type == "error")
            {
                _answerTcs.TrySetException(new InvalidOperationException(
                    $"WebRTC signaling error: {msg.Value}"));
            }
            else if (msg.Type == "webrtc/candidate" && !string.IsNullOrWhiteSpace(msg.Value))
            {
#if ANDROID && DEBUG
                global::Android.Util.Log.Info("FrameFluxICE", $"Remote candidate: {msg.Value}");
#endif
                lock (_sync)
                {
                    if (!_remoteDescriptionSet)
                    {
                        _earlyCandidates.Add(msg.Value);
                        return;
                    }
                }

                try
                {
                    ApplyRemoteCandidate(msg.Value);
                }
                catch
                {
                    // Best effort
                }
            }
        }
        catch
        {
            // Ignore unparseable or unrecognized frames (e.g. mse binary or stats)
        }
    }

    private async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void ApplyRemoteCandidate(string candidate)
    {
        var fields = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length >= 8 && fields[2].Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            // SIPSorcery accepts UDP candidates only. Connect actively to passive
            // ICE-TCP peers, exposing a private loopback endpoint to its ICE agent.
            if (fields[1] != "1" || !candidate.Contains("tcptype passive", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(fields[5], out var port) || port is < 1 or > 65535) return;
            lock (_sync)
            {
                if (_disposed || _tcpCandidates.ContainsKey(candidate) || _tcpCandidates.Count >= 8) return;
                _tcpCandidates.Add(candidate, ConnectTcpCandidateAsync(fields[4], port));
            }
            return;
        }
        _pc?.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMid = "0" });
    }

    private async Task ConnectTcpCandidateAsync(string address, int port)
    {
        IceTcpCandidateBridge? bridge = null;
        try
        {
            var pc = _pc!;
            var local = pc.GetRtpChannel().RTPLocalEndPoint;
            var loopback = local.Address.Equals(IPAddress.IPv6Any) || local.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            bridge = new IceTcpCandidateBridge(new IPEndPoint(loopback, local.Port));
            await bridge.ConnectAsync(address, port, _cts.Token).ConfigureAwait(false);
            lock (_sync)
            {
                // A go2rtc ICE TCP mux associates the same ICE username with the
                // active connection. Opening several paths to the same mux can
                // steal that association and strand DTLS/media on another socket.
                if (_disposed || _tcpBridges.Count != 0) return;
                var endpoint = bridge.LocalEndPoint;
#if ANDROID && DEBUG
                global::Android.Util.Log.Info("FrameFluxICE", $"TCP transport selected: {address}:{port} via {endpoint}");
#endif
                pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = $"candidate:tcpbridge 1 udp 2147483647 {endpoint.Address} {endpoint.Port} typ host",
                    sdpMid = "0"
                });
                _tcpBridges.Add(bridge);
                bridge = null; // Owned by signaling until disconnect.
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or OperationCanceledException or ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            if (!_cts.IsCancellationRequested)
                System.Diagnostics.Trace.TraceWarning("ICE TCP candidate connection failed: {0}", ex.Message);
        }
        finally
        {
            if (bridge is not null) await bridge.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        if (_pc is not null)
        {
            _pc.onicecandidate -= OnLocalIceCandidate;
        }

        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await _sendGate.WaitAsync(closeCts.Token).ConfigureAwait(false);
                try
                {
                    // The receive loop already owns ReceiveAsync. Send only the close
                    // frame before cancellation aborts its pending receive operation.
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
                }
                finally { _sendGate.Release(); }
            }
        }
        catch
        {
            // Best effort
        }

        _cts.Cancel();

        await Task.WhenAll(_tcpCandidates.Values).ConfigureAwait(false);
        foreach (var bridge in _tcpBridges) await bridge.DisposeAsync().ConfigureAwait(false);

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // Task canceled
            }
        }
        _ws.Dispose();
        _cts.Dispose();
    }

    internal sealed class Go2RtcSignalingMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
