# QSrcRecorder · 拾光留影

一个 Windows 轻量级录屏工具：单文件夹绿色运行、不录制时零资源占用、优先使用显卡硬件编码。
界面为浅色卡片式设计：浅灰底、白色圆角卡片、线性图标模式选择、柔和蓝色选中态与朱红主按钮。

## 当前版本：v0.2

| 功能 | 状态 |
|---|---|
| 全屏 / 区域 / 窗口录制 | ✅ |
| H.264 MP4 输出（faststart） | ✅ |
| 硬件编码自动检测（NVENC / QuickSync / AMF）+ 软编 x264 兜底 | ✅ |
| 帧率（24/30/60）、画质（高/中/低）、分辨率缩放（100%/75%/50%） | ✅ |
| 暂停 / 继续（暂停的时间不进成片） | ✅ |
| 快捷键：F9 开始/停止，F10 暂停/继续 | ✅ |
| 录制悬浮条（闲置自动淡化）、录制时长显示 | ✅ |
| 设置持久化、自动打开输出文件夹 | ✅ |
| 系统声音录制（WASAPI Loopback，无需虚拟声卡） | ✅ |
| 麦克风录制（WASAPI Capture，设备原生格式） | ✅ |
| 双声道独立音效（音量 / 人声降噪门 / 系统低音·高音） | ✅ |
| 鼠标点击高亮（合帧光圈，颜色可自选） | ✅ |
| 鼠标跟随圆（常驻高亮，颜色与点击高亮共用） | ✅ |
| 摄像头画中画 / 定时自动停止 | 🚧 后续规划 |

## 环境要求

- Windows 10 1903+ / Windows 11（窗口捕获与去黄框体验在 Win11 上最佳）
- 运行：无需安装任何东西（绿色版自带 ffmpeg 与 .NET 运行时）
- 开发构建：.NET 8 SDK

## 构建与运行

```bash
# 开发运行
dotnet run --project src/ScreenRecorder

# 单元测试
dotnet test src/ScreenRecorder.Tests

# 端到端冒烟测试（录制主显示器 3 秒并校验 MP4）
dotnet run --project src/ScreenRecorder.Smoke -c Release

# 发布绿色版（自包含，目标机器免装 .NET）
dotnet publish src/ScreenRecorder -c Release -r win-x64 --self-contained true -o publish
```

`ffmpeg.exe` / `ffprobe.exe` 从 `tools/ffmpeg/` 自动复制到输出目录；程序也支持从自身目录或 PATH 中查找，可自行替换更精简的构建。

## 音频说明（v0.2）

- **系统声音**：`WasapiLoopbackCapture`，捕获当前播放设备回环；使用设备原生采样格式，避免空 WAV。
- **麦克风**：`WasapiCapture`，使用设备原生格式（比 WinMM `WaveIn` 在 Realtek 阵列等硬件上更可靠）。
- **合成**：录制结束后由 ffmpeg `filter_complex` 将视频与音频 mux；双通道时 `amix` 混合，并做 `aformat` 统一为立体声 16-bit 44.1 kHz。
- **音效（可折叠面板）**
  - 麦克风：音量、噪声门（`agate`，默认关闭）
  - 系统声音：音量、低音 / 高音（`bass` / `treble`）
- 仅勾选麦克风或仅勾选系统声音时同样输出有声视频；两路都关则输出无声视频。

## 点击 / 鼠标高亮（v0.2）

- **点击高亮**：低级鼠标钩子捕获左键按下，在采集帧 BGRA 缓冲上绘制扩散光圈（软件合帧，全屏 / 区域 / 窗口均生效）。
- **鼠标跟随圆**：录制期间屏上半透明跟随圆（覆盖层），颜色与点击高亮共用色轮 / 预设色。
- 颜色：预设色块 + 自定义 `#RRGGBB`，设置持久化。

## 架构

```
UI (WPF MainView)            主界面 / 参数卡片 / 音效与高亮设置
    │
Overlays (WinForms)          区域选框 / 窗口选择 / 录制悬浮条 / 鼠标跟随圆
    │
RecordingSession             一次录制的编排：定帧率循环、暂停、音视频收尾
    ├─ WgcCapture            Windows Graphics Capture 帧池（CsWin32 生成 D3D11 互操作）
    │                        GPU 拷贝 → 暂存纹理 → 回读 BGRA（支持帧池级缩放）
    ├─ ClickHighlightEngine  鼠标钩子 + 帧内绘制点击光圈
    ├─ AudioCapture          麦克风 WASAPI Capture → WAV
    ├─ SystemAudioCapture    系统声音 WASAPI Loopback → WAV
    └─ FrameWriterQueue      独立写线程 + 有界缓冲（编码跟不上时丢最旧帧）
         │
         FfmpegVideoEncoder  BGRA 帧经 stdin 管道送 ffmpeg → H.264 MP4
         │
         （可选）ffmpeg mux   视频 + 系统/麦克风音频 filter_complex → 最终 MP4
```

关键设计：

- **采集**走 Windows Graphics Capture（系统 API，硬件加速，无需驱动）；
  D3D11 COM 调用由 CsWin32 从官方元数据生成。
- **编码**交给 ffmpeg 子进程：NVENC 等硬编优先，早期编码器失败时自动软编（libx264）重试。
- **恒定帧率**：采集循环按目标帧率推帧，无新帧时复用上一帧；暂停即停帧。
- **窗口大小变化**时录制自动收尾，避免花屏文件。
- **音频与视频分离录制再合成**，避免实时混音拖累采集线程；mux 前后校验视频流，避免「只有声音没有画面」的坏文件覆盖有效成片。

## 性能说明（实测：2560×1600 屏幕 + NVENC）

- 采集回读约 4~5ms/帧，日常负载很低。
- 100% 分辨率下 BGRA 原始帧约 16.7MB/帧，管道带宽是瓶颈；
  **2K/4K 屏幕建议在界面选 75% 或 50% 分辨率**（GPU 侧缩放），可稳定满 30fps。
- 静止桌面录制时 GPU 可能保持低频，导致前几秒丢帧率升高，属正常现象。

## 已知限制

- 受 DRM 保护的内容（Netflix 等）按系统规则录出来是黑屏，所有录屏软件一致。
- Win10 21H1 及更早系统上录制会有系统绘制的黄色边框（无法关闭）。
- 区域选择在单个显示器内进行；跨屏区域暂不支持。
- 暂停期间画面停帧（时长不含暂停时间），不支持暂停时插入定格画面。
- 录制悬浮条会出现在画面右下角（闲置 3 秒后自动淡化到 35% 透明度，鼠标靠近恢复）。
  早期版本用 `DWMWA_EXCLUDED_FROM_CAPTURE` 把悬浮条排除在捕获外，但部分 Win11 版本上
  被排除的窗口不会重新合成，屏幕上秒数永远停在第一帧，已移除该做法。
- 双显卡环境下 NVENC 偶发早期失败时会自动切软编重试，成片编码方式可能与所选不完全一致。

## 路线图

- **v0.3**：定时自动停止、录制倒计时、多显示器区域
- **v0.4**：摄像头画中画
- **v1.0**：绿色版 + 安装包、录制历史列表、GPU 侧色彩空间转换（进一步降低管道压力）

## 项目结构

```
src/
├─ ScreenRecorder/            # 主程序（WPF + WinForms 混合，输出 QSrcRecorder.exe）
│  ├─ Interop/                # WGC / D3D11 / Win32 互操作
│  ├─ Capture/                # WGC 采集引擎
│  ├─ Encoding/               # ffmpeg 进程管理、参数构建、硬编探测
│  ├─ Audio/                  # 麦克风 / 系统声音 WASAPI 捕获
│  ├─ Overlays/               # 区域选择、悬浮条、点击高亮、鼠标跟随
│  ├─ UI/Wpf/                 # 主界面、窗口选择器、颜色轮
│  └─ Settings/               # 设置持久化
├─ ScreenRecorder.Tests/      # 单元测试（xunit）
└─ ScreenRecorder.Smoke/      # 端到端冒烟测试（真实录制并校验）
tools/ffmpeg/                 # 内置 ffmpeg（构建时自动复制）
docs/                         # 设计系统等文档
```

## 界面设计系统

完整设计规范见 `docs/design-system.md`（Swiss Minimalism 风格、"录制红 + 波形蓝"配色）。
主界面为 **WPF**（`src/ScreenRecorder/UI/Wpf/MainView.xaml`）：矢量圆角卡片、投影、
悬停动效、矢量图标；录制悬浮条与区域/窗口选择器仍为 WinForms（互操作共存）。
录制引擎与会话逻辑与 UI 层完全解耦。
