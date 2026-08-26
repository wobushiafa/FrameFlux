using System;

namespace FrameFlux.FFmpeg;

public sealed class RtspStreamErrorEventArgs : EventArgs
{
    public RtspStreamErrorEventArgs(RtspStreamError error)
    {
        Error = error;
    }

    public RtspStreamError Error { get; }
}
