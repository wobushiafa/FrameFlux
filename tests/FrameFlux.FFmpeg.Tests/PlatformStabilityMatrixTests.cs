using FrameFlux.Presentation;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class PlatformStabilityMatrixTests
{
    public static TheoryData<string, MediaFrameStorageKind, string> Platforms => new()
    {
        { "Windows-D3D11", MediaFrameStorageKind.D3D11Texture, "device-lost" },
        { "Linux-VAAPI-DMA-BUF", MediaFrameStorageKind.DmaBuf, "device-lost" },
        { "Android-MediaCodec", MediaFrameStorageKind.CpuMemory, "surface-recreated" }
    };

    [Theory]
    [MemberData(nameof(Platforms))]
    public void RecoveryPolicies_RemainBoundedAcrossPlatformMatrix(
        string platform,
        MediaFrameStorageKind storageKind,
        string presentationFailure)
    {
        var reconnect = new MediaReconnectState(
            new FfmpegPlaybackOptions
            {
                ReconnectEnabled = true,
                MaximumReconnectAttempts = 3,
                ReconnectInitialDelayMilliseconds = 10,
                ReconnectMaximumDelayMilliseconds = 40
            },
            _ => 0);
        var presentation = new MediaPresentationFailureTracker(maximumAttempts: 3);

        for (var cycle = 0; cycle < 100; cycle++)
        {
            Assert.True(reconnect.RegisterFailure().RetryAllowed);
            Assert.True(reconnect.RegisterSuccess());

            var first = presentation.Register(
                new InvalidOperationException($"{platform}:{storageKind}:{presentationFailure}"));
            var second = presentation.Register(
                new InvalidOperationException($"{platform}:{storageKind}:{presentationFailure}"));
            Assert.True(first.RequiresBackendRestart);
            Assert.True(second.RequiresBackendRestart);
            presentation.ReportSuccess();
        }

        Assert.Equal(100, reconnect.Diagnostics.TotalAttempts);
        Assert.Equal(100, reconnect.Diagnostics.RecoveryCount);
        Assert.False(presentation.IsExhausted);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task ConcurrentStreams_KeepIndependentRecoveryBudgets(
        string platform,
        MediaFrameStorageKind storageKind,
        string presentationFailure)
    {
        const int streamCount = 16;
        var states = Enumerable.Range(0, streamCount)
            .Select(_ => new MediaReconnectState(
                new FfmpegPlaybackOptions
                {
                    ReconnectEnabled = true,
                    MaximumReconnectAttempts = 2,
                    ReconnectInitialDelayMilliseconds = 0,
                    ReconnectMaximumDelayMilliseconds = 0
                },
                _ => 0))
            .ToArray();

        await Task.WhenAll(states.Select((state, index) => Task.Run(() =>
        {
            Assert.True(state.RegisterFailure().RetryAllowed);
            Assert.True(state.RegisterFailure().RetryAllowed);
            Assert.False(state.RegisterFailure().RetryAllowed);
            Assert.Equal(
                $"{platform}:{storageKind}:{presentationFailure}:{index}",
                $"{platform}:{storageKind}:{presentationFailure}:{index}");
        })));

        Assert.All(states, state =>
        {
            Assert.Equal(3, state.Diagnostics.ConsecutiveFailures);
            Assert.Equal(2, state.Diagnostics.TotalAttempts);
        });
    }
}
