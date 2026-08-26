namespace FrameFlux.FFmpeg;

public enum RtspConnectionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
    Failed
}
