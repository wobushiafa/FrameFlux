# FrameFlux WPF demo

This Windows-only demo uses the protocol-neutral `FrameFlux.Wpf.MediaView`
control. Playback lifecycle, rendering, audio, volume, and mute are owned by the
control.
Enter an RTSP/RTSPS address or use **Open file** to select and play a local
video. Choose **Media (FFmpeg)** or **WebRTC** in **Player** for an HTTP URL
whose type is unclear. **Auto** recognizes explicit WebRTC schemes and endpoints, and common
media file extensions; it asks for a player when neither applies.

```powershell
dotnet run --project examples/FrameFlux.Demo.Wpf
```
