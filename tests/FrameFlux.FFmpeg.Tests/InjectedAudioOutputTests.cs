using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class InjectedAudioOutputTests
{
    [Fact]
    public async Task PlayerDisposalIsSafeWhenRepeatedAndConcurrent()
    {
        var player = new FfmpegMediaPlayer();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => player.DisposeAsync().AsTask()));
        await player.DisposeAsync();
        Assert.Equal(MediaPlaybackState.Stopped, player.State);
    }

    [Fact]
    public void CustomAudioCannotBeAccidentallySharedAcrossPlayers()
    {
        var calls = 0;
        var factory = new FfmpegMediaSessionFactory(options: new FfmpegMediaPlayerFactoryOptions
        {
            AudioOutputFactory = (_, _) => { calls++; return new Audio(); }
        });
        Assert.Throws<NotSupportedException>(() => factory.Create(MediaSource.FromFile("test.mp4"),
            new MediaOpenOptions { SessionSharing = MediaSessionSharingMode.Shared }, 1, false, null));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void InvalidFormatDisposesTheTransferredOutput()
    {
        var output = new Audio { SampleRate = 44100 };
        Assert.Throws<ArgumentException>(() => new InjectedAudioOutput(output));
        Assert.True(output.Disposed);
    }

    private sealed class Audio : IMediaAudioOutput
    {
        public bool Disposed { get; private set; }
        public int SampleRate { get; init; } = 48000;
        public int Channels => 2;
        public long PlayedFrames => 0;
        public bool IsOperational => true;
        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;
        public void Write(ReadOnlySpan<byte> pcm) { }
        public void Reset() { }
        public void Dispose() => Disposed = true;
    }
}
