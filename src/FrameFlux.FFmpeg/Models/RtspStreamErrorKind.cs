namespace FrameFlux.FFmpeg;

public enum RtspStreamErrorKind
{
    OpenFailed,
    ReadFailed,
    DecodeFailed,
    EndOfStream,
    Unknown
}
