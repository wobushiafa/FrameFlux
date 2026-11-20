# FrameFlux.Unity.Native

Windows x64 NativeAOT C 接口，供 Unity 2022.3 LTS 调用。构建与使用说明见
[`unity/com.frameflux.player/README.md`](../../unity/com.frameflux.player/README.md)。

导出名以 `ffu_` 开头，ABI 版本为 1，调用约定 Cdecl。播放器句柄为不复用的
64 位整数，字符串为 NUL 结尾的 UTF-8。调用者拥有输入和输出缓冲区。
控制接口为同步操作，Unity 包将其放在后台任务执行；状态和帧查询遇到控制操作
正在执行时立即返回 busy，不阻塞 Unity 主线程。

| 导出 | 参数 / 结果 |
| --- | --- |
| `ffu_abi_version` | 返回 ABI 版本 1 |
| `ffu_create` | UTF-8 原生库目录、输出 `int64_t` 句柄 |
| `ffu_destroy` | 句柄；销毁后不可继续调用 |
| `ffu_open` | 句柄、UTF-8 媒体源、`int` 音频开关 |
| `ffu_command` | 句柄、命令编号、`double` 参数 |
| `ffu_status` | 句柄；输出 `int` 状态、`double` 位置/时长秒数 |
| `ffu_audio_status` | 句柄；输出 `int` 后端/可用性。0=无/其他，1=waveOut，2=WASAPI |
| `ffu_copy_frame` | 句柄、RGBA 指针、容量字节数；输出 `int` 宽/高、读写 `int64_t` 序号 |
| `ffu_error` | 句柄、UTF-8 输出缓冲区、容量；返回含 NUL 的所需字节数 |

控制命令：0=播放、1=暂停、2=停止、3=跳转秒数、4=音量、5=静音（非零）、6=倍速。
普通操作返回 0 成功、-1 失败。轮询返回 3 表示 busy。
帧拷贝返回 0 无新帧、1 已拷贝、2 缓冲区不足；2 会返回最新宽高但不消费序号，
按 `width * height * 4` 分配后重试。序号仅在成功拷贝时更新；帧内容为紧凑 RGBA32，
第一行是底部，适合 Unity 默认纹理坐标。

错误文本：句柄 0 返回当前 OS 线程最近一次操作失败的原因，必须在失败操作之后
同线程读取；有效播放器句柄返回后台播放错误。异常不会传播出 C 接口。
