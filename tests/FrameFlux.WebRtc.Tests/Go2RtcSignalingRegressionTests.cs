using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class Go2RtcSignalingRegressionTests
{
    [Fact]
    public async Task GatheredCandidatesAreSentAfterOfferAndServerErrorsFailPromptly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var socket = await AcceptWebSocketAsync(client.GetStream(), timeout.Token);
            Assert.Equal("webrtc/offer", await ReadMessageTypeAsync(socket, timeout.Token));
            Assert.Equal("webrtc/candidate", await ReadMessageTypeAsync(socket, timeout.Token));
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"error\",\"value\":\"camera unavailable\"}"),
                WebSocketMessageType.Text, true, timeout.Token);
            // Keep the socket open until the client has handled the server error.
            var buffer = new byte[4096];
            while ((await socket.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close) { }
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        });

        using var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 96), MediaStreamStatusEnum.RecvOnly));
        while (pc.GetRtpChannel().Candidates.Count == 0) await Task.Delay(10, timeout.Token);
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        await using (var signaling = await Go2RtcWebSocketSignaling.ConnectAndExchangeAsync(
            new Uri($"ws://127.0.0.1:{port}/api/ws"), pc, offer.sdp, timeout.Token))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => signaling.WaitForAnswerAsync(timeout.Token));
            Assert.Contains("camera unavailable", error.Message);
        }
        await server;
    }

    [Fact]
    public async Task CloseBeforeAnswerDoesNotHangNegotiation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var socket = await AcceptWebSocketAsync(client.GetStream(), timeout.Token);
            Assert.Equal("webrtc/offer", await ReadMessageTypeAsync(socket, timeout.Token));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            var buffer = new byte[4096];
            while ((await socket.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        });
        using var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        await using (var signaling = await Go2RtcWebSocketSignaling.ConnectAndExchangeAsync(
            new Uri($"ws://127.0.0.1:{port}/api/ws"), pc, "v=0\r\n", timeout.Token))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => signaling.WaitForAnswerAsync(timeout.Token));
            Assert.Contains("closed before", error.Message);
        }
        await server;
    }

    private static async Task<WebSocket> AcceptWebSocketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // A loopback-only server avoids HTTP.sys URL reservations on Windows.
        var request = new StringBuilder();
        var buffer = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.Equal(1, await stream.ReadAsync(buffer, cancellationToken));
            request.Append((char)buffer[0]);
            Assert.True(request.Length < 16384);
        }
        var key = request.ToString().Split("\r\n")
            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), cancellationToken);
        return WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
    }

    private static async Task<string?> ReadMessageTypeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(message.ToArray());
        return json.RootElement.GetProperty("type").GetString();
    }
}
