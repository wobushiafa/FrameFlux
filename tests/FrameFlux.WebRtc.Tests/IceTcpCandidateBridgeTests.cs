using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class IceTcpCandidateBridgeTests
{
    [Fact]
    public async Task ForwardsOpaquePacketsWithFragmentedTcpFramesAndKeepAlive()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var bridge = new IceTcpCandidateBridge((IPEndPoint)peer.Client.LocalEndPoint!);
        var accept = listener.AcceptTcpClientAsync(timeout.Token);
        await bridge.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var remote = await accept;
        var stream = remote.GetStream();
        byte[] packet = [0x16, 0xfe, 0xfd, 1, 2, 3, 4];
        await peer.SendAsync(packet, bridge.LocalEndPoint, timeout.Token);
        var framed = new byte[packet.Length + 2];
        await stream.ReadExactlyAsync(framed, timeout.Token);
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16BigEndian(framed));
        Assert.Equal(packet, framed[2..]);

        // Keepalive plus two frames, with the length header split across reads.
        await stream.WriteAsync(new byte[] { 0, 0, 0 }, timeout.Token);
        await stream.WriteAsync(new byte[] { 3, 8, 9 }, timeout.Token);
        await stream.WriteAsync(new byte[] { 10, 0, 2, 11, 12 }, timeout.Token);
        Assert.Equal(new byte[] { 8, 9, 10 }, (await peer.ReceiveAsync(timeout.Token)).Buffer);
        Assert.Equal(new byte[] { 11, 12 }, (await peer.ReceiveAsync(timeout.Token)).Buffer);
    }

    [Fact]
    public async Task DisposeCancelsBlockedSocketReads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bridge = new IceTcpCandidateBridge((IPEndPoint)peer.Client.LocalEndPoint!);
        var accept = listener.AcceptTcpClientAsync(timeout.Token);
        await bridge.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var remote = await accept;
        await bridge.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.Equal(0, await remote.GetStream().ReadAsync(new byte[1], timeout.Token));
    }
}
