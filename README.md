# FrameFlux

FrameFlux 是一个跨平台媒体播放库，提供 FFmpeg 和 WebRTC 两套播放后端，支持 RTSP、WHEP/go2rtc WebRTC 直播和本地文件播放。硬件解码使用与目标 FFmpeg 公共头文件匹配的版本化 ABI 布局。

当前能力包括音视频软硬件解码、平台音频输出、直播音画同步、本地文件时钟、Seek、Duration、0.25x 至 4x 音视频倍速，以及运行时音量和静音控制。音频倍速通过 FFmpeg `atempo` 保持音调，内部统一为 48 kHz、双声道、16 位有符号 PCM。

## 项目结构

| 项目 | 包 | 用途 |
| --- | --- | --- |
| `src/FrameFlux.Abstractions` | `FrameFlux.Abstractions` | 与协议无关的播放器、媒体源、帧、能力和视频输出契约。 |
| `src/FrameFlux.FFmpeg` | `FrameFlux.FFmpeg` | 独立于 UI 的 FFmpeg 播放器，支持 RTSP 和本地文件，不附带原生二进制。 |
| `src/FrameFlux.WebRtc` | `FrameFlux.WebRtc` | 基于 SIPSorcery 的 WebRTC 播放器，支持 WHEP、go2rtc、WebRTC URI 和 SDP/ICE 输入。 |
| `src/FrameFlux.FFmpeg.Android` | `FrameFlux.FFmpeg.Android` | 接收 FFmpeg 解复用 H.264/HEVC 数据的 Android MediaCodec 硬件解码器。 |
| `src/FrameFlux.Presentation` | `FrameFlux.Presentation` | UI 控件共享的、与渲染后端无关的播放生命周期。 |
| `src/FrameFlux.Rendering.Windows` | `FrameFlux.Rendering.Windows` | Windows UI 控件共享的 Win32 和 D3D11 视频呈现。 |
| `src/FrameFlux.Avalonia` | `FrameFlux.Avalonia` | Avalonia `MediaView` 和平台渲染输出。 |
| `src/FrameFlux.Avalonia.Android` | `FrameFlux.Avalonia.Android` | Avalonia Android SurfaceTexture/OES 零拷贝呈现后端。 |
| `src/FrameFlux.Avalonia.Linux` | `FrameFlux.Avalonia.Linux` | Avalonia Linux EGL、DMA-BUF 和 NativeSurface 呈现后端。 |
| `src/FrameFlux.Avalonia.Windows` | `FrameFlux.Avalonia.Windows` | Avalonia Windows D3D11 呈现后端。 |
| `src/FrameFlux.Wpf` | `FrameFlux.Wpf` | 可复用的 WPF 播放控件和渲染器。 |
| `src/FrameFlux.FFmpeg.NativeAssets.*` | 对应平台原生资源包 | Windows x64、Linux x64 和 Android ABI 对应的 FFmpeg 运行库。 |

## 示例程序

| 示例 | 框架 | 播放集成 |
| --- | --- | --- |
| `examples/FrameFlux.Demo.Wpf` | WPF（Windows） | 自动选择 FFmpeg/WebRTC，支持软硬解码和 `SoftwareBitmap`、`GpuComposition`、`NativeSurface` 三种呈现模式。 |
| `examples/FrameFlux.Demo.Avalonia.Desktop` | Avalonia Desktop | 自动选择 FFmpeg/WebRTC；Windows 支持三种呈现模式，Linux 使用已注册的 EGL 后端。 |
| `examples/FrameFlux.Demo.Avalonia.Android` | Avalonia Android | 在标准 Android Activity 中承载共享 AXAML 界面。 |

```powershell
dotnet run --project examples/FrameFlux.Demo.Wpf
dotnet run --project examples/FrameFlux.Demo.Avalonia.Desktop
dotnet build examples/FrameFlux.Demo.Avalonia.Android -c Release `
  -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
```

桌面示例构建时，会从 `native/artifacts/runtimes/{rid}/native` 自动复制当前宿主 RID 的 FFmpeg 文件。测试其他 RID 时可设置 `FrameFluxNativeRuntimeIdentifier`。正式发布的应用应引用匹配的 `FrameFlux.FFmpeg.NativeAssets.*` 包；`FrameFlux.FFmpeg` 核心包不包含原生二进制。

## Avalonia 平台注册

Avalonia 桌面应用只需引用并注册实际发布的平台包。跨平台桌面应用可以同时注册 Windows 和 Linux 后端，运行时只会创建当前操作系统对应的实现：

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseFrameFluxWindows()
    .UseFrameFluxLinux();
```

Windows 的 `NativeSurface` 可以使用 FFmpeg 或 WebRTC 的 D3D11VA 解码纹理、D3D11 视频处理器和 DXGI 交换链，不会把视频帧读回 CPU。`GpuComposition` 会把解码纹理转换为共享 BGRA 纹理并导入框架合成器，因此允许 Avalonia 或 WPF 控件覆盖在视频上方。

Linux 在可用时使用 VAAPI 硬件解码，将 DRM PRIME DMA-BUF 通过 EGLImage 导入 GPU：

- `GpuComposition` 位于 Avalonia 合成树中，支持控件覆盖，并保持 VAAPI 到 DMA-BUF、EGLImage 的零拷贝路径。
- `NativeSurface` 在 X11/XWayland 下使用真实子 XID、独立 EGL 窗口和无 alpha 的 X11 Visual。
- 纯 Wayland 当前使用 `GpuComposition`。Avalonia 12 尚未通过 `NativeControlHost` 提供 `wl_subsurface` 宿主。
- 零拷贝互操作不可用时，自动模式可以回退到软件输出；显式硬件模式会报告错误。

Android 应用注册 Android 后端：

```csharp
protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
    base.CustomizeAppBuilder(builder).UseFrameFluxAndroid();
```

注册同时启用 `FrameFlux.FFmpeg.Android`。FFmpeg 负责 RTSP、音频解码以及 H.264/HEVC 解复用，视频访问单元交给 MediaCodec，并通过 `GL_TEXTURE_EXTERNAL_OES` 渲染到 SurfaceTexture，全程不读回 CPU。零拷贝 Surface 没有可长期保留的 CPU 帧，因此快照使用软件路径。

Android 的 `NativeSurface` 基于系统 `SurfaceView`，位于 Avalonia 合成树之外。因此 Popup、ComboBox 下拉层、菜单、Tooltip 和 Flyout 可能显示在视频下方。界面需要覆盖视频时应选择 `GpuComposition`；`NativeSurface` 适用于视频始终处于原生层、且上方没有 Avalonia 浮层的场景。

## 解码与呈现

解码策略和呈现模式可以独立配置：

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

`DecodingPolicy` 支持 `Automatic`、`SoftwareOnly`、`HardwarePreferred` 和 `HardwareRequired`。`PresentationMode` 支持 `Automatic`、`SoftwareBitmap`、`NativeSurface` 和 `GpuComposition`。

播放器运行期间修改任一设置会执行受控重启。显式 GPU 呈现要求已注册的平台 GPU 后端、独占会话和可用的硬件解码；不支持的显式组合会抛出错误，而不会静默切换模式。`Automatic` 在可用时选择 GPU 合成，否则使用软件输出。`HardwarePreferred` 回退到软件解码后，自适应输出也会切换为 `SoftwareBitmap`。

可通过以下属性检查实际管线：

- `EffectivePresentationMode`
- `IsHardwareVideoDecodingActive`
- `VideoDecoderDiagnostics`

## Android 页面大小

Android 目标要求 API 24 或更高。当前仓库中的 Android FFmpeg 二进制使用 4 KB ELF LOAD 对齐，在发布到要求 16 KB 页面的设备或应用商店前必须替换；托管项目设置无法修复原生二进制。

因此 Android 示例构建和原生资源打包默认会失败，失败时不会产生 `.nupkg`。`FrameFluxAllowUnsupportedAndroidPageAlignment=true` 仅用于本地托管代码验证，不会让 APK 或包满足正式发布要求。

## FFmpeg 运行库

应用也可以在创建播放器前配置自己的 FFmpeg 目录：

```csharp
FFmpegHelper.RegisterFFmpeg(@"C:\ffmpeg\bin");
```

目录必须包含一套完整、架构匹配且来自同一 FFmpeg 版本的运行库。播放需要 `avcodec`、`avformat`、`avutil`、`avfilter`、`swscale` 和 `swresample`。Windows D3D11VA 与 Linux VAAPI 直接使用这些库。Android 使用相同的 FFmpeg 导出完成解复用、音频解码和音频倍速，再将编码视频送入系统 MediaCodec。

UI 包不依赖具体 FFmpeg 后端，应用负责注入播放器工厂：

```csharp
Player.PlayerFactory = new FfmpegMediaPlayerFactory();

// WHEP、go2rtc 或其他 WebRTC 输入
Player.PlayerFactory = new WebRtcMediaPlayerFactory();
```

Avalonia 和 WPF 使用同一套呈现契约。Windows 上的 WebRTC 软件解码输出到 `SoftwareBitmap`；D3D11VA 硬件解码既可回传软件位图，也可将 D3D11 纹理直接交给 `GpuComposition` 或 `NativeSurface`。`IsHardwareVideoDecodingActive` 和 `VideoDecoderDiagnostics` 会在首帧建立实际解码管线后刷新。

WPF 可以直接使用打包的控件：

```xml
<frameFlux:MediaView
    Source="{Binding Source}"
    AutoPlay="True"
    Volume="{Binding Volume}"
    IsMuted="{Binding IsMuted}"
    Stretch="Uniform" />
```

`MediaView.Source` 使用与协议无关的 `MediaSource` 契约。当前后端支持 RTSP、RTSPS 和本地文件路径。本地文件会提供 Duration、Seek 和 0.25x 至 4x 的音视频倍速；直播源保持实时播放，不开放 Seek 和倍速能力。

## 音频输出

平台音频输出分别使用：

- Windows：WASAPI 共享模式；默认设备无法初始化时自动回退到 `waveOut`。
- Linux：ALSA（`libasound.so.2`）。
- Android：`AudioTrack`。

可通过 `Audio.OutputDeviceId` 和 `Audio.BufferDuration` 选择设备及缓冲时长，并通过 `MediaDiagnostics.Audio` 查看当前后端、排队音频、恢复次数和最近错误。

## 播放器 API

与协议无关的播放器 API 将不可变的打开选项和运行时控制分开：

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

`SessionSharing` 默认为 `Dedicated`，每个播放器分别打开输入。只有相同媒体源和流相关选项需要复用同一个物理输入时才应使用 `Shared`。各播放器仍保有独立事件和生命周期，最后一个播放器停止后输入才会关闭。共享输入只有一个音频输出，因此音量和静音状态共享，并以最后一次修改为准。原生 Surface 无法分发给多个视图，所以共享播放使用软件帧渲染。

快照缓冲默认关闭。只有需要 `CaptureSnapshotAsync` 时才应使用 `KeepLatestFrame`。关闭快照时 GPU 呈现保持零读回；启用后，在原生纹理所有权转移给渲染器之前会复制最新解码帧。

创建大量播放器时，可设置 `FfmpegMediaPlayerFactoryOptions.MaximumConcurrentOpenOperations`；默认值为 8，设置为 `null` 可移除工厂级并发限制。

`Audio.GainDecibels` 在运行时音量控制前应用源增益，默认 `0 dB`，范围为 `-60 dB` 到 `+24 dB`。正增益使用饱和转换避免整数溢出。`IMediaPlayer.Volume` 始终使用标准的 `0..1` 范围。

平台渲染器通过 `IMediaVideoOutput` 接收 `IMediaFrameLease`。`TryPresent` 成功后帧所有权转移给输出；拒绝或失败的提交仍由播放器持有。软件渲染器和原生框架渲染器遵循同一所有权规则。

## 开发与验证

构建完整解决方案并运行确定性测试：

```powershell
dotnet build FrameFlux.slnx -c Release `
  -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
dotnet test tests/FrameFlux.FFmpeg.Tests/FrameFlux.FFmpeg.Tests.csproj -c Release
```

完整解决方案构建临时启用 Android 对齐覆盖，是因为仓库内的原生二进制被有意阻止用于发布。替换为 16 KB 对齐的 Android `.so` 后应移除该参数。

测试覆盖公共 API 漂移、并发播放器、共享会话生命周期、帧租约所有权和短时稳定性循环。本地压力测试可以增加循环次数而不修改测试源码：

```powershell
$env:FRAMEFLUX_STABILITY_ITERATIONS = 10000
dotnet test tests/FrameFlux.FFmpeg.Tests/FrameFlux.FFmpeg.Tests.csproj -c Release
```

确定性测试不能代替真实 RTSP 长时间运行验证。发布前仍需在目标硬件上覆盖断网重连、音频设备恢复、GPU 适配器兼容性、设备丢失和原生渲染器回退。

桌面库目标框架为 `net8.0`，可由 .NET 8、9 和 10 应用引用。Android 程序集目标框架为当前支持的 `net10.0-android`，无需额外生成桌面 `net10.0` 程序集。

当前直接绑定支持 FFmpeg 6、7 和 8 的 ABI，即 `avcodec` 主版本 60、61 和 62。每个平台目录必须保持一套完整且架构匹配的 FFmpeg 构建。

首次发布的准备步骤、包白名单和阻断条件见 [发布指南](docs/RELEASING.md)。第三方组件及原生二进制的待确认事项见 [第三方声明](THIRD-PARTY-NOTICES.md)。

## 许可证

FrameFlux 源码和托管包使用 [MIT License](LICENSE)。FFmpeg 及其他第三方组件适用各自的许可证；发布原生资源包前必须同时满足 [第三方声明](THIRD-PARTY-NOTICES.md) 中列出的义务。

仓库地址：https://github.com/wobushiafa/FrameFlux
