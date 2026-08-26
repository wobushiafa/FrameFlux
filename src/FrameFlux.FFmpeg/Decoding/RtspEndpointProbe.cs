using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FrameFlux.FFmpeg;

internal static class RtspEndpointProbe
{
    internal static bool IsReachable(
        string url,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        out string? failureMessage)
    {
        failureMessage = null;
        var endpoint = ResolveEndpoint(url);
        if (timeoutMilliseconds <= 0 || endpoint == null)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var connectArgs = new SocketAsyncEventArgs
        {
            RemoteEndPoint = new DnsEndPoint(endpoint.Value.Host, endpoint.Value.Port)
        };
        using var completionSignal = new ManualResetEventSlim(false);
        connectArgs.Completed += (_, _) => completionSignal.Set();

        if (socket.ConnectAsync(connectArgs))
        {
            var waitResult = WaitHandle.WaitAny(
                [completionSignal.WaitHandle, cancellationToken.WaitHandle],
                timeoutMilliseconds);
            if (waitResult != 0)
            {
                Socket.CancelConnectAsync(connectArgs);
                completionSignal.Wait();
                cancellationToken.ThrowIfCancellationRequested();
                failureMessage =
                    $"RTSP endpoint {endpoint.Value.Host}:{endpoint.Value.Port} did not respond within {timeoutMilliseconds} ms.";
                return false;
            }
        }

        if (connectArgs.SocketError == SocketError.Success)
        {
            return true;
        }

        failureMessage = $"RTSP endpoint {endpoint.Value.Host}:{endpoint.Value.Port} is unavailable.";
        return false;
    }

    internal static (string Host, int Port)? ResolveEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost))
        {
            return null;
        }

        var defaultPort = uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase)
            ? 322
            : 554;
        var port = uri.Port > 0 ? uri.Port : defaultPort;
        return (uri.DnsSafeHost, port);
    }
}
