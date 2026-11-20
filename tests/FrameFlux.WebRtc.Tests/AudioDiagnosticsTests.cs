using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class AudioDiagnosticsTests
{
    [Fact]
    public async Task InjectedCommonAudioSinkReportsItsBackendAndOperationalState()
    {
        var audio = new CommonAudioSink();
        await using var player = new WebRtcMediaPlayer(new WebRtcPlayerOptions
        {
            VideoDecoder = new DefaultWebRtcVideoDecoder(), AudioOutput = audio
        });

        Assert.Equal("Injected PCM", player.Diagnostics.Audio.Backend);
        Assert.True(player.Diagnostics.Audio.IsOperational);
        Assert.Equal(48000, player.Diagnostics.Audio.SampleRate);
        await player.DisposeAsync();
        Assert.False(player.Diagnostics.Audio.IsOperational);
    }

    [Fact]
    public async Task LegacyAudioSinkRemainsCompatibleWithoutDiagnosticsContract()
    {
        await using var player = new WebRtcMediaPlayer(new WebRtcPlayerOptions
        {
            VideoDecoder = new DefaultWebRtcVideoDecoder(), AudioOutput = NullWebRtcAudioOutput.Instance
        });
        Assert.Equal(MediaAudioDiagnostics.Empty, player.Diagnostics.Audio);
    }

    private sealed class CommonAudioSink : IWebRtcAudioOutput, IMediaAudioOutput
    {
        private bool disposed;
        public int SampleRate => 48000;
        public int Channels => 2;
        public long PlayedFrames => 0;
        public bool IsOperational => !disposed;
        public bool IsSupported => IsOperational;
        public MediaAudioDiagnostics Diagnostics => new("Injected PCM", null, null, 48000, 2,
            TimeSpan.Zero, TimeSpan.Zero, IsOperational, 0, null);
        public void EnsureFormat(int rate, int channels) { }
        public void Write(ReadOnlySpan<byte> pcm) { }
        public void WriteSamples(ReadOnlySpan<short> pcm) { }
        public void SetVolume(double volume, bool muted) { }
        public void Pause() { }
        public void Resume() { }
        public void Reset() { }
        public void Dispose() => disposed = true;
    }
}
