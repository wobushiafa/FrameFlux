using FrameFlux;
using FrameFlux.FFmpeg;
using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class MediaCodecBitstreamAdapterTests
{
    [Fact]
    public void ParseAvcConfiguration_SeparatesSpsAndPps()
    {
        byte[] configuration =
        [
            1, 100, 0, 31, 0xff, 0xe1,
            0, 3, 0x67, 0x64, 0x00,
            1, 0, 2, 0x68, 0xee
        ];

        var result = MediaCodecBitstreamAdapter.Parse(
            NativeVideoCodec.H264,
            configuration);

        Assert.Equal(4, result.NalLengthSize);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x64, 0x00 }, result.CodecSpecificData0);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x68, 0xee }, result.CodecSpecificData1);
    }

    [Fact]
    public void ParseHevcConfiguration_CollectsDecoderNalArrays()
    {
        var configuration = new byte[30];
        configuration[0] = 1;
        configuration[21] = 0xfd;
        configuration[22] = 1;
        configuration[23] = 0x20;
        configuration[25] = 1;
        configuration[27] = 2;
        configuration[28] = 0x40;
        configuration[29] = 0x01;

        var result = MediaCodecBitstreamAdapter.Parse(
            NativeVideoCodec.Hevc,
            configuration);

        Assert.Equal(2, result.NalLengthSize);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x40, 0x01 }, result.CodecSpecificData0);
        Assert.Null(result.CodecSpecificData1);
    }

    [Fact]
    public void NormalizePacket_ReusesCallerBuffer()
    {
        byte[] destination = new byte[64];
        var original = destination;
        var length = MediaCodecBitstreamAdapter.NormalizePacket(
            new byte[] { 0, 2, 0x65, 0x88 }, 2, ref destination);

        Assert.Same(original, destination);
        Assert.Equal(6, length);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0x88 }, destination[..length]);
    }

    [Fact]
    public void NormalizePacket_ConvertsLengthPrefixedNalUnits()
    {
        byte[] packet = [0, 0, 0, 2, 0x65, 0x88, 0, 0, 0, 1, 0x06];

        var result = MediaCodecBitstreamAdapter.NormalizePacket(packet, 4);

        Assert.Equal(
            new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0, 0, 0, 1, 0x06 },
            result);
    }

    [Fact]
    public void NormalizePacket_RejectsTruncatedNalUnit()
    {
        byte[] packet = [0, 0, 0, 4, 0x65];

        Assert.Throws<InvalidDataException>(
            () => MediaCodecBitstreamAdapter.NormalizePacket(packet, 4));
    }

    [Fact]
    public void AdaptiveOutput_ForwardsOptionalPlatformFeature()
    {
        var feature = new TestFeature();
        var primary = new FeatureOutput(feature);
        var fallback = new FeatureOutput(null);
        var output = new AdaptiveMediaVideoOutput(primary, fallback);

        Assert.Same(feature, output.GetVideoOutputFeature(typeof(ITestFeature)));
    }

    private sealed class FeatureOutput(object? feature) :
        IMediaVideoOutput,
        IMediaVideoOutputFeatureProvider
    {
        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) => false;

        public bool TryPresent(IMediaFrameLease frame) => false;

        public object? GetVideoOutputFeature(Type featureType) => feature;
    }

    private interface ITestFeature;

    private sealed class TestFeature : ITestFeature;
}
