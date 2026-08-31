using System;
using System.Diagnostics;

namespace FrameFlux.FFmpeg;

internal sealed class FfmpegPerformanceTracker
{
    private const int SamplesPerSnapshot = 30;
    private readonly Action<FfmpegPerformanceSnapshot> _publish;
    private long _totalReadTicks;
    private long _totalCodecTicks;
    private long _totalHardwareTransferTicks;
    private long _totalDecodeTicks;
    private long _totalConvertTicks;
    private long _totalDispatchTicks;
    private int _sampleCount;

    internal FfmpegPerformanceTracker(Action<FfmpegPerformanceSnapshot> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    internal void Record(
        long readTicks,
        long codecTicks,
        long hardwareTransferTicks,
        long decodeTicks,
        long convertTicks,
        long dispatchTicks)
    {
        _totalReadTicks += readTicks;
        _totalCodecTicks += codecTicks;
        _totalHardwareTransferTicks += hardwareTransferTicks;
        _totalDecodeTicks += decodeTicks;
        _totalConvertTicks += convertTicks;
        _totalDispatchTicks += dispatchTicks;
        _sampleCount++;

        if (_sampleCount < SamplesPerSnapshot)
        {
            return;
        }

        var snapshot = new FfmpegPerformanceSnapshot(
            ReadMilliseconds: ToAverageMilliseconds(_totalReadTicks),
            CodecMilliseconds: ToAverageMilliseconds(_totalCodecTicks),
            HardwareTransferMilliseconds: ToAverageMilliseconds(_totalHardwareTransferTicks),
            DecodeMilliseconds: ToAverageMilliseconds(_totalDecodeTicks),
            ConvertMilliseconds: ToAverageMilliseconds(_totalConvertTicks),
            DispatchMilliseconds: ToAverageMilliseconds(_totalDispatchTicks),
            SampleCount: _sampleCount);

        _totalReadTicks = 0;
        _totalCodecTicks = 0;
        _totalHardwareTransferTicks = 0;
        _totalDecodeTicks = 0;
        _totalConvertTicks = 0;
        _totalDispatchTicks = 0;
        _sampleCount = 0;
        _publish(snapshot);
    }

    private double ToAverageMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency / _sampleCount;
}
