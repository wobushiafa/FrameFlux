using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class PlayerDisposalTests
{
    [Fact]
    public async Task DisposalIsSafeWhenRepeatedAndConcurrent()
    {
        var player = new WebRtcMediaPlayer(new WebRtcPlayerOptions
        {
            VideoDecoder = new DefaultWebRtcVideoDecoder(),
            AudioOutput = NullWebRtcAudioOutput.Instance
        });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => player.DisposeAsync().AsTask()));
        await player.DisposeAsync();
    }
}
