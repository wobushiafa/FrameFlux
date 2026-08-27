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
dotnet build examples/FrameFlux.Demo.Avalonia.Android -c Release `
  -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
```

Desktop demo builds automatically copy the current host RID's FFmpeg files from `native/artifacts/runtimes/{rid}/native` into their output. Override `FrameFluxNativeRuntimeIdentifier` when testing a different RID. Published applications should reference the matching `FrameFlux.FFmpeg.NativeAssets.*` package; the core `FrameFlux.FFmpeg` package contains no native binaries. The Android asset package maps each ABI-specific `.so` into the Android application package.

On Windows, `MediaVideoPresentationMode.NativeSurface` uses FFmpeg D3D11VA
frames directly in a D3D11 video processor and DXGI swap chain. This
minimum-latency path does not read frames back to the CPU. Avalonia and WPF
also support `MediaVideoPresentationMode.GpuComposition` for dedicated
playback. It converts the decoder texture to a shared BGRA texture and imports
it into the framework compositor, allowing framework controls to render above
the video. Linux and Android currently use software frame delivery.

Decoding and rendering are configured independently:

```csharp
Player.OpenOptions = new MediaOpenOptions
{
    Video = new MediaVideoOptions
    {
        DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred
    }
};
Player.PresentationMode = MediaVideoPresentationMode.GpuComposition;
```

`DecodingPolicy` accepts `Automatic`, `SoftwareOnly`,
`HardwarePreferred`, or `HardwareRequired`. `PresentationMode` accepts
`Automatic`, `SoftwareBitmap`, `NativeSurface`, or `GpuComposition`.
Assigning either setting while an Avalonia or WPF player is active performs a
controlled restart. Explicit GPU presentation requires Windows, a dedicated
session, and hardware-capable decoding; unsupported explicit combinations
throw instead of silently changing mode. `Automatic` presentation chooses GPU
composition when available and software otherwise. When
`HardwarePreferred` falls back to software decoding, the adaptive output
switches to `SoftwareBitmap`; inspect `EffectivePresentationMode`,
`IsHardwareVideoDecodingActive`, and `VideoDecoderDiagnostics` for the
active pipeline.

Android targets require API level 24 or later. The current bundled Android
FFmpeg binaries use 4 KB ELF LOAD alignment and must be replaced before
publishing to 16 KB-page-compatible devices or stores that require 16 KB page
support; managed project settings cannot repair the binaries. The Android demo
build and native-assets pack therefore fail by default, and a failed pack does
not emit a `.nupkg`. Set
`FrameFluxAllowUnsupportedAndroidPageAlignment=true` only for local managed-code
validation; it does not make the resulting APK or package suitable for release.

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

Platform audio output uses Windows WASAPI shared mode, Linux ALSA
(`libasound.so.2`), and Android `AudioTrack`. Windows retains `waveOut` only
as an automatic fallback when the default WASAPI device cannot be initialized.
Linux applications therefore need the ALSA runtime installed on the target
system. Configure `Audio.OutputDeviceId` and `Audio.BufferDuration` when the
platform default device or latency is unsuitable, and inspect
`MediaDiagnostics.Audio` for the active backend, queued audio, recovery count,
and latest backend error.

The protocol-neutral player API separates immutable open options from runtime controls:

```csharp
await using IMediaPlayer player = new FfmpegMediaPlayer();
await player.OpenAsync(
    MediaSource.Parse("rtsp://camera/stream"),
    new MediaOpenOptions
    {
        SessionSharing = MediaSessionSharingMode.Shared,
        Network = new MediaNetworkOptions
        {
            Transport = MediaTransport.Tcp,
            LatencyMode = MediaLatencyMode.Low,
            Reconnect = new MediaReconnectOptions
            {
                IsEnabled = true,
                MaximumAttempts = 5
            }
        },
        Video = new MediaVideoOptions
        {
            DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred,
            SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame
        },
        Audio = new MediaAudioOptions
        {
            IsEnabled = true,
            GainDecibels = 6
        }
    });

player.Volume = 0.75;
player.IsMuted = false;
await player.PlayAsync();
```

`SessionSharing` defaults to `Dedicated`, so each player opens its own input.
Set it to `Shared` only when players with the same source and
stream-affecting options should reuse one physical input. Each player keeps
independent events and lifecycle; the input closes after the last player
stops. A shared input has one audio output, so volume and mute are shared and
the latest change applies. Shared playback uses software frame rendering
because native surfaces cannot be fanned out to multiple views.

Snapshot buffering defaults to `Disabled`. Use `KeepLatestFrame` only when
`CaptureSnapshotAsync` is required. GPU presentation keeps its zero-readback
path while snapshots are disabled; when `KeepLatestFrame` is enabled, the
latest decoded frame is copied for snapshot capture before native texture
ownership transfers to the renderer. Applications that create many players can set
`FfmpegMediaPlayerFactoryOptions.MaximumConcurrentOpenOperations`; the
default is 8 and `null` removes the factory-level limit.

`Audio.GainDecibels` applies source gain before runtime volume control. It
defaults to `0 dB` and accepts `-60 dB` through `+24 dB`; positive gain can
raise quiet camera audio and uses saturating conversion to prevent integer
overflow. Keep `IMediaPlayer.Volume` in its standard `0..1` range.

Frames sent to a platform renderer use `IMediaFrameLease` through
`IMediaVideoOutput`. A successful `TryPresent` transfers ownership to the
output; rejected or failed deliveries remain owned by the player. Software and
native framework renderers use this same path. Applications should use
`IMediaPlayer`, `MediaOpenOptions`, and the framework-specific `MediaView`;
protocol and decoder implementation types are not part of the public API.

## Development

Build the full solution and run the deterministic test suite with:

```powershell
dotnet build FrameFlux.slnx -c Release `
  -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
dotnet test tests/FrameFlux.FFmpeg.Tests/FrameFlux.FFmpeg.Tests.csproj -c Release
```

The full-solution build uses the temporary Android alignment override because
the checked-in native binaries are intentionally blocked from release. Omit the
override after replacing all Android `.so` files with 16 KB-aligned builds.

The test suite includes public API drift detection, concurrent player and
shared-session lifecycle coverage, frame-lease ownership checks, and a short
stability loop. Increase that loop for local stress runs without changing the
test source:

```powershell
$env:FRAMEFLUX_STABILITY_ITERATIONS = 10000
dotnet test tests/FrameFlux.FFmpeg.Tests/FrameFlux.FFmpeg.Tests.csproj -c Release
```

These deterministic tests do not replace a real RTSP soak run. Release
validation still needs a representative endpoint and target hardware to cover
network loss/reconnect, audio-device recovery, GPU adapter compatibility,
device loss, and native renderer fallback.

The desktop libraries target `net8.0`, which is consumable from .NET 8, 9, and 10 applications. Android-specific assemblies target the currently supported `net10.0-android`; an extra desktop `net10.0` build is unnecessary.

The direct binding currently supports the ABI used by FFmpeg 6, 7, and 8 (`avcodec` majors 60, 61, and 62). Keep every platform directory to one complete, architecture-matched FFmpeg build.

Repository: https://github.com/wobushiafa/FrameFlux
