namespace FrameFlux.FFmpeg;

internal enum RtspStreamErrorKind
{
    OpenFailed,
    ReadFailed,
    DecodeFailed,
    EndOfStream,
    Unknown
}
