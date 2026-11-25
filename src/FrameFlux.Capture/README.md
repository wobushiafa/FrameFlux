# FrameFlux.Capture

Local USB camera and video/audio capture device backend for FrameFlux.

## Features

- Enumerates local video capture devices (`MediaCaptureDevices.GetVideoDevices`) with unique hardware IDs (`SymbolicLink`) and supported `(Width, Height, FrameRate, PixelFormat)` formats.
- Supports `capture://video/<index>` and `capture://video/device?deviceId=...` URIs via `CaptureSource`.
- Windows implementation powered by Media Foundation (`IMFSourceReader`) with automatic format selection (`NV12 > H264 > HEVC > MJPEG > YUY2`), D3D11 GPU texture output (`GpuComposition` / `NativeSurface`), and CPU `Bgra32` fallback (`SoftwareBitmap`).
- Optional paired WASAPI audio capture and playback loopback (automatically matched by USB `VID/PID` so silent cameras do not accidentally open unrelated microphones).
- Supports shared capture sessions (`MediaSessionSharingMode.Shared`) and USB hot-unplug/replug recovery (`MediaReconnectOptions`).
