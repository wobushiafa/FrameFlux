using System.Collections.Concurrent;
using System.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class WebRtcVideoDecodeQueueTests
{
    [Fact]
    public async Task SlowDecoderDoesNotBlockReceiveAndRecoveryDiscardsDependentFrames()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoded = new ConcurrentQueue<(uint Timestamp, bool Refresh)>();
        var requests = 0;
        await using var queue = new WebRtcVideoDecodeQueue((frame, refresh) =>
        {
            decoded.Enqueue((frame.Timestamp, refresh));
            if (frame.Timestamp == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            if (frame.Timestamp == 5) recovered.TrySetResult();
        }, () => Interlocked.Increment(ref requests));

        try
        {
            queue.Enqueue(Frame(1, keyFrame: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // This must complete while the decoder is still blocked.
            await Task.Run(() => queue.Enqueue(Frame(2))).WaitAsync(TimeSpan.FromSeconds(1));
            queue.RequestRecovery();
            queue.Enqueue(Frame(3));
            queue.Enqueue(Frame(4, keyFrame: true));
            queue.Enqueue(Frame(5));
            release.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { (1u, true), (4u, true), (5u, false) }, decoded.ToArray());
            Assert.True(requests > 0);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task QueueOverflowWaitsForNewKeyFrameInsteadOfDecodingBrokenReferenceChain()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoded = new ConcurrentQueue<uint>();
        await using var queue = new WebRtcVideoDecodeQueue((frame, _) =>
        {
            decoded.Enqueue(frame.Timestamp);
            if (frame.Timestamp == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            if (frame.Timestamp == 100) recovered.TrySetResult();
        }, () => { }, new WebRtcPlayerOptions { MaxPendingVideoFrames = 8 });
        try
        {
            queue.Enqueue(Frame(1, keyFrame: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (uint timestamp = 2; timestamp <= 20; timestamp++) queue.Enqueue(Frame(timestamp));
            queue.Enqueue(Frame(100, keyFrame: true));
            release.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new uint[] { 1, 100 }, decoded.ToArray());
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task StopWaitsForActiveDecodeAndDiscardsQueuedFrames()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var queue = new WebRtcVideoDecodeQueue((_, _) =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        }, () => { });
        try
        {
            queue.Enqueue(Frame(1, keyFrame: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Enqueue(Frame(2));
            var stopped = queue.DisposeAsync().AsTask();
            Assert.False(stopped.IsCompleted);
            release.Set();
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Enqueue(Frame(3, keyFrame: true));
            Assert.Equal(1, count);
        }
        finally
        {
            release.Set();
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task NetworkBurstRetainsReferenceFramesAndIdleWorkerDrainsPendingOutput()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoded = new ConcurrentQueue<uint>();
        var requests = 0;
        await using var queue = new WebRtcVideoDecodeQueue((frame, _) =>
        {
            decoded.Enqueue(frame.Timestamp);
            if (frame.Timestamp == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        }, () => Interlocked.Increment(ref requests), drainOutput: () =>
        {
            if (decoded.Count == 41) drained.TrySetResult();
        });
        try
        {
            queue.Enqueue(Frame(1, keyFrame: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (uint timestamp = 2; timestamp <= 41; timestamp++) queue.Enqueue(Frame(timestamp));
            release.Set();
            // No more network input arrives to trigger output drain.
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Enumerable.Range(1, 41).Select(value => (uint)value), decoded);
            Assert.Equal(0, requests);
        }
        finally { release.Set(); }
    }

    private static WebRtcEncodedFrame Frame(uint timestamp, bool keyFrame = false) => new(
        new IPEndPoint(IPAddress.Loopback, 5004), timestamp,
        [0, 0, 0, 1, keyFrame ? (byte)0x65 : (byte)0x41, 1, 2],
        new VideoFormat(VideoCodecsEnum.H264, 96));
}
