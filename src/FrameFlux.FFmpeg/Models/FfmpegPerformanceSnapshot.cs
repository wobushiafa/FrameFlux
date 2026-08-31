namespace FrameFlux.FFmpeg;

internal readonly record struct FfmpegPerformanceSnapshot(
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
