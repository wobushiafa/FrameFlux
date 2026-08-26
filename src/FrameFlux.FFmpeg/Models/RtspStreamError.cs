using System;

namespace FrameFlux.FFmpeg;

public sealed record RtspStreamError(
    RtspStreamErrorKind Kind,
    string Message,
    Exception? Exception = null,
    bool WillRetry = true);
