# FrameFlux

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platforms](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-blue.svg)]()

**FrameFlux** 是一个专为 .NET 生态（WPF、Avalonia Desktop、Avalonia Android）打造的现代化、高性能、跨平台多媒体播放框架。提供 **FFmpeg** 与 **WebRTC** 双播放后端，针对实时安防监控、低延迟 WebRTC 直播与高清网络/本地视频点播进行了深度的架构解耦与渲染优化。

---

## 核心特性

- **双引擎全格式支持**：
  - **FFmpeg 后端**：支持本地多媒体文件、RTSP/RTSPS 实时监控流、HLS（`.m3u8`）直播切片流，以及 **HTTP/HTTPS 在线音视频点播**（MP4、MKV、WebM、MOV、TS、FLV、MP3、AAC 等常见格式）。
  - **WebRTC 后端**：基于 SIPSorcery 实现，原生支持 WHEP（WebRTC HTTP Egress Protocol）、WHIP、go2rtc WebSocket 信令、`webrtc://` 协议及 SDP/ICE 直接协商。
- **异步预读缓冲（Packet Prefetch Buffer）**：
  - 底层解复用与解码渲染完全解耦，内置 1024 级后台异步网络预取队列与 4MB 套接字大缓存，彻底抵御网络抖动与卡顿。
  - 支持在线点播流的线程安全无缝拖拽（Seek）与时钟自动重基准。
- **全平台硬件加速与零拷贝呈现**：
  - **Windows**：支持 D3D11VA 硬件解码，提供基于 DirectX / DXGI 共享纹理的 `GpuComposition`（允许 UI 控件自由浮动遮罩在视频上方）与高性能 `NativeSurface`。
  - **Linux**：支持 VAAPI 硬件解码，通过 DRM PRIME / DMA-BUF 和 EGLImage 直通 GPU 合成。
  - **Android**：MediaCodec 硬件解码与 `GL_TEXTURE_EXTERNAL_OES` / `SurfaceTexture` 零拷贝渲染。
- **完整的播放控制与音画同步**：
  - 精确 PTS 时间轴同步，支持播放、暂停、毫秒级 Seek、Duration 获取。
  - **0.25x ~ 4.0x 音视频倍速**：内置 FFmpeg `atempo` 变调滤镜，倍速播放时保证音频音调不失真。
  - 运行时音量、静音与源增益（Gain dB）调节。
- **原生多平台音频输出**：
  - Windows：WASAPI 共享模式（自动容灾回退至 `waveOut`）。
  - Linux：ALSA（`libasound.so.2`）。
  - Android：`AudioTrack`。
- **实时诊断与视频快照**：
  - 实时暴露硬件解码状态（`IsHardwareVideoDecodingActive`）、解码器详情、丢帧率与音画同步偏移。
  - 支持随时通过 `CaptureSnapshotAsync` 截取高质量视频当前帧。

---

## 项目架构

| 模块 / 项目 | 程序集 / Nuget 包 | 说明 |
| --- | --- | --- |
| `src/FrameFlux.Abstractions` | `FrameFlux.Abstractions` | 与 UI/底层解耦的核心契约（`IMediaPlayer`、`MediaSource`、配置项、能力和帧租约）。 |
| `src/FrameFlux.FFmpeg` | `FrameFlux.FFmpeg` | FFmpeg 独立媒体播放引擎，负责解复用、音频解码与时钟同步（不含原生二进制）。 |
| `src/FrameFlux.WebRtc` | `FrameFlux.WebRtc` | 纯托管实现的 WebRTC 播放引擎，支持 WHEP、go2rtc 及 SDP 协商。 |
| `src/FrameFlux.FFmpeg.Android` | `FrameFlux.FFmpeg.Android` | 接收 FFmpeg 解复用 H.264/HEVC 数据的 Android MediaCodec 硬件解码器。 |
| `src/FrameFlux.Presentation` | `FrameFlux.Presentation` | UI 控件共享的播放控制与生命周期调度层。 |
| `src/FrameFlux.Rendering.Windows` | `FrameFlux.Rendering.Windows` | Windows 平台专用的 D3D11 与 Win32 视频渲染共享管道。 |
| `src/FrameFlux.Wpf` | `FrameFlux.Wpf` | 面向 **WPF** 的播放器控件 `MediaView`。 |
| `src/FrameFlux.Avalonia` | `FrameFlux.Avalonia` | 面向 **Avalonia** 的跨平台播放器控件 `MediaView`。 |
| `src/FrameFlux.Avalonia.Windows` | `FrameFlux.Avalonia.Windows` | Avalonia 在 Windows 下的 D3D11 合成后端。 |
| `src/FrameFlux.Avalonia.Linux` | `FrameFlux.Avalonia.Linux` | Avalonia 在 Linux 下的 EGL / DMA-BUF 合成后端。 |
| `src/FrameFlux.Avalonia.Android` | `FrameFlux.Avalonia.Android` | Avalonia 在 Android 下的 MediaCodec / OES 零拷贝后端。 |
| `src/FrameFlux.FFmpeg.NativeAssets.*` | 平台 Native 运行库 | 包含 Windows x64、Linux x64、Android 架构编译好的 FFmpeg 动静态库。 |

---

## 快速上手

### 1. WPF 项目集成

在项目文件 `.csproj` 中引用 `FrameFlux.Wpf`：

```xml
<PackageReference Include="FrameFlux.Wpf" Version="0.1.2" />
```

#### XAML 界面定义
```xml
<Window x:Class="MyApp.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ff="clr-namespace:FrameFlux.Wpf;assembly=FrameFlux.Wpf"
        Title="FrameFlux Player" Height="600" Width="900">
    <Grid>
        <!-- 播放器控件 -->
        <ff:MediaView x:Name="Player"
                      AutoPlay="True"
                      PresentationMode="GpuComposition"
                      Stretch="Uniform" />
    </Grid>
</Window>
```

#### 代码逻辑绑定
```csharp
using FrameFlux;
using FrameFlux.FFmpeg;

// 1. 指定播放器内核（FFmpeg 播放器或 WebRtcMediaPlayerFactory）
Player.PlayerFactory = new FfmpegMediaPlayerFactory();

// 2. 配置播放选项（可选）
Player.OpenOptions = new MediaOpenOptions
{
    Network = new MediaNetworkOptions
    {
        LatencyMode = MediaLatencyMode.Default, // 点播建议 Default，RTSP 监控可设为 Low
        Transport = MediaTransport.Tcp
    },
    Video = new MediaVideoOptions
    {
        DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred // 优先使用 GPU 硬解
    }
};

// 3. 打开播放源（支持本地文件、在线 MP4、HLS 或 RTSP）
await Player.OpenAsync(MediaSource.Parse("https://example.com/videos/sample.mp4"));
await Player.PlayAsync();
```

---

### 2. Avalonia 项目集成

在 `Program.cs` 初始化时注册当前操作系统的渲染后端：

```csharp
public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .UseFrameFluxWindows()   // Windows 平台 D3D11 渲染
        .UseFrameFluxLinux();    // Linux 平台 EGL / DMA-BUF 渲染
```

在 AXAML 中使用 `MediaView`：

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ff="clr-namespace:FrameFlux.Avalonia;assembly=FrameFlux.Avalonia">
    <ff:MediaView x:Name="Player"
                  PresentationMode="GpuComposition"
                  Stretch="Uniform" />
</UserControl>
```

---

### 3. 无 UI / 后台纯代码调用 (Core API)

`FrameFlux` 的引擎完全独立于界面，可直接在后台服务中解码与提取帧：

```csharp
await using IMediaPlayer player = new FfmpegMediaPlayer();

// 注册状态与帧到达事件
player.StateChanged += (sender, e) => Console.WriteLine($"State: {e.NewState}");
player.FrameReceived += (sender, frame) =>
{
    Console.WriteLine($"Received {frame.Width}x{frame.Height} format: {frame.PixelFormat}");
};

// 打开并播放
await player.OpenAsync(MediaSource.Parse("rtsp://admin:123456@192.168.1.100:554/stream"));
await player.PlayAsync();

// 运行时控制
await player.PauseAsync();
await player.SeekAsync(TimeSpan.FromMinutes(2)); // 支持点播源跳转
player.PlaybackRate = 1.5;                       // 1.5x 倍速
player.Volume = 0.8;                             // 80% 音量
```

---

## 媒体源与模式参考

### 媒体源类型与协议支持

| 源类型 | 协议示例 | 推荐后端 | 具备能力 |
| --- | --- | --- | --- |
| **在线音视频点播** | `http://.../movie.mp4`<br/>`https://.../video.mkv` | `FfmpegMediaPlayer` | 异步预读缓冲、Seek、Pause、Duration、0.25x~4x 倍速 |
| **本地文件** | `C:\Videos\demo.mp4`<br/>`file:///home/user/test.mkv` | `FfmpegMediaPlayer` | 本地高速 I/O、Seek、Pause、Duration、0.25x~4x 倍速 |
| **HLS 直播流** | `http://.../live.m3u8` | `FfmpegMediaPlayer` | 异步切片预取、直播时钟同步、低延迟缓冲 |
| **RTSP 监控流** | `rtsp://camera.local:554/stream` | `FfmpegMediaPlayer` | TCP/UDP 传输、极低延迟低丢帧策略（`LowLatency`） |
| **WebRTC 实时流** | `http://.../whep`<br/>`ws://.../api/ws`<br/>`webrtc://...` | `WebRtcMediaPlayer` | WHEP/WHIP 协商、超低延迟毫秒级互动直播 |

---

### 呈现模式与解码策略

#### 呈现模式（`MediaVideoPresentationMode`）
- **`GpuComposition`（推荐）**：
  将显卡解码后的 GPU 纹理直接共享给 WPF / Avalonia 的 UI 合成引擎。
  - **优势**：GPU 内部处理，零 CPU 拷贝，**完美支持在其上方叠加 WPF / Avalonia 按钮、控制条等原生 XAML 浮层**。
- **`NativeSurface`**：
  使用独立的平台原生交换链（DirectX SwapChain / X11 Visual / Android SurfaceView）直接向屏幕投屏。
  - **优势**：极致渲染帧率；注意在 Android 等系统上可能覆盖上层 UI 控件。
- **`SoftwareBitmap`**：
  回读为内存 BGRA 字节并由软件位图渲染。
  - **优势**：兼容所有老旧显卡与无 GPU 加速的环境。

#### 解码策略（`MediaVideoDecodingPolicy`）
- `Automatic`：优先尝试硬件解码，失败时无缝自动回退到软件解码。
- `HardwarePreferred`：倾向硬件解码；若硬件初始化失败则回退至软件解码。
- `HardwareRequired`：必须使用 GPU 硬件解码；若硬件不支持则抛出异常。
- `SoftwareOnly`：强制仅使用 CPU 软解（如排查驱动兼容性问题）。

---

## 示例程序运行

仓库内置了全功能演示 Demo，涵盖源解析、模式切换、倍速/音量控制与底层诊断状态展示：

```powershell
# 运行 WPF 示例 (Windows)
dotnet run --project examples/FrameFlux.Demo.Wpf

# 运行 Avalonia 跨平台桌面示例 (Windows / Linux)
dotnet run --project examples/FrameFlux.Demo.Avalonia.Desktop

# 构建 Android 示例
dotnet build examples/FrameFlux.Demo.Avalonia.Android -c Release -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
```

---

## 构建与测试

编译整个解决方案：
```powershell
dotnet build FrameFlux.slnx -c Release -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
```

运行全部自动化单元测试（包含会话生命周期、时钟同步、预读机制、协议边界校验）：
```powershell
dotnet test tests/FrameFlux.FFmpeg.Tests/FrameFlux.FFmpeg.Tests.csproj -c Release
```

---

## 许可证

本项目基于 [MIT License](LICENSE) 协议开源。包含的第三方依赖及 FFmpeg 组件请参阅 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
