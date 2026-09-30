using System.Diagnostics;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FrameFlux.WebRtc.Tests;

public sealed class WebRtcTransportProbeTests(ITestOutputHelper output)
{
    private sealed class ProbeTrace(ITestOutputHelper output) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) => output.WriteLine(message ?? "");
    }
    [ExternalFact]
    [Trait("Category", "Integration")]
    public async Task LiveTransportReceivesFramesWithoutDecoder()
    {
        using var trace = new ProbeTrace(output);
        Trace.Listeners.Add(trace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new WebRtcPlayerOptions().IceServers });
        pc.addTrack(new MediaStreamTrack(
            new List<VideoFormat> { new(VideoCodecsEnum.H264, 96), new(VideoCodecsEnum.H265, 97) },
            MediaStreamStatusEnum.RecvOnly));
        pc.VideoStream.AddBuffer(TimeSpan.FromMilliseconds(80));
        var packets = 0;
        var frames = 0;
        var lost = 0;
        ushort? previousSequence = null;
        long previousPacketTime = 0, previousFrameTime = 0;
        double maximumPacketGap = 0, maximumFrameGap = 0;
        pc.OnRtpPacketReceived += (_, kind, packet) =>
        {
            if (kind != SDPMediaTypesEnum.video) return;
            var now = Stopwatch.GetTimestamp();
            if (previousPacketTime != 0) maximumPacketGap = Math.Max(maximumPacketGap, Stopwatch.GetElapsedTime(previousPacketTime, now).TotalMilliseconds);
            previousPacketTime = now;
            var sequence = packet.Header.SequenceNumber;
            if (previousSequence.HasValue)
            {
                var diff = (ushort)(sequence - previousSequence.Value);
                if (diff > 1 && diff < 3000) lost += diff - 1;
            }
            previousSequence = sequence;
            packets++;
        };
        pc.OnVideoFrameReceived += (_, _, _, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (previousFrameTime != 0) maximumFrameGap = Math.Max(maximumFrameGap, Stopwatch.GetElapsedTime(previousFrameTime, now).TotalMilliseconds);
            previousFrameTime = now;
            frames++;
        };
        var endpoint = WebRtcEndpointResolver.Resolve(MediaSource.Parse(Environment.GetEnvironmentVariable("FRAMEFLUX_GO2RTC_URL")!));
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        await using var signaling = await Go2RtcWebSocketSignaling.ConnectAndExchangeAsync(endpoint.EndpointUri!, pc, offer.sdp, timeout.Token);
        var answer = await signaling.WaitForAnswerAsync(timeout.Token);
        foreach (var line in answer.Split('\n'))
            if (line.StartsWith("a=candidate:") || line.StartsWith("a=rtpmap:")) output.WriteLine(line.Trim());
        Assert.Equal(SetDescriptionResultEnum.OK, pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer }));
        signaling.OnRemoteDescriptionSet();
        await Task.Delay(TimeSpan.FromSeconds(60), timeout.Token);
        output.WriteLine($"Selected ICE peer: {pc.GetRtpChannel().NominatedEntry?.RemoteCandidate.DestinationEndPoint}");
        output.WriteLine($"PC={pc.connectionState}, ICE={pc.iceConnectionState}, local candidates={string.Join("; ", pc.GetRtpChannel().Candidates.Select(c => c.candidate))}");
        pc.close();
        Trace.Listeners.Remove(trace);
        output.WriteLine($"RTP packets={packets}, detected missing={lost}, framed video={frames}, max RTP gap={maximumPacketGap:F0}ms, max frame gap={maximumFrameGap:F0}ms");
        Assert.True(frames > 20, $"Too few incoming frames: {frames}");
        Assert.True(maximumFrameGap < 1500, $"Incoming video stalled for {maximumFrameGap:F0}ms");
    }
}
