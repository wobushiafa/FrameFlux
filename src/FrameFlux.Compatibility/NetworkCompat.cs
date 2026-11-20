#if NETSTANDARD2_1
using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FrameFlux.Compatibility
{
    internal static class NetworkCompat
    {
        internal static async Task ConnectAsync(this TcpClient client, string host, int port, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (token.Register(client.Dispose))
            {
                try { await client.ConnectAsync(host, port).ConfigureAwait(false); }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            }
            token.ThrowIfCancellationRequested();
        }
        internal static async Task<UdpReceiveResult> ReceiveAsync(this UdpClient client, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (token.Register(client.Dispose))
            {
                try { return await client.ReceiveAsync().ConfigureAwait(false); }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            }
        }
        internal static async Task<int> SendAsync(this UdpClient client, ReadOnlyMemory<byte> data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var bytes = data.ToArray();
            return await client.SendAsync(bytes, bytes.Length).WaitAsync(token).ConfigureAwait(false);
        }
        internal static async Task ReadExactlyAsync(this Stream stream, Memory<byte> data, CancellationToken token)
        {
            while (data.Length > 0)
            {
                var count = await stream.ReadAsync(data, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                data = data.Slice(count);
            }
        }
        internal static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken token) => content.ReadAsStringAsync().WaitAsync(token);
    }
}
#endif
