namespace FrameFlux.FFmpeg;

internal enum PlaybackConnectionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
    Failed
}
