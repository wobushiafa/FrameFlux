namespace FrameFlux;

public interface IRtspSession : IAsyncDisposable
{
    RtspSource Source { get; }

    RtspSessionOptions Options { get; }

    RtspSessionState State { get; }

    RtspSessionDiagnostics Diagnostics { get; }

    double Volume { get; set; }

    bool IsMuted { get; set; }

    event EventHandler<RtspSessionStateChangedEventArgs>? StateChanged;

    event EventHandler<RtspSessionErrorEventArgs>? Error;

    event EventHandler<RtspVideoFrame>? FrameReceived;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask<RtspSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IRtspSessionFactory
{
    IRtspSession Create(RtspSource source, RtspSessionOptions? options = null);
}

public enum RtspSessionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
    Faulted
}

public sealed class RtspSessionStateChangedEventArgs(
    RtspSessionState oldState,
    RtspSessionState newState) : EventArgs
{
    public RtspSessionState OldState { get; } = oldState;

    public RtspSessionState NewState { get; } = newState;
}

public sealed record RtspSessionError(
    string Code,
    string Message,
    bool WillRetry,
    Exception? Exception = null);

public sealed class RtspSessionErrorEventArgs(RtspSessionError error) : EventArgs
{
    public RtspSessionError Error { get; } = error;
}

public enum RtspFramePixelFormat
{
    Bgra32
}

public sealed record RtspVideoFrame(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Stride,
    RtspFramePixelFormat PixelFormat,
    long Sequence,
    DateTimeOffset CapturedAt);

public sealed record RtspSnapshot(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Stride,
    RtspFramePixelFormat PixelFormat,
    DateTimeOffset CapturedAt);

public sealed record RtspSessionDiagnostics(
    bool IsHardwareAccelerationActive,
    string HardwareDiagnostics,
    double ReadMilliseconds,
    double DecodeMilliseconds,
    int PerformanceSampleCount,
    string? LastError);
