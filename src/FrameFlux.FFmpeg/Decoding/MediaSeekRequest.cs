namespace FrameFlux.FFmpeg;

internal sealed class MediaSeekRequest(TimeSpan position)
{
    internal TimeSpan Position { get; } = position;

    internal TaskCompletionSource<object?> Completion { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
