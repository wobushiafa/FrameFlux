using System;

namespace FrameFlux.FFmpeg;

internal sealed class PlaybackConnectionStateChangedEventArgs : EventArgs
{
    public PlaybackConnectionStateChangedEventArgs(PlaybackConnectionState oldState, PlaybackConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public PlaybackConnectionState OldState { get; }

    public PlaybackConnectionState NewState { get; }
}
