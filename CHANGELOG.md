# Changelog

All notable changes to FrameFlux are documented in this file.

## [0.1.2] - 2026-09-20

### Added

- Resilient FFmpeg HLS playback for `.m3u8` live streams, including packet prefetch and buffered audio output.
- Long-running HLS audio/video clock recovery and diagnostics for audio buffering and synchronization.

### Changed

- Generalized FFmpeg media session handling beyond RTSP-only terminology.
- Kept the demo source gain at the neutral `0 dB` default; per-player gain does not change the system master volume.
- Updated package and integration documentation for RTSP/RTSPS, HLS, WebRTC, and local-file sources.

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
