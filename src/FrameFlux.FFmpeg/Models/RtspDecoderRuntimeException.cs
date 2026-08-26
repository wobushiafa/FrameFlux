using System;

namespace FrameFlux.FFmpeg;

public sealed class RtspDecoderRuntimeException : Exception
{
    public RtspDecoderRuntimeException(string message, Exception? innerException, bool hardwareAccelerationActive)
        : base(message, innerException)
    {
        HardwareAccelerationActive = hardwareAccelerationActive;
    }

    public bool HardwareAccelerationActive { get; }
}
