# FrameFlux.Abstractions

This package defines stable, platform-neutral media playback contracts. New
integrations use `IMediaPlayer`, `MediaSource`, `MediaOpenOptions`,
`MediaCapabilities`, generic frames, snapshots, diagnostics, and playback
states.

The original RTSP source and session contracts remain available for
compatibility. Use this package with `FrameFlux.FFmpeg` and either
`FrameFlux.Wpf` or `FrameFlux.Avalonia`.
