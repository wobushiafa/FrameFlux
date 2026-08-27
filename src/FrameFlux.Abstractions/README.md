# FrameFlux.Abstractions

This package defines stable, platform-neutral media playback contracts. New
integrations use `IMediaPlayer`, `MediaSource`, `MediaOpenOptions`,
`MediaCapabilities`, owned CPU frames, snapshots, diagnostics, playback
states, and the `IMediaVideoOutput` lease-based rendering contract.

`IMediaFrameLease` exposes explicit CPU or D3D11 buffer descriptors instead
of platform fields that are invalid for most frames. An output owns a frame
only when `TryPresent` returns `true`; the caller releases rejected or failed
deliveries.

Use this package with a media backend such as `FrameFlux.FFmpeg` and either
`FrameFlux.Wpf` or `FrameFlux.Avalonia`.
