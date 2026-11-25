# QSrcRecorder

Windows 上的轻量录屏工具。用 .NET 8 和 WPF 编写，采集走 Windows Graphics Capture，编码走 ffmpeg。

## 功能

- 全屏录制、区域录制、窗口录制
- 输出 H.264 MP4
- 硬件编码自动选择 NVENC、Intel Quick Sync 或 AMD AMF，失败时改用 libx264
- 系统声音和麦克风分开录制，可分别调节音量
- 摄像头画中画：勾选后拖动预览窗口改位置，拖四边或四角改大小
- 鼠标点击高亮，颜色可自选
- 多显示器：录制来源列出已连接的显示器，选择其中一块录制
- 快捷键：F9 开始或停止，F10 暂停或继续

定时停止和录制倒计时还没有做进当前版本。

## 界面

- 深色科技风，星空背景
- 窗口 960×540，16:9
- 中文使用华文中宋，英文和数字使用 Times New Roman
- 标题栏名称使用华文隶书
- 任务栏图标为深色底、青色六瓣光圈

## 使用方法

1. 从 [v0.3 Release](https://github.com/qiz7z/QSrcRecorder/releases/tag/v0.3) 下载 `QSrcRecorder.exe`。这是自包含单文件，约 174 MB，不需要安装 .NET 8 运行时。
2. 把 `ffmpeg.exe` 和 `ffprobe.exe` 放在 `QSrcRecorder.exe` 同一个目录。Release 附件里没有这两个文件，编码和检查成片时会用到。
3. 双击 `QSrcRecorder.exe`。

系统需要 Windows 10 1903 或更高版本，64 位，以及支持 DirectX 11 的显卡。

## 从源码构建

需要 .NET 8 SDK。发布时会把 `tools/ffmpeg/` 里的 `ffmpeg.exe` 和 `ffprobe.exe` 复制到输出目录，不会打进 exe。

```bash
git clone https://github.com/qiz7z/QSrcRecorder.git
cd QSrcRecorder
dotnet publish src/ScreenRecorder -c Release
```

产物在：

```text
src/ScreenRecorder/bin/Release/net8.0-windows10.0.22000.0/win-x64/publish/
```

该目录里应有 `QSrcRecorder.exe`、`ffmpeg.exe`、`ffprobe.exe`。工程已设置为 `win-x64` 自包含单文件。

本地调试：

```bash
dotnet run --project src/ScreenRecorder
```

## 许可证

[MIT License](LICENSE)。Copyright (c) 2026 qiz7z。
