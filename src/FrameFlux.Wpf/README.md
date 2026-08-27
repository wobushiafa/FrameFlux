# FrameFlux.Wpf

Reusable WPF controls for FrameFlux media playback.

The control is backend-neutral. Reference a backend package and inject its
factory during application setup:

```csharp
Player.PlayerFactory = new FfmpegMediaPlayerFactory();
```

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

For end-to-end Windows hardware acceleration, set `RenderPreference` to
`NativeSurface` in `MediaView.OpenOptions`. The same control accepts
lease-based D3D11VA frames and presents them through a DXGI swap chain.

Native-surface output supports dedicated RTSP/RTSPS playback on Windows. Use
`MediaRenderPreference.Auto` or `Software` when portable BGRA frame delivery
and snapshot support are required. Both software and native presentation use
the same lease-based `IMediaVideoOutput` contract.

The WPF bitmap output and native `HwndHost` own only framework integration and
latest-frame queues. Win32, D3D11 video processing, and swap-chain resources
live in the shared `FrameFlux.Rendering.Windows` package.
