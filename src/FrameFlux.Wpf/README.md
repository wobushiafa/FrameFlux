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
`NativeSurface` or `CompositedGpu` in `MediaView.OpenOptions`. Both modes
accept lease-based D3D11VA frames without reading them back to the CPU.

`NativeSurface` presents through a DXGI swap chain hosted in a child HWND and
remains the minimum-latency option. WPF elements cannot reliably cover that
child window because of HWND airspace rules.

`CompositedGpu` converts each decoder texture into a shared BGRA texture,
opens it through D3D9Ex, and supplies its surface to WPF `D3DImage`. Additional
children declared inside `MediaView` can therefore render above the video.
Explicit `NativeSurface` playback is rejected when overlay children exist.

Use `MediaRenderPreference.Auto` or `Software` when portable BGRA frame
delivery and snapshot support are required. All three outputs use the same
lease-based `IMediaVideoOutput` contract.

The WPF bitmap, `D3DImage`, and native `HwndHost` outputs own only framework
integration and latest-frame queues. Win32 and D3D11 video processing resources
live in the shared `FrameFlux.Rendering.Windows` package.
