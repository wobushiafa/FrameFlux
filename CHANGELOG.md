# Changelog

All notable changes to FrameFlux are documented in this file.

## [0.1.1] - 2026-09-08

### Changed

- Improved Avalonia direct-GPU media rendering and Windows D3D11 composition startup behavior.
- Reduced contention while starting concurrent media operations.

## [0.1.0] - 2026-09-01

### Added

- Cross-platform media abstractions and an FFmpeg-backed RTSP player.
- Audio playback, synchronization, volume, mute, diagnostics, and reconnect support.
- WPF and Avalonia media controls with software and platform GPU presentation paths.
- Windows D3D11, Linux EGL/VAAPI/DMA-BUF, and Android MediaCodec presentation backends.
- Dedicated native FFmpeg asset packages for Windows, Linux, and Android.
- Local file playback with duration, seeking, and pitch-preserving audio/video rates from 0.25x to 4x.
- MIT licensing and standardized NuGet package metadata.

### Release gates

- Resolve FFmpeg GPLv3-or-later redistribution obligations and record exact build provenance before publishing Windows or Linux native asset packages.
- Record Android FFmpeg provenance and replace all Android native binaries with 16 KB page-aligned builds before publishing the Android native asset package.
