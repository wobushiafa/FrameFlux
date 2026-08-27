namespace FrameFlux.FFmpeg;

internal enum RtspConnectionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
    Failed
}
