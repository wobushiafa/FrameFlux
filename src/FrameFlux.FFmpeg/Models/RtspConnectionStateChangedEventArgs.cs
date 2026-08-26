using System;

namespace FrameFlux.FFmpeg;

public sealed class RtspConnectionStateChangedEventArgs : EventArgs
{
    public RtspConnectionStateChangedEventArgs(RtspConnectionState oldState, RtspConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public RtspConnectionState OldState { get; }

    public RtspConnectionState NewState { get; }
}
