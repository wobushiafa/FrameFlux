using System;

namespace FrameFlux.FFmpeg;

internal sealed class FfmpegPlaybackErrorEventArgs : EventArgs
{
    public FfmpegPlaybackErrorEventArgs(FfmpegPlaybackError error)
    {
        Error = error;
    }

    public FfmpegPlaybackError Error { get; }
}
