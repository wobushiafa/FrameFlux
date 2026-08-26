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
        EnableAudio = true,
        LowLatency = true,
        StreamSharing = MediaStreamSharingMode.Shared
    });
await player.PlayAsync();
```

`IRtspSession` remains available as a compatibility API.

Stream sharing is opt-in. `Dedicated` keeps one physical input per player;
`Shared` reuses an input only when the source and stream-affecting options
match. Logical players retain separate events and stop independently. The
shared input and its audio output are released after the last lease stops.

Applications may provide their own platform FFmpeg shared-library directory before creating any player or session:

```csharp
FFmpegHelper.RegisterFFmpeg(@"C:\ffmpeg\bin");
```

The loader accepts versioned Windows, Linux, macOS, and Android library names,
validates the required `avutil`, `avcodec`, `avformat`, `swscale`, and
`swresample` components, and calls their exported functions directly. No
FrameFlux adapter library is required. A process can use only one configured
FFmpeg directory.

Navigation visibility policy and full-screen ownership remain application concerns.
