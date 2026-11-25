namespace FrameFlux.Capture;

internal interface ICaptureBackend : IDisposable
{
    CaptureOpenResult Open(
        ParsedCaptureUri parsedUri,
        MediaOpenOptions openOptions,
        CapturePlayerOptions playerOptions);

    void Start(
        Func<bool> prefersHardwareFrame,
        Func<bool> needsCpuFrame,
        Action<CaptureDeliveredFrame> onFrame,
        Action<Exception, bool> onError);

    void Stop();
}

internal interface ICaptureLiveDiagnosticsProvider
{
    string? CurrentDiagnosticsDescription { get; }

    bool? CurrentIsHardwareVideoActive { get; }
}

internal interface ICaptureAudioLoopback : IDisposable
{
    MediaAudioDiagnostics Diagnostics { get; }

    void SetVolume(double volume, bool isMuted, double gainDecibels);

    void Start();

    void Stop();
}

internal sealed record CaptureOpenResult(
    string ResolvedDeviceId,
    string DeviceDisplayName,
    string? PairedAudioDeviceId,
    MediaCaptureVideoFormat NegotiatedFormat,
    MediaCapturePixelFormat OutputPixelFormat,
    string DiagnosticsDescription,
    bool IsHardwareVideoActive = false);

internal readonly record struct CaptureDeliveredFrame(
    CaptureMediaFrameLease? HardwareLease,
    CaptureMediaFrameLease? CpuLease,
    int Width,
    int Height,
    double ReadMilliseconds,
    double DecodeMilliseconds);
