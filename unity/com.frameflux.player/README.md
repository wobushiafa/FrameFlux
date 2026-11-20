# FrameFlux for Unity 2022.3 LTS — Windows x64

这是自包含 UPM 包，提供通用 `FrameFlux.Unity.MediaView`，复用
FrameFlux.Abstractions、Presentation、FFmpeg 和 WebRtc 的 netstandard2.1 程序集。
RawImage、场景材质与播放控制分离；FFmpeg/WebRTC 音频进入 Unity AudioSource。
版本 0.1.0 为预览版；Editor/Mono Player 的基础场景验收已通过，IL2CPP Player
尚缺构建模块，详见下方验证状态。

## 构建与安装

在 FrameFlux 仓库根目录执行，需要 .NET 8 或更新的 SDK：

```powershell
powershell -NoProfile -File scripts/build-unity-package.ps1
```

在 Unity Package Manager 中选择 **Add package from disk**，打开
`artifacts/unity-managed/com.frameflux.player/package.json`。包包含托管程序集、
Windows x64 FFmpeg 插件、依赖、稳定 .meta、asmdef、link.xml、Editor linker
保留规则注册回调和许可证。
项目 API Compatibility Level 使用 .NET Standard 2.1。

`unity/com.frameflux.player` 是源码目录，尚未包含构建生成的 DLL；直接用 Git URL
安装此源码目录不能获得完整依赖。先使用构建产物本地安装。后续可将完整构建产物
发布到 UPM registry 或独立发行仓库；开发源码继续与核心保留在同一仓库。

Unity 没有内置 NuGet 包还原功能。NuGetForUnity 可以导入兼容的托管依赖，但
不会自动提供 Unity 控件、原生插件导入规则及 AOT/link.xml 配置。此 UPM 已包含
所需依赖，不要再通过 NuGetForUnity 或 Assets/Plugins 导入同名 DLL。已有项目
使用 SIPSorcery、NAudio 或 Microsoft.Extensions 等程序集时，应统一版本和来源。
.NET 8/10 程序集不能直接作为此 Unity 2022.3 的托管兼容目标。

## 使用通用控件

1. 新建 Canvas → RawImage，在 RawImage 上添加 `MediaView` 和 `MediaRawImage`。
2. 设置 MediaView 的 Source、Backend 和 Auto Play；本地文件使用绝对路径。
3. 需要保持比例时添加 AspectRatioFitter，设为 Fit In Parent。
4. 场景网格使用 `MediaMaterial`，配置纹理属性（内置 shader 为 `_MainTex`，
   URP 通常为 `_BaseMap`）；可引用其他 GameObject 上的同一 MediaView。
5. 内置工厂自动添加 UnityAudioOutput/AudioSource；Mixer Group 可接 AudioMixer。
6. Package Manager → Samples → **MediaView Controls** 可导入按钮/音量示例。

从 Unity 主线程调用公共控制接口，并在 Awake 完成后配置播放器。组件添加后
Awake 会初始化；若在代码中启动，先关闭 AutoPlay，避免重复自动打开。

```csharp
using System;
using FrameFlux;
using FrameFlux.Unity;

// view 是场景中的 MediaView；在 Start 中或之后调用。
view.AutoPlay = false;
view.Source = MediaSource.FromFile(@"C:\Videos\demo.mp4");
view.OpenOptions = new MediaOpenOptions
{
    Video = new MediaVideoOptions { SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame }
};
await view.StartAsync();
if (view.Capabilities.CanPause) await view.PauseAsync();
if (view.Capabilities.CanSeek) await view.SeekAsync(TimeSpan.FromSeconds(10));
view.Volume = 0.5;
view.IsMuted = false;
if (view.Capabilities.CanChangePlaybackRate) view.PlaybackRate = 1.5;
await view.PlayAsync();
var snapshot = await view.CaptureSnapshotAsync();
await view.StopAsync();
```

切源时修改 Source，再调用 StartAsync。WebRTC 选择 Backend=WebRtc，可在代码中
使用 `WebRtcSource.FromWhep(...)` 或 `MediaSource.Parse("ws://host/api/ws?src=camera")`；控制能力以
Capabilities 为准，直播 WebRTC 不提供暂停/Seek。协议的实际验收状态见下文。

可以在启动前注入 `IMediaPlayerFactory`、`MediaOpenOptions`、`IMediaVideoOutput`。
工厂每次 Create 应提供可独立释放的播放器；不要跨视图复用独占的音频 sink。
默认 Unity 音频要求 Dedicated 会话，显式 Shared 配合注入音频会报错。
自定义工厂应自行配置音频输出和原生依赖；内置配置不会替自定义工厂处理这些。
自定义视频输出收到帧租约后按公共契约负责接收/释放，组件只释放其内置纹理输出。

StateChanged、Error、FrameReceived 和 TextureChanged 从 Unity 主线程通知。
控制方法失败会抛异常，调用者应捕获。FrameReceived 仅在订阅时复制最新帧；
常规呈现无需订阅，避免多余 CPU 拷贝。Volume、IsMuted、PlaybackRate 使用
公共播放契约；Position/Duration 为 TimeSpan，未知 Duration 为 null。

## 生命周期与呈现边界

禁用策略默认 Pause：可暂停源暂停，不可暂停直播停止；重新启用后恢复。
也可选择 Stop 或 KeepPlaying。销毁/退出时释放播放器、帧与纹理；DisposeAsync
返回可等待且可重复调用的任务。Editor 在退出 Play Mode 和域重载前等待后端停止，
已验证快速禁用/启用与常规退出；多实例、场景切换、重编译和禁用域重载仍待实测。

内置视频输出是有界最新帧队列 + CPU BGRA/RGBA 转换 + Texture2D 上传，处理
方向、stride 与分辨率变化。Automatic 硬件解码可以使用回读到 CPU 的路径。
**Unity D3D11 外部纹理/零拷贝 GPU 呈现尚未实现**；显式 GPU 呈现模式须提供
自定义平台输出，否则返回 NotSupportedException。现有桌面 GPU 输出不能直接
当作 Unity 纹理使用。多路/高分辨率/高帧率仍须做性能验收。

Unity 音频通过有界 PCM 队列供给 OnAudioFilterRead，支持音量、静音、暂停、
Seek/切源刷新和 Mixer 路由。当前重采样使用最近邻，音频质量、音画同步、设备
切换与空间音效仍须做实际听测，不将 PCM 测试结果等同于完整音频验收。

包导入规则仅启用 Windows Editor/Windows x64 Player。Android/macOS/Linux/
iOS/WebGL 不在当前已实施的 Unity 发布范围内，需要对应后端、插件和验收。

## 验证状态与复现

截至 2026-10-09：四个核心 netstandard2.1 目标构建通过，无框架回退警告；
Unity 自带 C# 9 编译器与 .NET Standard 2.1 引用编译组件通过。嵌入 Unity 自带
x64 Mono 的后端测试通过：FFmpeg 文件视频/音频、控制与快照，真实本地 WHEP
协商/ICE/DTLS/SRTP/JPEG/PCMU 视频音频。IL2CPP 对核心和依赖生成 C++ 成功，
三个原生回调的 reverse P/Invoke 包装与注册表已检查。
真实 Unity Editor 和 Windows Mono Player 的 FFmpeg/WebRTC 场景检查通过：
Texture2D/RawImage、快照、能力允许的控制、AudioSource 音频及重复释放。
Mono Player 另通过本地文件、HTTP MP4（支持 Range 的服务器）和 HLS 播放，
以及 FFmpeg 组件快速禁用/启用。验收中修复了软件解码回退丢失音频工厂、
WebRTC 音频诊断遗漏和 UPM linker 保留规则未自动应用的问题。

**IL2CPP Player 尚未完成。** 实际构建报错：`Currently selected scripting backend
(IL2CPP) is not installed`。早期受限环境中的许可证失败在允许访问现有许可证
服务后已解除。IL2CPP C++ 生成不代表原生编译、裁剪和 Player 运行已通过。
HTTPS/RTSP、go2rtc、真实摄像头编解码、多实例与完整 Unity 生命周期仍需补充验收。

核心验证（Python 3，Windows x64；fixture 须有视频和音频）：

```powershell
python scripts/test-unity-backends.py --unity-editor "D:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe" --source "C:\Videos\demo.mp4"
```

补齐 Windows Build Support（IL2CPP）后运行三个目标的完整 Unity 验收：

```powershell
powershell -NoProfile -File scripts/test-unity-player.ps1 -UnityEditorPath "D:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe" -Source "C:\Videos\demo.mp4"
```

脚本在仓库 .verify 下建立独立工程，默认验收 Editor、Mono 和 Il2Cpp。
自动验收使用隐藏窗口且结束后退出。如需直接观看，在当前 PowerShell 会话执行：

```powershell
& .\scripts\test-unity-player.ps1 -UnityEditorPath "D:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe" -Source "C:\Videos\demo.mp4" -Preview
```

这会打开独立的可见 Unity 预览工程并进入 Play Mode。Game 窗口提供播放、暂停、
重播和循环选项；预览不会自动退出，结束后手动关闭 Unity。
在当前 PowerShell 会话中直接调用 `& .\scripts\test-unity-player.ps1 ... -Targets Editor,Mono`
可先验证现有目标；增加 `-Backend WebRtc` 验证内置的本地 WHEP 环回场景。
`unity/Tests~/Backends/serve-media.py` 提供支持 HTTP Range 的本地 fixture 服务，
可将其 URL 传给 Source 来复现 HTTP MP4/Seek 检查。
音频/纹理验证需要真实音频和图形设备。完整验收清单见仓库
`docs/UNITY-COMPATIBILITY.md`。

## 旧 NativeAOT 原型

`FrameFluxNativePlayer` / `FrameFluxRawImagePlayer` 与 NativeAOT sample 保留供迁移。
只有构建时指定 `-IncludeNativeAotPrototype` 才生成其 `FrameFluxUnity.dll`，这需要
Visual Studio C++ 工具和 Windows SDK。它采用 C ABI、仅 FFmpeg、软件视频输出及
Windows waveOut 音频；新项目使用 MediaView。默认 UPM 无需 NativeAOT 编译工具。
