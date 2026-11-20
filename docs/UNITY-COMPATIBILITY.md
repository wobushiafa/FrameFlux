# Unity 兼容实施与验收状态

日期：2026-10-09。用户已授权完整的公共核心兼容改造；当前目标为
Unity 2022.3 LTS / Windows x64，含 FFmpeg、WebRTC、Editor、Mono、IL2CPP。
代码和 UPM 产物已建立，Editor 与 Mono Player 的基础场景验收已通过；
IL2CPP Player 构建仍因缺少 Windows IL2CPP 模块而阻塞。

## 分层与交付

WPF、Avalonia、Unity 的 MediaView 共用 FrameFlux.Presentation 生命周期调度和
FrameFlux.Abstractions 契约，通过 IMediaPlayerFactory 选择 FFmpeg/WebRTC。
Unity MediaView 与 RawImage、Renderer 适配器分离，UnityTextureOutput 接受 CPU
帧租约，UnityAudioOutput 将 PCM 送入 AudioSource/AudioMixer。

Abstractions 增加 netstandard2.1，Presentation/FFmpeg/WebRtc 增加同一目标并
保留原有目标。内部兼容层处理现代 BCL API、原生加载和可取消网络调用。
新增 IMediaAudioOutput 与 FFmpeg AudioOutputFactory 是公开 API 的增量扩展；
原有桌面默认音频输出继续生效。三个 FFmpeg 原生回调已使用静态委托和 AOT 标记。
播放器重复/并发 DisposeAsync 共享同一个释放任务。
软件解码回退保留注入的音频工厂；WebRTC 公共诊断报告兼容的注入音频 sink。
Editor 构建回调向 Unity Linker 显式注册 UPM 内 link.xml，保留第三方反射配置
类型与原生回调；仅将 link.xml 放在包内不会在 Unity 2022 自动生效。

开发源码保留在当前仓库 `unity/com.frameflux.player`；构建脚本生成自包含
`artifacts/unity-managed/com.frameflux.player`。Unity 以 UPM 交付，核心仍可用
NuGet；UPM 提供组件、netstandard2.1 DLL、依赖、原生插件、.meta、asmdef、
link.xml 和许可证。源码目录尚无 DLL，不能直接作为完整 Git UPM 安装产物。
推荐待验收后发布到 registry 或独立发行仓库，使用独立包版本并记录对应核心版本。
当前未创建、提交或推送远程发行仓库，未修改 CI 或机器配置。

Unity 的 CPU 呈现已实现；硬件解码可回读后呈现。Unity D3D11 外部纹理/GPU
零拷贝输出尚未实现，显式 GPU 模式要求注入自定义平台输出。这个缺口须作为
后续工作明确跟踪，不能宣称已经完成所有呈现能力或所有 Unity 发布平台。

## 已完成的验证

| 检查 | 结果与边界 |
| --- | --- |
| 四个 netstandard2.1 目标 | 构建通过，无 NU1701 框架回退 |
| 公开 API 基线 | 仅新增音频输出接口/工厂选项；桌面 API 回归通过 |
| FFmpeg 单元测试 | 239 通过，1 跳过 |
| WebRtc 单元测试 | 54 通过，5 跳过 |
| Unity 辅助测试 | 10 通过；含 PCM 队列、帧及原型桥接检查 |
| WPF/Avalonia API 测试 | 3 通过 |
| Avalonia Linux 测试 | 4 通过 |
| Unity 实际编译器 | 使用安装内 C# 9 编译器/引用编译组件与 smoke 脚本通过 |
| Unity 自带 x64 Mono 后端 | FFmpeg 文件视频/音频、暂停/Seek/倍速/音量/静音、快照、重复释放通过 |
| 真实 WebRTC 环回 | WHEP、ICE/DTLS/SRTP、JPEG 视频、PCMU 音频通过；不是网络摄像头/全部编解码器验收 |
| IL2CPP 核心 C++ 生成 | 包含完整依赖通过；三个 reverse P/Invoke 回调包装和注册通过 |
| 真正的 Unity Editor 场景 | FFmpeg 与 WebRTC：纹理、RawImage、快照、能力允许的控制、AudioSource、重复释放通过 |
| Windows Mono Player 场景 | FFmpeg 与 WebRTC 的相同场景检查通过；已验证 UPM linker 回调生效 |
| Mono Player 协议/生命周期 | 文件、HTTP MP4/Seek（Range 服务）、HLS，以及 FFmpeg 快速禁用/启用通过 |

独立 Mono runtime harness 不加载 Unity 场景，因此自身不覆盖 Texture2D、
AudioSource、Editor 或 Player。IL2CPP 对核心生成 C++ 不覆盖 Unity 引擎的链接
裁剪、原生 C++ 编译或实际 Player。核心回归不能替代下表的完整验收。

## 待完成的验收

| 检查 | 当前状态/完成标准 |
| --- | --- |
| Editor UPM 导入与 Play Mode | 基础场景已通过；更全面的生命周期/性能仍需测试 |
| Windows Mono Player | FFmpeg/WebRTC 基础场景已通过；真实摄像头/多实例等仍需测试 |
| Windows IL2CPP Player | 实际构建报错 IL2CPP is not installed；补齐模块后构建/裁剪/运行 |
| 文件/HTTP/HTTPS/HLS/RTSP | 文件、HTTP、HLS 通过；HTTPS/RTSP、取消/超时/重连仍需实测 |
| WebRTC WHEP/go2rtc | 本地 WHEP 通过；go2rtc、真实摄像头、H264/VP8/Opus 等仍需实测 |
| UI/材质呈现 | 实际场景颜色、方向、宽高比、切源/分辨率变化与共享纹理 |
| GPU 呈现 | Unity 平台输出尚未实现；需要单独实现/验收 |
| Unity 音频 | 实际 AudioSource/Mixer 输出、音画同步、设备切换、听测和性能 |
| 资源生命周期 | 重复释放、FFmpeg 快速禁用/启用通过；打开时销毁、多实例、场景切换、重编译/域重载及禁用域重载待验证 |
| 安装/依赖冲突 | 新工程导入和 NuGetForUnity 共存项目的单份程序集/版本统一 |
| 性能 | 多路、高清、高帧率下 CPU 上传/拷贝与有界队列 |
| 其他 Unity 平台 | Android/macOS/Linux/iOS/WebGL 未纳入本轮实现或验收 |

早期 `.verify/unity-2022/import.log` 的许可证失败在允许访问现有许可证服务后解除。
最终 Editor/Mono Player 的独立 PASS 结果在 `.verify/unity-player-tests/*.result.txt`，
各次尝试使用唯一文件名，避免旧结果被误用。IL2CPP 失败日志为
`.verify/unity-player-tests/Il2Cpp-572703fe709041faa9411aea4328baa8.log`。
`scripts/test-unity-player.ps1` 已准备真实场景/Editor/Mono/IL2CPP 验收入口；
`scripts/test-unity-backends.py` 复现编译器、x64 Mono 和核心 IL2CPP 代码生成检查。
所有验证使用仓库 .verify 下的隔离产物，不修改用户工程或全局机器配置。

## 参考

- [Unity 2022.3 .NET profiles](https://docs.unity3d.com/2022.3/Documentation/Manual/dotnetProfileSupport.html)
- [Unity scripting restrictions](https://docs.unity3d.com/2022.3/Documentation/Manual/ScriptingRestrictions.html)
- [Unity native rendering extensions](https://docs.unity3d.com/2022.3/Documentation/Manual/LowLevelNativePluginRenderingExtensions.html)
