using System.Buffers;
using System.Net.WebSockets;
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

        // Wire local ICE candidates to trickle to go2rtc
        _pc.onicecandidate += OnLocalIceCandidate;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        await _ws.ConnectAsync(wsUri, linkedCts.Token).ConfigureAwait(false);

        // Start background message loop
        _receiveLoopTask = Task.Run(ReceiveLoopAsync);

        // Send webrtc/offer
        var offerPayload = JsonSerializer.Serialize(new Go2RtcSignalingMessage
        {
            Type = "webrtc/offer",
            Value = offerSdp
        });

        await SendTextAsync(offerPayload, linkedCts.Token).ConfigureAwait(false);
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
                    _pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = candidate,
                        sdpMid = "0"
                    });
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
        if (_disposed || _ws.State != WebSocketState.Open)
        {
            return;
        }

        var candidateStr = candidate?.candidate ?? string.Empty;
        var payload = JsonSerializer.Serialize(new Go2RtcSignalingMessage
        {
            Type = "webrtc/candidate",
            Value = candidateStr
        });

        _ = SendTextAsync(payload, _cts.Token);
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
            var msg = JsonSerializer.Deserialize<Go2RtcSignalingMessage>(json);
            if (msg is null || string.IsNullOrEmpty(msg.Type))
            {
                return;
            }

            if (msg.Type == "webrtc/answer")
            {
                _answerTcs.TrySetResult(msg.Value ?? string.Empty);
            }
            else if (msg.Type == "webrtc/candidate" && !string.IsNullOrWhiteSpace(msg.Value))
            {
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
                    _pc?.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = msg.Value,
                        sdpMid = "0"
                    });
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
        if (_disposed || _ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Connection closing
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

        _cts.Cancel();

        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort
        }

        _ws.Dispose();
        _cts.Dispose();

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
    }

    private sealed class Go2RtcSignalingMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
