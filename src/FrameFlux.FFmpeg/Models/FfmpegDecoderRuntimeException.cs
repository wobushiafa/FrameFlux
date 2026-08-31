using System;

namespace FrameFlux.FFmpeg;

internal sealed class FfmpegDecoderRuntimeException : Exception
{
    public FfmpegDecoderRuntimeException(
        string message,
        Exception? innerException,
        bool isHardwareVideoDecodingActive)
        : base(message, innerException)
    {
        IsHardwareVideoDecodingActive = isHardwareVideoDecodingActive;
    }

    public bool IsHardwareVideoDecodingActive { get; }
}
