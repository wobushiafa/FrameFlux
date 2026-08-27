using System;

namespace FrameFlux.FFmpeg;

internal sealed class RtspStreamErrorEventArgs : EventArgs
{
    public RtspStreamErrorEventArgs(RtspStreamError error)
    {
        Error = error;
    }

    public RtspStreamError Error { get; }
}
