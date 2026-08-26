# FrameFlux.Wpf

Reusable WPF controls for FrameFlux media playback.

```xml
<frameFlux:MediaView
    Source="{Binding Source}"
    AutoPlay="True"
    Volume="{Binding Volume}"
    IsMuted="{Binding IsMuted}"
    Stretch="Uniform" />
```

The current FFmpeg backend supports RTSP and RTSPS sources. The control uses the
protocol-neutral `MediaSource` contract so local files and additional media
backends can be added without changing the WPF control API.

For end-to-end Windows hardware acceleration, use D3D11MediaView. It keeps
FFmpeg D3D11VA frames on the GPU and presents them through a DXGI swap chain:

```xml
<frameFlux:D3D11MediaView
    Source="{Binding Source}"
    AutoPlay="True"
    Volume="{Binding Volume}"
    IsMuted="{Binding IsMuted}"
    Stretch="Uniform" />
```

D3D11MediaView currently supports dedicated RTSP/RTSPS playback on Windows
with FFmpeg 7 x64. It fails explicitly when D3D11VA is unavailable instead of
silently falling back to CPU rendering. Use MediaView when portable BGRA
frame delivery or software fallback is required.
