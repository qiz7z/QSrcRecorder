<p align="center">
  <img src="docs/icon.png" alt="QSrcRecorder" width="96"/>
</p>

# QSrcRecorder

QSrcRecorder 是 Windows 上的轻量录屏工具，用 .NET 8 和 WPF 写成。适合做课程讲解、软件演示，或把一次操作留成可以回看的视频。

图标来自仓库里的 `src/ScreenRecorder/Assets/QSrcRecorder.ico`。

## 功能

- **多种编码**：输出 H.264 MP4。优先使用 NVENC、Intel Quick Sync 或 AMD AMF，硬件编码不可用时自动改用 libx264。
- **声音分开录**：系统声音和麦克风各自采集，音量可以分别调节。
- **摄像头画中画**：勾选后出现预览窗口。拖动窗口改位置，拖四边或四角改大小，成片按这个位置和大小叠加。
- **点击高亮**：鼠标点击带颜色高亮，方便演示时让观众看清点了哪里。
- **三种录制范围**：全屏、框选区域，或指定一个窗口。
- **多显示器**：录制来源会列出已连接的显示器，选择其中一块来录。

定时停止和录制倒计时还没有放进当前版本。

## 界面

深色科技风，星空背景。窗口是 960×540 的 16:9 宽屏。中文使用华文中宋，英文和数字使用 Times New Roman，标题栏名称使用华文隶书。

界面截图待补。仓库里的 `docs/screenshot.png` 仍是旧图标稿，不是当前界面，所以这里先不放。

## 使用方法

1. 打开 [Releases](https://github.com/qiz7z/QSrcRecorder/releases)，下载自包含的 `QSrcRecorder.exe`。文件大约 174 MB，不需要安装 .NET 8 运行时。
2. 把 `ffmpeg.exe` 和 `ffprobe.exe` 放在 exe 同一个目录。发布附件里没有这两个文件，开始录制和检查成片时会用到。
3. 双击 `QSrcRecorder.exe` 即可使用。

系统需要 64 位 Windows 10（1903 或更高）或 Windows 11，以及支持 DirectX 11 的显卡。

常用快捷键：F9 开始或停止，F10 暂停或继续。

## 从源码构建

需要安装 .NET 8 SDK。`tools/ffmpeg/` 里要有 `ffmpeg.exe` 和 `ffprobe.exe`，发布时会复制到输出目录，不会打进 exe。

```bash
git clone https://github.com/qiz7z/QSrcRecorder.git
cd QSrcRecorder
dotnet publish src/ScreenRecorder -c Release
```

产物目录：

```text
src/ScreenRecorder/bin/Release/net8.0-windows10.0.22000.0/win-x64/publish/
```

其中应有 `QSrcRecorder.exe`、`ffmpeg.exe` 和 `ffprobe.exe`。工程已设置为 Windows x64 自包含单文件。

本地调试可以直接运行：

```bash
dotnet run --project src/ScreenRecorder
```

## 许可证

本项目使用 [MIT License](LICENSE)。Copyright (c) 2026 qiz7z。
