namespace FrameFlux.FFmpeg;

internal static class MediaFrameDelivery
{
    internal static bool Deliver(
        IMediaVideoOutput? output,
        IMediaFrameLease frame,
        out Exception? error)
    {
        error = null;
        var accepted = false;
        try
        {
            accepted = output is not null &&
                output.Supports(frame.StorageKind, frame.PixelFormat) &&
                output.TryPresent(frame);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            if (!accepted)
            {
                frame.Dispose();
            }
        }

        return accepted;
    }

    internal static void Deliver(
        IMediaVideoOutput? output,
        IMediaFrameLease frame,
        Action<Exception>? onError = null)
    {
        Deliver(output, frame, out var error);
        if (error is not null)
        {
            onError?.Invoke(error);
        }
    }
}
