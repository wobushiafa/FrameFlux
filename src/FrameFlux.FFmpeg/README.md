# FrameFlux.FFmpeg

`FrameFlux.FFmpeg` contains the UI-independent `FfmpegMediaPlayer` and the
current RTSP backend. The player exposes protocol-neutral sources, options,
states, capabilities, frames, snapshots, and diagnostics.

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
                InitialDelay = TimeSpan.FromSeconds(1),
                MaximumDelay = TimeSpan.FromSeconds(30),
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
            GainDecibels = 6,
            OutputDeviceId = null,
            BufferDuration = TimeSpan.FromMilliseconds(100)
        }
    });
await player.PlayAsync();
```

Stream sharing is opt-in. `Dedicated` keeps one physical input per player;
`Shared` reuses an input only when the source and stream-affecting options
match. Logical players retain separate events and stop independently. The
shared input and its audio output are released after the last lease stops.
Shared sessions use CPU frame delivery so one decoded frame can be fanned out
to multiple outputs.

`MediaVideoDecodingPolicy.Automatic` uses the platform default.
`SoftwareOnly` forbids hardware decoding, `HardwarePreferred` permits a
runtime software fallback, and `HardwareRequired` fails when the hardware
path cannot be established. Presentation is deliberately not part of
`MediaOpenOptions`; Avalonia and WPF select it with
`MediaView.PresentationMode`.

Snapshot buffering is opt-in through
`MediaSnapshotPolicy.KeepLatestFrame`. GPU presentation stays zero-readback
when snapshots are disabled. When `KeepLatestFrame` is selected, the decoder
creates a separate BGRA snapshot copy while the original D3D11 texture remains
on the GPU presentation path. Network timeouts are nullable;
`null` leaves the corresponding backend timeout disabled. Reconnect attempts
and exponential backoff are configured under `Network.Reconnect`.
`Audio.GainDecibels` is a source-level gain applied before the runtime
`Volume` control. It defaults to `0 dB` and accepts `-60 dB` through
`+24 dB`. `Audio.OutputDeviceId` selects a platform output endpoint;
`null` follows the operating-system default. `Audio.BufferDuration` controls
the requested output latency and accepts 10 milliseconds through 2 seconds.
Windows uses shared-mode WASAPI by default and falls back to `waveOut` only
when WASAPI initialization fails. Linux maps the device ID to an ALSA PCM name.
The effective backend, selected endpoint, queued duration, recovery count, and
last backend error are available from `player.Diagnostics.Audio`.

Audio is the playback master clock when an audio stream is present. Video
frames that are more than 100 milliseconds late are dropped; early frames are
delayed by at most 500 milliseconds. Audio and video timestamp discontinuities
rebase the corresponding clock instead of leaving playback permanently stalled
or dropping every subsequent frame. Current positions, A/V offset, delayed and
dropped frame counts, and clock reset count are available from
`player.Diagnostics.Synchronization`. Reconnects create a fresh synchronizer.

Limit simultaneous endpoint probes and FFmpeg open operations when many players
start together. Factories configured with the same limit share one process-wide
limiter, but applications should still reuse one factory for consistent player
configuration:

```csharp
var factory = new FfmpegMediaPlayerFactory(
    new FfmpegMediaPlayerFactoryOptions
    {
        MaximumConcurrentOpenOperations = 4
    });
```

The default is selected from the logical processor count: two concurrent opens
on systems with up to 8 processors, three with 9 to 12, and four with 13 or
more. Set an explicit value to override it, or `null` to disable limiting.

Platform-specific native binaries are distributed separately in
`FrameFlux.FFmpeg.NativeAssets.Windows`, `FrameFlux.FFmpeg.NativeAssets.Linux`,
and `FrameFlux.FFmpeg.NativeAssets.Android`. The core package contains no
native libraries. Applications may instead provide their own platform FFmpeg
shared-library directory before creating a player:

```csharp
FFmpegHelper.RegisterFFmpeg(@"C:\ffmpeg\bin");
```

The loader accepts versioned Windows, Linux, macOS, and Android library names,
validates the required `avutil`, `avcodec`, `avformat`, `swscale`, and
`swresample` components, and calls their exported functions directly. Windows
D3D11VA and Linux VAAPI share the same managed hardware-decoder initialization
path. The small required structure layout is versioned in the managed ABI layer
and generated from the matching FFmpeg 7/8 public headers. No FrameFlux native
adapter library is loaded. A process can use only one configured FFmpeg directory.

On Linux, `Automatic` and `HardwarePreferred` request VAAPI and fall back to
software when the codec, render node, or driver is unavailable.
`HardwareRequired` reports initialization failure instead. The initial VAAPI
path exports DRM PRIME frames to outputs that prefer
`MediaFrameStorageKind.DmaBuf`, avoiding hardware-frame readback. CPU outputs
continue to transfer frames to system memory; inspect
`MediaDiagnostics.IsHardwareVideoDecodingActive` and
`MediaDiagnostics.VideoDecoderDiagnostics` for the effective path.

On Android, reference `FrameFlux.FFmpeg.Android` and register
`FrameFluxAndroidMediaCodec` (the Avalonia Android extension does this
automatically). The core FFmpeg binding continues to open RTSP, demux packets,
and decode audio directly from the supplied `.so` exports. H.264 and HEVC
access units are normalized to Annex-B and queued into the public Android
MediaCodec API. Decoded output is released to an application-provided
`IAndroidVideoSurfaceOutput`; no native FrameFlux bridge or FFmpeg private
MediaCodec ABI is used.

Android Surface decoding requires a dedicated session and snapshots disabled.
`HardwarePreferred` falls back to the normal software decoder when the codec,
Surface, or GL context is unavailable. `HardwareRequired` reports the failure.

Navigation visibility policy and full-screen ownership remain application concerns.
