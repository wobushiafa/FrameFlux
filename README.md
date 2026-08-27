# FrameFlux

FrameFlux is a cross-platform media playback library built on FFmpeg. Its current implementation provides RTSP playback, while the package boundaries are ready for local files and additional media sources. The managed packages directly bind the required FFmpeg exports and do not depend on `FFmpeg.AutoGen` or a FrameFlux native adapter.

Current playback capabilities include video and audio decoding, platform audio output, audio-master A/V synchronization, and live volume/mute control. Audio is normalized internally to 48 kHz stereo signed 16-bit PCM.

| Project | Package | Purpose |
| --- | --- | --- |
| `src/FrameFlux.Abstractions` | `FrameFlux.Abstractions` | Protocol-neutral player, source, frame, capability, and video-output contracts. |
| `src/FrameFlux.FFmpeg` | `FrameFlux.FFmpeg` | UI-independent FFmpeg media player and RTSP backend, without bundled native binaries. |
| `src/FrameFlux.Presentation` | `FrameFlux.Presentation` | Shared backend-neutral playback lifecycle used by UI controls. |
| `src/FrameFlux.Rendering.Windows` | `FrameFlux.Rendering.Windows` | Shared Win32 and D3D11 video presentation used by Windows UI controls. |
| `src/FrameFlux.Avalonia` | `FrameFlux.Avalonia` | Avalonia `MediaView` and platform rendering outputs. |
| `src/FrameFlux.Wpf` | `FrameFlux.Wpf` | Reusable WPF media playback control and renderer. |
| `src/FrameFlux.FFmpeg.NativeAssets.Windows` | `FrameFlux.FFmpeg.NativeAssets.Windows` | Windows x64 FFmpeg runtime assets. |
| `src/FrameFlux.FFmpeg.NativeAssets.Linux` | `FrameFlux.FFmpeg.NativeAssets.Linux` | Linux x64 FFmpeg runtime assets. |
| `src/FrameFlux.FFmpeg.NativeAssets.Android` | `FrameFlux.FFmpeg.NativeAssets.Android` | Android FFmpeg runtime assets for the supported ABIs. |

## Demos

| Demo | Framework | Playback integration |
| --- | --- | --- |
| `examples/FrameFlux.Demo.Wpf` | WPF (Windows) | Uses the reusable `MediaView` control with bindable volume and mute. |
| `examples/FrameFlux.Demo.Avalonia.Desktop` | Avalonia Desktop | Hosts the shared AXAML UI and uses zero-copy D3D11VA/D3D11 presentation on Windows. |
| `examples/FrameFlux.Demo.Avalonia.Android` | Avalonia Android | Hosts the same AXAML UI in a standard Android activity. |

```powershell
dotnet run --project examples/FrameFlux.Demo.Wpf
dotnet run --project examples/FrameFlux.Demo.Avalonia.Desktop
dotnet build examples/FrameFlux.Demo.Avalonia.Android -c Release
```

Desktop demo builds automatically copy the current host RID's FFmpeg files from `native/artifacts/runtimes/{rid}/native` into their output. Override `FrameFluxNativeRuntimeIdentifier` when testing a different RID. Published applications should reference the matching `FrameFlux.FFmpeg.NativeAssets.*` package; the core `FrameFlux.FFmpeg` package contains no native binaries. The Android asset package maps each ABI-specific `.so` into the Android application package.

On Windows, `MediaRenderPreference.NativeSurface` with hardware acceleration
enabled uses FFmpeg D3D11VA frames directly in a D3D11 video processor and DXGI
swap chain. This minimum-latency path does not read frames back to the CPU.
Avalonia and WPF also support `MediaRenderPreference.CompositedGpu` for
dedicated playback. It converts the decoder texture to a shared BGRA texture
and imports it into the framework compositor, allowing framework controls to
render above the video. Linux and Android currently use software frame
delivery.

The current Android FFmpeg binaries are not aligned for Android 16's required
16 KB memory page size. They work for current test targets, but must be rebuilt
with 16 KB page-size support before publishing an Android 16-compatible
package; this cannot be corrected by managed project settings.

Applications can instead configure their own FFmpeg directory before creating a player:

```csharp
FFmpegHelper.RegisterFFmpeg(@"C:\ffmpeg\bin");
```

UI packages do not depend on the FFmpeg backend. Applications reference the
backend they want and inject its factory:

```csharp
Player.PlayerFactory = new FfmpegMediaPlayerFactory();
```

WPF applications can then use the packaged control:

```xml
<frameFlux:MediaView
    Source="{Binding Source}"
    AutoPlay="True"
    Volume="{Binding Volume}"
    IsMuted="{Binding IsMuted}"
    Stretch="Uniform" />
```

`MediaView.Source` uses the protocol-neutral `MediaSource` contract. The current backend accepts RTSP and RTSPS; local file support can be added without changing the WPF or Avalonia control APIs.

The configured directory must contain one complete, architecture-matched FFmpeg build. Audio playback requires `avcodec`, `avformat`, `avutil`, `swscale`, and `swresample` from the same FFmpeg release. FrameFlux does not require `FFmpeg.AutoGen` or `frameflux_ffmpeg.dll`.

Platform audio output uses Windows `waveOut`, Linux ALSA (`libasound.so.2`), and Android `AudioTrack`. Linux applications therefore need the ALSA runtime installed on the target system.

The protocol-neutral player API separates immutable open options from runtime controls:

```csharp
await using IMediaPlayer player = new FfmpegMediaPlayer();
await player.OpenAsync(
    MediaSource.Parse("rtsp://camera/stream"),
    new MediaOpenOptions
    {
        EnableAudio = true,
        LowLatency = true,
        Transport = MediaTransport.Tcp,
        StreamSharing = MediaStreamSharingMode.Shared
    });

player.Volume = 0.75;
player.IsMuted = false;
await player.PlayAsync();
```

`StreamSharing` defaults to `Dedicated`, so each player opens its own input.
Set it to `Shared` only when players with the same source and
stream-affecting options should reuse one physical input. Each player keeps
independent events and lifecycle; the input closes after the last player
stops. A shared input has one audio output, so volume and mute are shared and
the latest change applies. Shared playback uses software frame rendering
because native surfaces cannot be fanned out to multiple views.

Frames sent to a platform renderer use `IMediaFrameLease` through
`IMediaVideoOutput`. A successful `TryPresent` transfers ownership to the
output; rejected or failed deliveries remain owned by the player. Software and
native framework renderers use this same path. Applications should use
`IMediaPlayer`, `MediaOpenOptions`, and the framework-specific `MediaView`;
protocol and decoder implementation types are not part of the public API.

## Development

Build the full solution with `dotnet build FrameFlux.slnx` and run the tests with `dotnet test FrameFlux.slnx`.

The desktop libraries target `net8.0`, which is consumable from .NET 8, 9, and 10 applications. Android-specific assemblies target the currently supported `net10.0-android`; an extra desktop `net10.0` build is unnecessary.

The direct binding currently supports the ABI used by FFmpeg 6, 7, and 8 (`avcodec` majors 60, 61, and 62). Keep every platform directory to one complete, architecture-matched FFmpeg build.

Repository: https://github.com/wobushiafa/FrameFlux
