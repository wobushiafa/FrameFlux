using FrameFlux.Unity;
using Xunit;

namespace FrameFlux.Unity.Tests;

public sealed class PcmAudioBufferTests
{
    [Fact]
    public void StereoChannelsAndRenderedClockSurviveUnderrun()
    {
        var buffer = new PcmAudioBuffer();
        buffer.Write(new short[] { short.MaxValue, short.MinValue, 16384, -16384 });
        var samples = new float[8];
        buffer.Read(samples, 2, 48000);
        Assert.InRange(samples[0], 0.999f, 1f);
        Assert.Equal(-1f, samples[1]);
        Assert.Equal(0.5f, samples[2]);
        Assert.Equal(-0.5f, samples[3]);
        Assert.All(samples.Skip(4), value => Assert.Equal(0, value));
        Assert.Equal(2, buffer.PlayedFrames);
        buffer.Read(samples, 2, 48000);
        Assert.Equal(2, buffer.PlayedFrames);
    }

    [Fact]
    public void BoundedQueueRetainsNewestCompleteStereoFrames()
    {
        var buffer = new PcmAudioBuffer(2);
        buffer.Write(new short[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(2, buffer.QueuedFrames);
        var samples = new float[4];
        buffer.Read(samples, 2, 48000);
        Assert.Equal(3 / 32768f, samples[0]);
        Assert.Equal(6 / 32768f, samples[3]);
    }

    [Fact]
    public void MonoTelephonyAudioIsResampledToUnityStereo()
    {
        var buffer = new PcmAudioBuffer();
        buffer.Configure(8000, 1);
        buffer.Write(new short[] { 16384, -16384 });
        Assert.Equal(12, buffer.QueuedFrames);
        var samples = new float[24];
        buffer.Read(samples, 2, 48000);
        Assert.All(samples.Take(12), value => Assert.Equal(0.5f, value));
        Assert.All(samples.Skip(12), value => Assert.Equal(-0.5f, value));
        Assert.Equal(12, buffer.PlayedFrames);
    }

    [Fact]
    public void PauseKeepsQueueAndResetPreventsOldAudioAfterSeek()
    {
        var buffer = new PcmAudioBuffer();
        buffer.Write(new short[] { 16384, 16384 });
        buffer.SetPaused(true);
        var samples = new float[2];
        buffer.Read(samples, 2, 48000);
        Assert.Equal(1, buffer.QueuedFrames);
        Assert.Equal(0, buffer.PlayedFrames);
        buffer.SetPaused(false);
        buffer.SetVolume(0.5, false);
        buffer.Read(samples, 2, 48000);
        Assert.Equal(0.25f, samples[0]);
        buffer.Write(new short[] { 16384, 16384 });
        buffer.Reset();
        buffer.Read(samples, 2, 48000);
        Assert.All(samples, value => Assert.Equal(0, value));
        Assert.Equal(1, buffer.PlayedFrames);
    }
}
