# Changelog

All notable changes to FrameFlux are documented in this file.

## [0.2.0] - 2026-11-25

### Added

- New `FrameFlux.Capture` managed NuGet package for local USB cameras and capture devices (Windows Media Foundation + WASAPI), featuring device enumeration, format scoring and negotiation, shared capture session pooling, exponential-backoff reconnect on hot-unplug, VID/PID paired audio loopback, `IMFDXGIDeviceManager` GPU texture copy, D3D11 NV12 texture upload, and WPF/Avalonia demo integration.

### Changed

- End-to-end performance optimization across capture, decoding, audio output, and multi-platform rendering:
  - **Capture**: Added SIMD pixel conversion (`CapturePixelConverter`), zero-closure frame leases (`CaptureMediaFrameLease`), `readonly record struct` frame delivery, lock-free Copy-on-Write subscriber/event snapshots, and 64-byte aligned buffer pooling with dynamic 2-frame retention.
  - **FFmpeg**: Enabled multithreaded software video decoding, skipped redundant `forceOpaqueAlpha` scans for opaque YUV sources, allocated `extra_hw_frames` only when preserving hardware frames, eliminated per-frame `SafeHandle` wrapping in `FfmpegDecoder`, converted `FrameLayout` to `readonly record struct`, refactored `NetworkPacketPrefetchBuffer` to pooled native packets, introduced high-resolution `WaitableTimer` waits (`HighResolutionWait`), optimized Windows/Linux/Android audio buffer reuse, and introduced zero-allocation/pooled shared session fan-out (`MediaFrameDelivery`, `ReferenceCountedMediaFrameLease`, `SharedMediaSessionPool`).
  - **WebRTC**: Added AVX2/SSSE3 SIMD pixel conversion (`WebRtcPixelConverter`), multithreaded FFmpeg software decoding with static release delegates, Android MediaCodec hardware codec caching and `DirectByteBuffer` input writes, improved RTP loss/keyframe recovery and consecutive GPU failure fallback threshold, and pooled ALSA audio buffers.
  - **Rendering & UI**: Cached `ID3D11VideoProcessorInputView` and enabled fast `ResizeBuffers` in `WindowsD3D11Presenter`, added reference-counted cleanup for `WindowsD3D11DeviceSynchronizer.DeviceLocks`, switched `WindowsD3D11DirectVisualHandler` to frame-driven on-demand invalidation, added shared `SharedD3D9DevicePool` and reduced GPU flushes in WPF `D3D11ImageMediaOutput`, hardened cross-D3D11/D3D9Ex/MIL composition synchronization, cached OpenGL/EGL/DMA-BUF and Android SurfaceTexture/NativeSurface state and vertices, and cached `RenderScaling` via `TopLevel.ScalingChanged`.
- Updated NuGet release packaging and validation (`publish-nuget.yml`, `eng/pack-managed.ps1`, `eng/verify-packages.py`) to publish 12 managed packages (adding `FrameFlux.Capture`) and 3 native FFmpeg packages.

### Fixed

- Fixed `CaptureSessionSubscriber.WantsCpuFrame` triggering unnecessary GPU-to-CPU readbacks and BGRA conversions when D3D11 output is preferred.
- Fixed FFmpeg GPU playback with `SnapshotPolicy.KeepLatestFrame` performing unused GPU-to-CPU readbacks and per-frame LOH allocations.
- Fixed COM lifetime race between synchronous `IMFSourceReader.ReadSample` and `Stop()` in Windows Media Foundation capture.
- Fixed transient GPU contention in `WindowsD3D11DirectVisualHandler` permanently degrading to software fallback.

### Removed

- Removed preview Unity 2022.3 LTS integration (`FrameFlux.Unity.Native`, `FrameFlux.Unity.Tests`, `unity/com.frameflux.player`, Unity packaging/smoke scripts, and `docs/UNITY-COMPATIBILITY.md`).

## [0.1.4] - 2026-11-20

### Added

- Multi-platform `MediaSessionSharingMode.Shared` support across all video presentation modes (`Automatic`, `GpuComposition`, `NativeSurface`, `SoftwareBitmap`), including mixed-mode multi-view delivery and dynamic view attach/detach on Windows D3D11 (WPF and Avalonia), Linux X11/EGL/DMA-BUF (Avalonia), and Android MediaCodec/EGL/SurfaceTexture (Avalonia).
- Public `IMediaAudioOutput` contract and `FfmpegMediaPlayerFactoryOptions.AudioOutputFactory` for custom PCM audio sinks.
- `.NET Standard 2.1` target framework support for `FrameFlux.Abstractions`, `FrameFlux.Presentation`, `FrameFlux.FFmpeg`, and `FrameFlux.WebRtc`.
- Unity 2022.3 LTS (Windows x64) UPM preview package sources and packaging scripts (`com.frameflux.player`, `MediaView`, `MediaRawImage`, `MediaMaterial`, `UnityAudioOutput`).

### Changed

- Synchronized shared D3D11 device and video context operations (`WindowsD3D11DeviceSynchronizer`) and reserved extra hardware decode surfaces (`extra_hw_frames`) for concurrent shared hardware rendering on Windows.
- Made concurrent `DisposeAsync` calls share a single disposal task, preserved injected audio factories across software decoder fallback, and reported injected audio sinks in WebRTC diagnostics.
- Removed redundant eager FFmpeg registration from the Avalonia desktop demo.

### Fixed

- Set `eglSwapInterval(..., 0)` on Linux X11 `NativeSurface` presenters to prevent multi-view presentation blocking.

## [0.1.3] - 2026-10-08

### Fixed

- Restored H.264 video with go2rtc/Pion WebRTC sources by advertising compatible SDP profile and packetization parameters.

### Changed

- Replaced the Windows x64 runtime with FFmpeg 9.0.1, including D3D11VA/DXVA2 hardware decoding and Schannel HTTPS.
- Rebuilt Linux x64 FFmpeg 9.0.1 on Ubuntu 22.04 for a glibc 2.35 baseline, retaining VAAPI, libdrm and HTTPS support.
- Publish Windows, Linux and Android native asset packages with corresponding source, build instructions and LGPL license texts. Android packages include the existing 16 KB aligned arm64 and x64 libraries.

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
