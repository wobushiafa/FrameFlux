# FrameFlux Avalonia demo

This cross-platform demo hosts the protocol-neutral `FrameFlux.Avalonia.MediaView`
control directly. The current FFmpeg backend supports RTSP and RTSPS sources.

The UI is defined in `App.axaml` and `Views/MainView.axaml`, then reused by the
desktop and Android hosts.

```powershell
dotnet run --project examples/FrameFlux.Demo.Avalonia.Desktop
dotnet build examples/FrameFlux.Demo.Avalonia.Android -c Release
```

On Windows, the desktop host registers `FrameFlux.Avalonia.Windows` and the
demo requests the native-surface renderer. D3D11VA decoded textures are
presented through D3D11 and DXGI without CPU readback. On Linux, the host
registers `FrameFlux.Avalonia.Linux`, uses VAAPI when available, and renders
through an Avalonia OpenGL texture. Android uses the same public control API
while registering `FrameFlux.Avalonia.Android`; FFmpeg-demuxed H.264/HEVC is
decoded by MediaCodec and presented through a SurfaceTexture external OES
texture without CPU readback or a desktop graphics dependency.
