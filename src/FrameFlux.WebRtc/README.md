# FrameFlux.WebRtc

WebRTC real-time media engine for FrameFlux using SIPSorcery.

## Features

- Implements `IMediaPlayer` and `IMediaPlayerFactory` for WebRTC live video streaming.
- Supports WHEP (`http://.../whep`, `https://.../whep`), WHIP, `webrtc://` URLs, and direct SDP/ICE configurations.
- Unmanaged frame memory pool with reusable `IMediaFrameLease`.
- Seamless presentation to existing Avalonia and WPF `MediaView` components via `_videoOutput.TryPresent(frameLease)`.

## Audio and video backends

WebRTC audio currently decodes G.711 PCMA and PCMU. The default output is
`waveOut` on Windows, ALSA on Linux, and `AudioTrack` on Android. Supply a
custom `IWebRtcAudioOutput` through `WebRtcPlayerOptions.AudioOutput` when the
application needs another device or audio codec pipeline.

H.264, H.265, VP8, VP9, AV1, and JPEG video decoding requires loadable native
FFmpeg libraries. JPEG frames are decoded by FFmpeg; the managed fallback does
not report decoded frames when native libraries are unavailable.

The package targets `net8.0` for desktop applications and `net10.0-android`
for Android applications.

## Tests

The four go2rtc tests are skipped in the default test run. Set
`FRAMEFLUX_GO2RTC_URL` to a reachable `stream.html?src=...` endpoint with
video and audio to run them. The D3D11 test requires Windows hardware decode.
The decoder tests run when native FFmpeg libraries are available in the test
runtime directory.
