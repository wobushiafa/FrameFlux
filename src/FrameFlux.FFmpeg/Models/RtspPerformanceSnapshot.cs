namespace FrameFlux.FFmpeg;

public readonly record struct RtspPerformanceSnapshot(
    double ReadMilliseconds,
    double CodecMilliseconds,
    double HardwareTransferMilliseconds,
    double DecodeMilliseconds,
    double ConvertMilliseconds,
    double DispatchMilliseconds,
    int SampleCount)
{
    public double PipelineCpuMilliseconds =>
        CodecMilliseconds + HardwareTransferMilliseconds + ConvertMilliseconds + DispatchMilliseconds;
}
