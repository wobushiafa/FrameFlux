using System;

namespace FrameFlux.FFmpeg;

internal sealed record FfmpegPlaybackError(
    FfmpegPlaybackErrorKind Kind,
    string Message,
    Exception? Exception = null,
    bool WillRetry = true);
