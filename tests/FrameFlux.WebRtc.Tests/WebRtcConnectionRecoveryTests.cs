using System.Reflection;
using SIPSorcery.Net;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class WebRtcConnectionRecoveryTests
{
    [Fact]
    public async Task TransientIceDisconnectKeepsPlaybackAliveButTerminalFailureFaults()
    {
        const string offer = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\nt=0 0\r\na=ice-ufrag:test\r\na=ice-pwd:testpassword123456789012345\r\na=fingerprint:sha-256 00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=setup:actpass\r\na=rtpmap:96 H264/90000\r\na=sendonly\r\n";
        await using var player = new WebRtcMediaPlayer();
        await player.OpenAsync(WebRtcSource.FromSdp(offer));
        await player.PlayAsync();
        var pc = player.PeerConnection!;
        var iceState = typeof(RtpIceChannel).GetProperty(nameof(RtpIceChannel.IceConnectionState))!;
        var change = typeof(RTCPeerConnection).GetMethod("IceConnectionStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MediaPlaybackError? error = null;
        player.Error += (_, args) => error = args.Error;

        // Reproduce the library reporting peer=failed while ICE=disconnected.
        iceState.SetValue(pc.GetRtpChannel(), RTCIceConnectionState.disconnected);
        change.Invoke(pc, [RTCIceConnectionState.disconnected]);
        Assert.Equal(RTCPeerConnectionState.failed, pc.connectionState);
        Assert.Equal(MediaPlaybackState.Playing, player.State);
        Assert.Null(error);

        iceState.SetValue(pc.GetRtpChannel(), RTCIceConnectionState.failed);
        change.Invoke(pc, [RTCIceConnectionState.failed]);
        Assert.Equal(MediaPlaybackState.Faulted, player.State);
        Assert.Equal("ConnectionFailed", error?.Code);
    }
}
