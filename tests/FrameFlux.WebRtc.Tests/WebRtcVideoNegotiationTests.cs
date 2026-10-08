using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class WebRtcVideoNegotiationTests
{
    [Fact]
    public async Task OfferAdvertisesH264ParametersCompatibleWithGo2Rtc()
    {
        using var sink = new WebRtcVideoSink();
        using var receiver = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        receiver.addTrack(new MediaStreamTrack(sink.GetVideoSinkFormats(), MediaStreamStatusEnum.RecvOnly));
        var offer = receiver.createOffer();
        await receiver.setLocalDescription(offer);

        // Check the actual wire SDP: Pion matches both the H.264 profile and
        // packetization mode, rather than accepting just the codec name.
        var sdp = SDP.ParseSDPDescription(offer.sdp);
        var video = Assert.Single(sdp.Media, media => media.Media == SDPMediaTypesEnum.video);
        var h264 = Assert.Single(video.MediaFormats.Values, format => format.IsH264);
        var parameters = h264.Fmtp!.Split(';')
            .Select(parameter => parameter.Trim().Split('=', 2))
            .ToDictionary(parameter => parameter[0], parameter => parameter[1]);
        Assert.Equal("1", parameters["packetization-mode"]);
        Assert.Equal("42e01f", parameters["profile-level-id"]);
        Assert.Equal("1", parameters["level-asymmetry-allowed"]);

        using var sender = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        sender.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 120,
            parameters: h264.Fmtp), MediaStreamStatusEnum.SendOnly));
        Assert.Equal(SetDescriptionResultEnum.OK, sender.setRemoteDescription(offer));
        var answer = sender.createAnswer();
        Assert.Equal(SetDescriptionResultEnum.OK, receiver.setRemoteDescription(answer));
        Assert.Contains(receiver.VideoRemoteTrack.Capabilities, format => format.IsH264 && format.ID == h264.ID);
    }
}
