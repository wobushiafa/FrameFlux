# FrameFlux.Avalonia.Windows

This package adds Windows D3D11 presentation to
`FrameFlux.Avalonia.MediaView`. Register it while configuring Avalonia:

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseFrameFluxWindows();
```

`NativeSurface` uses a child HWND and D3D11 swap chain for minimum latency.
`GpuComposition` draws a shared D3D11 texture through Avalonia's compositor
and supports Avalonia overlays. Both paths automatically fall back to the core
software output after repeated presentation failures.

## Direct GPU Drawing

On supported ANGLE/EGL contexts, the default path draws the shared texture
directly from the compositor thread. It avoids Avalonia's per-update snapshot
texture allocation and copy. To use the original GPU snapshot path instead,
set this switch before creating the application:

```csharp
AppContext.SetSwitch("FrameFlux.Windows.DirectGpuDrawing", false);
```

The presentation mode remains `GpuComposition`. Source resolution, D3D11 video
conversion and the default linear sampling are unchanged. Unsupported contexts
or import failures fall back to the existing GPU snapshot output, without using
a native child window.

The frame pump retains at most two pending decoder leases to absorb arrival
jitter. When a backlog is older than 50 ms, it selects the newest pending frame
instead of accumulating latency. It stops polling after a short idle period.
Resource release is serialized with incoming frames; GPU cleanup runs on the
compositor thread.

Local verification covers 30/60 fps playback, a live 1440p HEVC stream, overlays,
pause/resume, resize, stop/restart, resource recreation and snapshot fallback.
Multi-adapter hot switching and physical device removal have not been validated.
Snapshot submission counts and direct draw counts are different pipeline stages
and must not be treated as measured scanout FPS.
