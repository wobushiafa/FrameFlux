using System;

namespace FrameFlux.FFmpeg;

internal sealed record RtspStreamError(
    RtspStreamErrorKind Kind,
    string Message,
    Exception? Exception = null,
    bool WillRetry = true);
