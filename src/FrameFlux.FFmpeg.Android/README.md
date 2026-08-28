# FrameFlux.FFmpeg.Android

Android MediaCodec hardware decoding backend for `FrameFlux.FFmpeg`.

Applications normally register this backend through
`FrameFlux.Avalonia.Android.UseFrameFluxAndroid()`. Custom Android renderers can
implement `IAndroidVideoSurfaceOutput` and call
`FrameFluxAndroidMediaCodec.Register()` during application startup.

The backend uses the FFmpeg shared libraries supplied by
`FrameFlux.FFmpeg.NativeAssets.Android` directly for RTSP demuxing and audio.
Encoded H.264 or HEVC video access units are passed to Android MediaCodec.
