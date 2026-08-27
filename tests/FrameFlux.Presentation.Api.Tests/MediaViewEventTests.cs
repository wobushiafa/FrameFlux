using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace FrameFlux.Presentation.Api.Tests;

public sealed class MediaViewEventTests
{
    [Theory]
    [InlineData(typeof(global::FrameFlux.Wpf.MediaView))]
    [InlineData(typeof(global::FrameFlux.Avalonia.MediaView))]
    public void FrameReceived_ContinuesAfterSubscriberThrows(Type viewType)
    {
        var view = RuntimeHelpers.GetUninitializedObject(viewType);
        var delivered = 0;
        EventHandler<MediaVideoFrame> handlers = (_, _) =>
            throw new InvalidOperationException("subscriber failure");
        handlers += (_, _) => delivered++;

        var eventField = viewType.GetField(
            "_frameReceived",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var dispatchMethod = viewType.GetMethod(
            "OnFrameReceived",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(eventField);
        Assert.NotNull(dispatchMethod);
        eventField.SetValue(view, handlers);

        dispatchMethod.Invoke(
            view,
            [
                null,
                new MediaVideoFrame(
                    ReadOnlyMemory<byte>.Empty,
                    1,
                    1,
                    4,
                    MediaPixelFormat.Bgra32,
                    1,
                    DateTimeOffset.UnixEpoch)
            ]);

        Assert.Equal(1, delivered);
    }
}
