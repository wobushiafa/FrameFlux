# FrameFlux.Capture

Local USB camera and video/audio capture device backend for FrameFlux (Windows & Linux).

## Features

- Enumerates local video capture devices (`MediaCaptureDevices.GetVideoDevices`) with stable hardware IDs (Windows `SymbolicLink`, Linux `/dev/v4l/by-id` / `/dev/v4l/by-path` / `/dev/videoN`) and supported `(Width, Height, FrameRate, PixelFormat)` formats.
- Supports `capture://video/<index>` and `capture://video/device?deviceId=...` URIs via `CaptureSource`.
- **Windows**: Media Foundation (`IMFSourceReader`) with automatic format selection (`NV12 > H264 > HEVC > MJPEG > YUY2`), D3D11 GPU texture output (`GpuComposition` / `NativeSurface`), CPU `Bgra32` fallback (`SoftwareBitmap`), and paired WASAPI audio loopback.
- **Linux**: Video4Linux2 (`ioctl` + `mmap` + `poll`) with automatic format selection (`NV12 > MJPEG > YUY2 > YUV420P > BGRA32`), runtime-probed `libturbojpeg.so.0` SIMD MJPEG decoding, `VIDIOC_EXPBUF` DMA-BUF zero-copy output for NV12 devices (compatible with `FrameFlux.Avalonia.Linux`), CPU SIMD `Bgra32` fallback, and paired ALSA (`libasound.so.2`) audio loopback matched via `sysfs` USB topology / `VID:PID`.
- Supports shared capture sessions (`MediaSessionSharingMode.Shared`) and USB hot-unplug/replug recovery (`MediaReconnectOptions`).
