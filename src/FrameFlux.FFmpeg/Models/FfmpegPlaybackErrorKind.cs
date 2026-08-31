namespace FrameFlux.FFmpeg;

internal enum FfmpegPlaybackErrorKind
{
    OpenFailed,
    ReadFailed,
    DecodeFailed,
    EndOfStream,
    Unknown
}
