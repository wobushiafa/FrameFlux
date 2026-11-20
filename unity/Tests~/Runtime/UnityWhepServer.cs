using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

// Local real ICE/DTLS/SRTP sender for the Unity scene acceptance check.
internal sealed class UnityWhepServer : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly RTCPeerConnection peer = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task operation;
    internal string Endpoint { get; }
    internal UnityWhepServer()
    {
        listener.Start();
        Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/whep";
        peer.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.JPEG, 26), MediaStreamStatusEnum.SendOnly));
        peer.addTrack(new MediaStreamTrack(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), MediaStreamStatusEnum.SendOnly));
        peer.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) connected.TrySetResult(true); };
        operation = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        using (var socket = await listener.AcceptTcpClientAsync())
        using (var stream = socket.GetStream())
        {
            socket.ReceiveTimeout = 15000;
            var header = new List<byte>();
            bool completed = false;
            while (header.Count < 32768)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                header.Add((byte)value);
                int n = header.Count;
                if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10)
                { completed = true; break; }
            }
            if (!completed) throw new InvalidDataException("Invalid WHEP header.");
            int length = 0;
            foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
            if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Invalid WHEP offer size.");
            var body = new byte[length];
            int received = 0;
            while (received < length)
            {
                int n = await stream.ReadAsync(body, received, length - received, lifetime.Token);
                if (n == 0) throw new EndOfStreamException();
                received += n;
            }
            var result = peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = Encoding.UTF8.GetString(body) });
            if (result != SetDescriptionResultEnum.OK) throw new Exception("WHEP offer rejected: " + result);
            var answer = peer.createAnswer();
            await peer.setLocalDescription(answer);
            var bytes = Encoding.UTF8.GetBytes(answer.sdp);
            var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Type: application/sdp\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(prefix, 0, prefix.Length, lifetime.Token);
            await stream.WriteAsync(bytes, 0, bytes.Length, lifetime.Token);
        }
        if (await Task.WhenAny(connected.Task, Task.Delay(15000, lifetime.Token)) != connected.Task)
            throw new TimeoutException("WHEP peer did not connect.");
        var jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAgAAAQABAAD//gAQTGF2YzYxLjE5LjEwMQD/2wBDAAgEBAQEBAUFBQUFBQYGBgYGBgYGBgYGBgYGBgYHBwcICAgHBwcGBwcGBgcHCAgICAkJCQgICAgJCQoKCgwMCwsODg4RERT/xABMAAEBAAAAAAAAAAAAAAAAAAAABgEBAQAAAAAAAAAAAAAAAAAABgcQAQAAAAAAAAAAAAAAAAAAAAARAQAAAAAAAAAAAAAAAAAAAAD/wAARCAAQABADASIAAhEAAxEA/9oADAMBAAIRAxEAPwCLAFF/f//Z");
        var pcmu = new byte[160];
        for (int i = 0; i < pcmu.Length; i++) pcmu[i] = 0x80;
        for (int frame = 0; !lifetime.IsCancellationRequested; frame++)
        {
            if (frame % 5 == 0) peer.SendVideo(9000, jpeg);
            peer.SendAudio(160, pcmu);
            await Task.Delay(20, lifetime.Token);
        }
    }
    internal void ThrowIfFailed() { if (operation.IsFaulted) throw operation.Exception; }
    public void Dispose()
    {
        lifetime.Cancel();
        listener.Stop();
        peer.Close("Unity smoke complete");
        _ = operation.ContinueWith(task => { _ = task.Exception; lifetime.Dispose(); }, TaskScheduler.Default);
    }
}
