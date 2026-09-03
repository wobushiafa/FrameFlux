# FrameFlux.WebRtc

WebRTC real-time media engine for FrameFlux using SIPSorcery.

## Features

- Implements `IMediaPlayer` and `IMediaPlayerFactory` for WebRTC live video streaming.
- Supports WHEP (`http://.../whep`, `https://.../whep`), WHIP, `webrtc://` URLs, and direct SDP/ICE configurations.
- Unmanaged frame memory pool with reusable `IMediaFrameLease`.
- Seamless presentation to existing Avalonia and WPF `MediaView` components via `_videoOutput.TryPresent(frameLease)`.
