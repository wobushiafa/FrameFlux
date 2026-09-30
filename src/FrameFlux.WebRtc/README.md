# FrameFlux.WebRtc

WebRTC real-time media engine for FrameFlux using SIPSorcery.

## Features

- Implements `IMediaPlayer` and `IMediaPlayerFactory` for WebRTC live video streaming.
- Supports WHEP (`http://.../whep`, `https://.../whep`), WHIP, `webrtc://` URLs, and direct SDP/ICE configurations.
- go2rtc WebSocket signaling supports passive ICE TCP candidates on desktop and Android, in addition to UDP. TCP packets use RFC 4571 framing and retain ICE authentication, DTLS and SRTP encryption.
- Video decoding runs on a bounded worker queue so slow decoders do not block network reception. Packet loss or decoder input loss triggers recovery at a fresh key frame to avoid corrupted reference frames.
- Unmanaged frame memory pool with reusable `IMediaFrameLease`.
- Seamless presentation to existing Avalonia and WPF `MediaView` components via `_videoOutput.TryPresent(frameLease)`.

## Audio and video backends

WebRTC audio currently decodes G.711 PCMA and PCMU. The default output is
`waveOut` on Windows, ALSA on Linux, and `AudioTrack` on Android. Supply a
custom `IWebRtcAudioOutput` through `WebRtcPlayerOptions.AudioOutput` when the
application needs another device or audio codec pipeline.

On Android, H.264 and H.265 use MediaCodec with the Avalonia Android Surface
outputs. The default decoder prefers a hardware codec and reports whether the
selected codec is actually hardware accelerated. `HardwareRequired` requires
a Surface output and a hardware codec; `HardwarePreferred` falls back to the
Android software codec or FFmpeg when needed. VP8, VP9, AV1, and JPEG use
FFmpeg or a custom `IWebRtcVideoDecoder`.

On desktop, H.264, H.265, VP8, VP9, AV1, and JPEG require loadable native
FFmpeg libraries or a custom decoder. Opening a stream fails when neither is
available.

The package targets `net8.0` for desktop applications and `net10.0-android`
for Android applications.

`WebRtcPlayerOptions.MaxPendingVideoFrames` (64), `MaxPendingVideoBytes`
(16 MiB), and `MaxPendingVideoAge` (2 seconds) bound the compressed decode
queue. They allow short TCP bursts to drain without discarding the reference
chain; they do not add a fixed playback delay. Use lower limits for strict
latency requirements, accepting more key-frame recovery on slow decoders.

## Tests

The four go2rtc tests are skipped in the default test run. Set
`FRAMEFLUX_GO2RTC_URL` to a reachable `stream.html?src=...` endpoint with
video and audio to run them. The D3D11 test requires Windows hardware decode.
The decoder tests run when native FFmpeg libraries are available in the test
runtime directory.
