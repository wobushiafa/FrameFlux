# FrameFlux Avalonia demo

This cross-platform demo hosts the protocol-neutral `FrameFlux.Avalonia.MediaView`
control directly. The current FFmpeg backend supports RTSP and RTSPS sources.

The UI is defined in `App.axaml` and `Views/MainView.axaml`, then reused by the
desktop and Android hosts.

```powershell
dotnet run --project examples/FrameFlux.Demo.Avalonia.Desktop
dotnet build examples/FrameFlux.Demo.Avalonia.Android -c Release
```

On Windows, the demo requests the native-surface renderer. D3D11VA decoded
textures are presented through D3D11 and DXGI without CPU readback. Android
uses the same public control API without taking a dependency on DirectX.
