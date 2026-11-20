using FrameFlux.Unity.Native;
using Xunit;

namespace FrameFlux.Unity.Tests;

public sealed unsafe class UnityFrameMailboxTests
{
    [Fact]
    public void Copy_StripsPaddingSwapsChannelsAndFlipsVertically()
    {
        var mailbox = new UnityFrameMailbox();
        mailbox.Present(Frame([1, 2, 3, 255, 90, 91, 92, 93, 4, 5, 6, 128, 94, 95, 96, 97], 1, 2, 8));
        var bytes = new byte[8];
        int width = 0, height = 0;
        long sequence = 0;
        fixed (byte* destination = bytes)
            Assert.Equal(1, mailbox.CopyRgba(destination, bytes.Length, &width, &height, &sequence));
        Assert.Equal(1, width);
        Assert.Equal(2, height);
        Assert.Equal(new byte[] { 6, 5, 4, 128, 3, 2, 1, 255 }, bytes);
        Assert.Equal(1, sequence);
    }

    [Fact]
    public void SmallBuffer_DoesNotConsumeFrameOrOverwriteDestination()
    {
        var mailbox = new UnityFrameMailbox();
        mailbox.Present(Frame([1, 2, 3, 4], 1, 1, 4));
        byte sentinel = 77;
        int width = 0, height = 0;
        long sequence = 0;
        Assert.Equal(2, mailbox.CopyRgba(&sentinel, 1, &width, &height, &sequence));
        Assert.Equal(77, sentinel);
        Assert.Equal(0, sequence);
        Assert.Equal(1, width);
        Assert.Equal(1, height);
        var bytes = new byte[4];
        fixed (byte* destination = bytes)
        {
            Assert.Equal(1, mailbox.CopyRgba(destination, 4, &width, &height, &sequence));
            Assert.Equal(0, mailbox.CopyRgba(destination, 4, &width, &height, &sequence));
        }
    }

    [Fact]
    public void LatestFrameWins_AndSequenceSurvivesStreamChanges()
    {
        var mailbox = new UnityFrameMailbox();
        mailbox.Present(Frame([1, 2, 3, 4], 1, 1, 4));
        mailbox.Present(Frame([5, 6, 7, 8], 1, 1, 4));
        var bytes = new byte[4];
        int width = 0, height = 0;
        long sequence = 0;
        fixed (byte* destination = bytes)
        {
            Assert.Equal(1, mailbox.CopyRgba(destination, 4, &width, &height, &sequence));
            Assert.Equal(new byte[] { 7, 6, 5, 8 }, bytes);
            Assert.Equal(2, sequence);
            mailbox.Clear();
            Assert.Equal(0, mailbox.CopyRgba(destination, 4, &width, &height, &sequence));
            mailbox.Present(Frame([9, 10, 11, 12], 1, 1, 4));
            Assert.Equal(1, mailbox.CopyRgba(destination, 4, &width, &height, &sequence));
            Assert.Equal(3, sequence);
        }
    }

    [Fact]
    public void MalformedFrame_IsRejectedBeforeAccessingPixelMemory()
    {
        var mailbox = new UnityFrameMailbox();
        Assert.Throws<ArgumentException>(() => mailbox.Present(Frame([1, 2, 3], 1, 1, 4)));
        Assert.Throws<ArgumentException>(() => mailbox.Present(Frame([1, 2, 3, 4], 2, 1, 4)));
    }

    private static MediaVideoFrame Frame(byte[] bytes, int width, int height, int stride) =>
        new(bytes, width, height, stride, MediaPixelFormat.Bgra32, 1, DateTimeOffset.UtcNow);
}
