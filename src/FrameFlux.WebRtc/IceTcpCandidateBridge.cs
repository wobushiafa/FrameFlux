using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace FrameFlux.WebRtc;

/// <summary>
/// Adapts a passive ICE-TCP candidate (RFC 6544/4571) to the UDP-only ICE
/// channel. ICE, DTLS and SRTP packets are forwarded without modification.
/// The local socket is private to this peer connection and never advertised.
/// </summary>
internal sealed class IceTcpCandidateBridge : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    internal IceTcpCandidateBridge(IPEndPoint peer)
    {
        _udp = new UdpClient(new IPEndPoint(peer.Address, 0));
        // Connected UDP accepts datagrams only from this peer's RTP socket.
        _udp.Connect(peer);
    }

    internal IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    internal async Task ConnectAsync(string address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _tcp.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
        _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        var outbound = UdpToTcpAsync();
        var inbound = TcpToUdpAsync();
        try
        {
            await await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested)
                System.Diagnostics.Trace.TraceWarning("ICE TCP transport stopped: {0}", ex.Message);
        }
        finally
        {
            _cts.Cancel();
            _tcp.Dispose();
            _udp.Dispose();
            try { await Task.WhenAll(outbound, inbound).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task UdpToTcpAsync()
    {
        var stream = _tcp.GetStream();
        var frame = new byte[ushort.MaxValue + 2];
        while (!_cts.IsCancellationRequested)
        {
            var datagram = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
            BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)datagram.Buffer.Length));
            datagram.Buffer.CopyTo(frame, 2);
            await stream.WriteAsync(frame.AsMemory(0, datagram.Buffer.Length + 2), _cts.Token).ConfigureAwait(false);
        }
    }

    private async Task TcpToUdpAsync()
    {
        var stream = _tcp.GetStream();
        var header = new byte[2];
        var packet = new byte[ushort.MaxValue];
        long lastPacket = 0;
        while (!_cts.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header);
            // RFC 4571 permits empty frames as keep-alives.
            if (length == 0) continue;
            await stream.ReadExactlyAsync(packet.AsMemory(0, length), _cts.Token).ConfigureAwait(false);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastPacket != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastPacket, now).TotalMilliseconds > 500)
                System.Diagnostics.Trace.TraceWarning("ICE TCP incoming gap {0:F0}ms, next packet type {1}",
                    System.Diagnostics.Stopwatch.GetElapsedTime(lastPacket, now).TotalMilliseconds, packet[0]);
            lastPacket = now;
            await _udp.SendAsync(packet.AsMemory(0, length), _cts.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _tcp.Dispose();
        _udp.Dispose();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _cts.Dispose();
    }
}
