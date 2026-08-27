using System;

namespace FrameFlux.FFmpeg;

internal sealed class RtspDecoderRuntimeException : Exception
{
    public RtspDecoderRuntimeException(
        string message,
        Exception? innerException,
        bool isHardwareVideoDecodingActive)
        : base(message, innerException)
    {
        IsHardwareVideoDecodingActive = isHardwareVideoDecodingActive;
    }

    public bool IsHardwareVideoDecodingActive { get; }
}
