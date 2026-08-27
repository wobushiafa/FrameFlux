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
            GainDecibels = 6
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
`MediaSnapshotPolicy.KeepLatestFrame`. Network timeouts are nullable;
`null` leaves the corresponding backend timeout disabled. Reconnect attempts
and exponential backoff are configured under `Network.Reconnect`.
`Audio.GainDecibels` is a source-level gain applied before the runtime
`Volume` control. It defaults to `0 dB` and accepts `-60 dB` through
`+24 dB`.

Limit simultaneous FFmpeg open operations per factory when many players start
together:

```csharp
var factory = new FfmpegMediaPlayerFactory(
    new FfmpegMediaPlayerFactoryOptions
    {
        MaximumConcurrentOpenOperations = 4
    });
```

The default limit is 8. Set it to `null` for no factory-level limit.

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
`swresample` components, and calls their exported functions directly. No
FrameFlux adapter library is required. A process can use only one configured
FFmpeg directory.

Navigation visibility policy and full-screen ownership remain application concerns.
