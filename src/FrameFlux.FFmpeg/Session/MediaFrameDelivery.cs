namespace FrameFlux.FFmpeg;

internal static class MediaFrameDelivery
{
    internal static void Deliver(
        IMediaVideoOutput? output,
        IMediaFrameLease frame,
        Action<Exception>? onError = null)
    {
        var accepted = false;
        try
        {
            accepted = output is not null &&
                output.Supports(frame.PixelFormat) &&
                output.TryPresent(frame);
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
        }
        finally
        {
            if (!accepted)
            {
                frame.Dispose();
            }
        }
    }
}
