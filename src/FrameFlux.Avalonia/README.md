# FrameFlux.Avalonia

This package provides the protocol-neutral Avalonia `MediaView`. It creates an
`IMediaPlayer`, forwards the common playback lifecycle, and consumes software
and native frames through `IMediaVideoOutput`.

`MediaView` is a media surface rather than a content host. It intentionally
does not expose a `Content` property. The Windows native renderer uses a real
child window, so Avalonia controls cannot be reliably composited above it due
to native-window airspace rules. Place application UI beside the media surface
or use software rendering when an Avalonia overlay is required.

The control does not select a playback backend. Applications must reference a
backend package and set `PlayerFactory` before playback starts:

```csharp
Player.PlayerFactory = new FfmpegMediaPlayerFactory();
```

Software rendering supports snapshots and frame subscriptions on every
target. On Windows, dedicated playback can select
`MediaRenderPreference.NativeSurface` for D3D11 output. Linux and Android
currently use software output. Explicit native-surface playback does not
advertise snapshot support because D3D11 textures are not read back to the CPU.

Software bitmap delivery and the Avalonia native host are isolated outputs.
The Win32 window, D3D11 video processor, and swap chain are provided by the
shared `FrameFlux.Rendering.Windows` package.
