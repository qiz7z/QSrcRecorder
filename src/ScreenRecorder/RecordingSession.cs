using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using ScreenRecorder.Audio;
using ScreenRecorder.Capture;
using ScreenRecorder.Encoding;

namespace ScreenRecorder;

public enum RecordMode
{
    FullScreen,
    Region,
    Window,
}

public sealed record RecordingOptions
{
    public RecordMode Mode { get; init; } = RecordMode.FullScreen;
    /// <summary>全屏 / 区域模式：目标显示器句柄。</summary>
    public IntPtr MonitorHandle { get; init; }
    /// <summary>窗口模式：目标窗口句柄。</summary>
    public IntPtr WindowHandle { get; init; }
    /// <summary>区域模式：裁剪矩形，显示器相对物理坐标（Region 专用）。</summary>
    public Rectangle? Region { get; init; }
    public int Fps { get; init; } = 30;
    public QualityPreset Quality { get; init; } = QualityPreset.Medium;
    /// <summary>输出分辨率相对源的比例（0.5 = 一半大小，GPU 侧缩放，显著降低开销）。</summary>
    public double Scale { get; init; } = 1.0;
    public EncoderKind Encoder { get; init; } = EncoderKind.Auto;
    public string OutputFolder { get; init; } = "";
    /// <summary>同时录制麦克风声音。</summary>
    public bool RecordAudio { get; init; } = false;
    /// <summary>同时录制系统声音（WASAPI 回环）。</summary>
    public bool RecordSystemAudio { get; init; } = false;
    /// <summary>麦克风音量倍率（0.1 ~ 5.0，默认 1.0）。</summary>
    public double MicVolume { get; init; } = 1.0;
    /// <summary>系统声音音量倍率（0.1 ~ 5.0，默认 1.0）。</summary>
    public double SysVolume { get; init; } = 1.0;
    /// <summary>麦克风降噪门。</summary>
    public bool MicNoiseGate { get; init; } = false;
    /// <summary>系统声音低音增强（-5 ~ +5 dB，默认 0）。</summary>
    public int SysBass { get; init; } = 0;
    /// <summary>系统声音高音增强（-5 ~ +5 dB，默认 0）。</summary>
    public int SysTreble { get; init; } = 0;
    /// <summary>成片角落叠摄像头人像（画中画）。</summary>
    public bool WebcamEnabled { get; init; } = false;
    /// <summary>摄像头设备 Id（空=默认第一台）。</summary>
    public string WebcamDeviceId { get; init; } = "";
    /// <summary>画中画角落：TopLeft/TopRight/BottomLeft/BottomRight。</summary>
    public string WebcamCorner { get; init; } = "BottomRight";
    /// <summary>画中画大小档：0 小 / 1 中 / 2 大。</summary>
    public int WebcamSizeIndex { get; init; } = 1;
    /// <summary>摄像头画面水平镜像（自拍观感，默认开）。</summary>
    public bool WebcamMirror { get; init; } = true;
    /// <summary>画中画自定义位置（相对输出帧的宽高比）。</summary>
    public bool WebcamCustomPosition { get; init; } = false;
    /// <summary>画中画左上角 X 相对输出帧宽的比例（0~1）。</summary>
    public double WebcamPosX { get; init; } = 0.75;
    /// <summary>画中画左上角 Y 相对输出帧高的比例（0~1）。</summary>
    public double WebcamPosY { get; init; } = 0.75;
    /// <summary>画中画宽度相对输出帧宽的比例（0~1）。</summary>
    public double WebcamSizeW { get; init; } = 0.22;
    /// <summary>画中画高度相对输出帧高的比例（0~1）。</summary>
    public double WebcamSizeH { get; init; } = 0.16;
}

public sealed class RecordingResult
{
    public string OutputPath = "";
    public long FrameCount;
    public TimeSpan Duration;
    public string? StopReason;
    public string? Error;
    public bool Success;
    /// <summary>本次实际使用的编码器（Auto 解析后的结果）。</summary>
    public EncoderKind EncoderUsed;
    /// <summary>诊断：采集（回读）累计耗时。</summary>
    public double CaptureMs;
    /// <summary>诊断：写入 ffmpeg 管道累计耗时。</summary>
    public double WriteMs;
    /// <summary>诊断：编码端跟不上时丢弃的帧数。</summary>
    public long DroppedFrames;
    /// <summary>音频合成警告（非致命，视频仍成功）。</summary>
    public string? AudioWarning;
    /// <summary>摄像头/画中画警告（非致命）。</summary>
    public string? WebcamWarning;

    /// <summary>录制刚起步就因编码器失败（典型：双显卡机器上 NVENC 间歇不可用），适合自动降级重录。
    /// 帧数上限放宽到 12.5 秒（300 帧），覆盖编码中途失败的场景。</summary>
    public bool IsEarlyEncoderFailure =>
        !Success && Error != null && EncoderUsed != EncoderKind.SoftwareX264 && FrameCount <= 300;
}

public static class RegionMath
{
    /// <summary>把用户框选的矩形约束到画面内并取偶数尺寸（yuv420p 要求）。过小的输入直接抛异常。</summary>
    public static Rectangle PrepareCrop(Rectangle region, int fullWidth, int fullHeight)
    {
        if (region.Width < 2 || region.Height < 2)
            throw new InvalidOperationException("所选录制区域太小，请重新框选。");
        int x = Math.Clamp(region.X, 0, Math.Max(0, fullWidth - 2));
        int y = Math.Clamp(region.Y, 0, Math.Max(0, fullHeight - 2));
        int w = Math.Clamp(region.Width, 2, fullWidth - x);
        int h = Math.Clamp(region.Height, 2, fullHeight - y);
        w -= w % 2;
        h -= h % 2;
        if (w < 2 || h < 2)
            throw new InvalidOperationException("所选录制区域太小，请重新框选。");
        return new Rectangle(x, y, w, h);
    }
}

public static class OutputFile
{
    public static string DefaultFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "QSrcRecorder");

    public static string BuildPath(string folder)
    {
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"屏幕录制_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
    }
}

/// <summary>
/// 独立写线程 + 有界缓冲池：把“采集节奏”与“管道写入速度”解耦。
/// 编码端跟不上时丢弃最旧的帧（画面跳过但不卡顿）。
/// </summary>
internal sealed class FrameWriterQueue : IDisposable
{
    private const int MaxQueued = 3;
    private const int MaxPooled = 8;

    private readonly FfmpegVideoEncoder _encoder;
    private readonly Queue<(byte[] Buf, int Len)> _queue = new();
    private readonly List<byte[]> _pool = new();
    private readonly object _gate = new();
    private readonly Thread _thread;
    private bool _completed;
    private bool _disposed;

    public long DroppedFrames { get; private set; }
    public double WriteMs { get; private set; }

    /// <summary>写线程遇到的致命错误（编码器退出等）。设置后采集循环应尽快停止。</summary>
    public volatile string? WriteError;

    public FrameWriterQueue(FfmpegVideoEncoder encoder)
    {
        _encoder = encoder;
        _thread = new Thread(Consume) { IsBackground = true, Name = "sr-frame-writer" };
    }

    public void Start() => _thread.Start();

    /// <summary>整帧入队（复制到池化缓冲）。队列满时丢弃最旧帧。</summary>
    public void EnqueueFull(byte[] frame)
    {
        byte[] buf = Rent(frame.Length);
        Buffer.BlockCopy(frame, 0, buf, 0, frame.Length);
        if (!Enqueue(buf, buf.Length))
            Return(buf);
    }

    /// <summary>裁剪帧入队：把区域内的行复制到池化缓冲后排队。</summary>
    public void EnqueueCropped(byte[] frame, int cropX, int cropY, int cropW, int cropH, int fullWidth)
    {
        int rowBytes = cropW * 4;
        byte[] buf = Rent(rowBytes * cropH);
        int srcPitch = fullWidth * 4;
        for (int y = 0; y < cropH; y++)
            Buffer.BlockCopy(frame, (cropY + y) * srcPitch + cropX * 4, buf, y * rowBytes, rowBytes);
        if (!Enqueue(buf, buf.Length))
            Return(buf);
    }

    private byte[] Rent(int length)
    {
        lock (_gate)
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i].Length == length)
                {
                    byte[] buf = _pool[i];
                    _pool.RemoveAt(i);
                    return buf;
                }
            }
        }
        return new byte[length];
    }

    private void Return(byte[] buf)
    {
        lock (_gate)
        {
            if (_pool.Count < MaxPooled)
                _pool.Add(buf);
        }
    }

    private bool Enqueue(byte[] buf, int len)
    {
        lock (_gate)
        {
            if (_completed)
                return false;
            while (_queue.Count >= MaxQueued)
            {
                var (old, _) = _queue.Dequeue();
                Return(old);
                DroppedFrames++;
            }
            _queue.Enqueue((buf, len));
            Monitor.Pulse(_gate);
            return true;
        }
    }

    private void Consume()
    {
        while (true)
        {
            byte[] buf;
            int len;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_completed)
                    Monitor.Wait(_gate);
                if (_queue.Count == 0 && _completed)
                    return;
                (buf, len) = _queue.Dequeue();
            }
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _encoder.Write(buf, 0, len);
                WriteMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
            }
            catch (Exception ex)
            {
                // ffmpeg 退出/管道断裂：记录错误并唤醒所有等待方，让会话体面收场
                WriteError = ex.Message;
                lock (_gate)
                {
                    _completed = true;
                    while (_queue.Count > 0)
                    {
                        var (old, _) = _queue.Dequeue();
                        Return(old);
                    }
                    Monitor.PulseAll(_gate);
                }
                return;
            }
            finally
            {
                Return(buf);
            }
        }
    }

    /// <summary>停止：排空队列后结束线程。</summary>
    public void Complete(int joinMs = 30000)
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.Pulse(_gate);
        }
        _thread.Join(joinMs);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Complete(3000);
    }
}

/// <summary>
/// 一次录制会话：启动 WGC 采集与 ffmpeg 编码，在独立线程以恒定帧率推帧。
/// 暂停 = 停止推帧（暂停的时间不会出现在成片中）。
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly RecordingOptions _opts;
    private readonly string _ffmpegPath;
    private readonly Overlays.ClickHighlightEngine? _clickEngine;
    private readonly string _clickColorHex;
    private readonly WebcamCapture? _sharedWebcam; // 由调用方提供，overlay 可复用预览
    private D3DContext? _d3d;
    private WgcCapture? _capture;
    private FfmpegVideoEncoder? _encoder;
    private FrameWriterQueue? _writer;
    private Thread? _loop;
    private volatile bool _stop;
    private volatile bool _paused;
    private long _framesWritten;
    private int _fullWidth;
    private int _fullHeight;
    private Rectangle _crop;
    private bool _cropped;
    private string _outputPath = "";
    private string? _stopReason;
    private string? _error;
    private double _captureMs;
    private double _writeMs;
    private EncoderKind _encoderUsed;
    private AudioCapture? _audioCapture;
    private string? _audioWavePath;
    private SystemAudioCapture? _systemAudioCapture;
    private string? _systemAudioWavePath;
    private string? _tempMuxPath;
    private string? _audioMuxWarning;
    private string? _webcamWarning;
    private WebcamCapture? _webcam;
    private bool _ownsWebcam; // 仅内部创建的摄像头才需要 dispose
    private byte[]? _composeBuffer;
    private Rectangle _monitorRect;
    private readonly byte _clickB, _clickG, _clickR;

    public event Action<RecordingResult>? Completed;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public string OutputPath => _outputPath;
    public TimeSpan RecordedDuration => TimeSpan.FromSeconds(_framesWritten / (double)_opts.Fps);

    public RecordingSession(RecordingOptions opts, string ffmpegPath,
        Overlays.ClickHighlightEngine? clickEngine = null,
        string clickColorHex = "#DC2626",
        bool mouseHighlight = false,
        WebcamCapture? sharedWebcam = null)
    {
        _opts = opts;
        _ffmpegPath = ffmpegPath;
        _clickEngine = clickEngine;
        _clickColorHex = clickColorHex;
        _sharedWebcam = sharedWebcam;
        (_clickB, _clickG, _clickR) = Overlays.ClickHighlightEngine.ParseColor(clickColorHex);
        _ = mouseHighlight; // 跟随圆由 UI 覆盖层负责，session 只合帧点击光圈与摄像头
    }

    public void Start()
    {
        string folder = string.IsNullOrWhiteSpace(_opts.OutputFolder) ? OutputFile.DefaultFolder() : _opts.OutputFolder;
        _outputPath = OutputFile.BuildPath(folder);

        _d3d = new D3DContext();
        try
        {
            _capture = new WgcCapture(_d3d);
            _capture.SourceClosed += Stop;

            var size = _opts.Mode == RecordMode.Window
                ? _capture.StartForWindow(_opts.WindowHandle, _opts.Scale)
                : _capture.StartForMonitor(_opts.MonitorHandle, _opts.Scale);
            _fullWidth = size.Width;
            _fullHeight = size.Height;

            _cropped = _opts.Mode == RecordMode.Region && _opts.Region.HasValue;
            if (_cropped)
            {
                // 区域坐标是源像素，换算到（可能已缩放的）帧池坐标
                var region = _opts.Region!.Value;
                var scaled = new Rectangle(
                    (int)(region.X * _opts.Scale), (int)(region.Y * _opts.Scale),
                    (int)(region.Width * _opts.Scale), (int)(region.Height * _opts.Scale));
                _crop = RegionMath.PrepareCrop(scaled, size.Width, size.Height);
            }

            // 点击坐标换算用的监视器矩形（窗口模式用客户区原点，ScreenToClient 处理）
            if (_opts.Mode == RecordMode.Window)
                _monitorRect = Rectangle.Empty;
            else
                _monitorRect = Interop.Win32Native.MonitorRect(_opts.MonitorHandle);

            // 等第一帧，确认画面可用
            var sw = Stopwatch.StartNew();
            while (!_capture.TryReadFrame())
            {
                if (sw.ElapsedMilliseconds > 3000)
                    throw new InvalidOperationException("无法获取屏幕画面（可能显示器不支持捕获）。");
                Thread.Sleep(10);
            }

            if (_opts.WebcamEnabled)
            {
                if (_sharedWebcam != null)
                    _webcam = _sharedWebcam;
                else
                {
                    _webcam = new WebcamCapture();
                    try { _webcam.Start(string.IsNullOrWhiteSpace(_opts.WebcamDeviceId) ? null : _opts.WebcamDeviceId); }
                    catch (Exception ex)
                    {
                        _webcamWarning = "摄像头不可用，已跳过人像画中画：" + ex.Message;
                        try { _webcam?.Dispose(); } catch { }
                        _webcam = null;
                    }
                }
                // 若外部已提供共享实例，不释放（overlay 还在用）
                if (_sharedWebcam == null)
                    _ownsWebcam = true;
            }

            int outW = _cropped ? _crop.Width : size.Width;
            int outH = _cropped ? _crop.Height : size.Height;
            // 区域裁剪或需要叠点击/摄像头时，使用输出尺寸工作缓冲
            if (_cropped || _clickEngine != null || _webcam != null)
                _composeBuffer = new byte[outW * outH * 4];

            var kind = _opts.Encoder == EncoderKind.Auto
                ? HardwareEncoderDetector.Detect(_ffmpegPath)
                : _opts.Encoder;
            _encoderUsed = kind;
            var settings = new EncoderSettings(
                kind,
                _opts.Fps,
                _opts.Quality,
                outW,
                outH);

            try
            {
                _encoder = CreateEncoder(settings);
            }
            catch (Exception) when (kind != EncoderKind.SoftwareX264)
            {
                // 硬件编码器不可用（驱动/权限等），自动回退软件编码
                _encoder?.Dispose();
                settings = settings with { Encoder = EncoderKind.SoftwareX264 };
                _encoderUsed = EncoderKind.SoftwareX264;
                _encoder = CreateEncoder(settings);
            }
            _writer = new FrameWriterQueue(_encoder);
            _writer.Start();

            IsRecording = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "sr-capture-loop" };
            _loop.Start();

            if (_opts.RecordAudio || _opts.RecordSystemAudio)
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), "QSrcRecorder");
                Directory.CreateDirectory(tempFolder);
                if (_opts.RecordAudio)
                {
                    _audioCapture = new AudioCapture(tempFolder);
                    _audioWavePath = _audioCapture.WavePath;
                    try { _audioCapture.Start(); }
                    catch (Exception ex) { _error = "音频设备不可用，继续纯视频录制：" + ex.Message; }
                }
                if (_opts.RecordSystemAudio)
                {
                    _systemAudioCapture = new SystemAudioCapture(tempFolder);
                    _systemAudioWavePath = _systemAudioCapture.WavePath;
                    try { _systemAudioCapture.Start(); }
                    catch (Exception ex) { _error = "系统声音设备不可用，继续纯视频录制：" + ex.Message; }
                }
            }
        }
        catch
        {
            if (_webcam != null)
            {
                _webcam.Stop();
                if (_ownsWebcam)
                    try { _webcam.Dispose(); } catch { }
                _webcam = null;
            }
            _capture?.Dispose();
            _capture = null;
            _d3d.Dispose();
            _d3d = null!;
            throw;
        }
    }

    private FfmpegVideoEncoder CreateEncoder(EncoderSettings settings)
    {
        var encoder = new FfmpegVideoEncoder(
            _ffmpegPath,
            FfmpegArgumentBuilder.BuildArgs(settings, _outputPath),
            _outputPath);
        encoder.Start();
        return encoder;
    }

    private void Loop()
    {
        var capture = _capture!;
        var srcBuffer = capture.FrameBuffer;
        long interval = Stopwatch.Frequency / _opts.Fps;
        long next = Stopwatch.GetTimestamp();
        int outW = _cropped ? _crop.Width : _fullWidth;
        int outH = _cropped ? _crop.Height : _fullHeight;
        bool needCompose = _composeBuffer != null;
        var pipCorner = PipCompositor.ParseCorner(_opts.WebcamCorner);
        IntPtr hwnd = _opts.WindowHandle;

        try
        {
            while (!_stop)
            {
                if (_paused)
                {
                    Thread.Sleep(30);
                    next = Stopwatch.GetTimestamp();
                    continue;
                }

                if (_writer!.WriteError is string we)
                {
                    _error = we;
                    break;
                }

                long t0 = Stopwatch.GetTimestamp();
                capture.TryReadFrame(); // 无新帧则复用上一帧内容
                _captureMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                byte[] frameToEncode;
                if (needCompose)
                {
                    var dest = _composeBuffer!;
                    if (_cropped)
                        PipCompositor.CopyCrop(srcBuffer, _fullWidth, _fullHeight, _crop, dest);
                    else
                        Buffer.BlockCopy(srcBuffer, 0, dest, 0, outW * outH * 4);

                    DrawClickOverlays(dest, outW, outH, hwnd);
                    DrawWebcamPip(dest, outW, outH, pipCorner);
                    frameToEncode = dest;
                }
                else
                {
                    frameToEncode = srcBuffer;
                }

                _writer!.EnqueueFull(frameToEncode);
                _framesWritten++;

                if (capture.SizeChanged)
                {
                    _stopReason = "画面尺寸已变化（窗口大小改变或分辨率切换），录制已自动结束。";
                    break;
                }

                next += interval;
                long wait = next - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    int ms = (int)(wait * 1000 / Stopwatch.Frequency);
                    if (ms > 1)
                        Thread.Sleep(ms - 1);
                    while (Stopwatch.GetTimestamp() < next)
                        Thread.SpinWait(64);
                }
                else if (wait < -Stopwatch.Frequency)
                {
                    next = Stopwatch.GetTimestamp(); // 落后超过 1 秒，重新对齐（等效丢帧）
                }
            }
        }
        catch (Exception ex)
        {
            _error = "录制中途出错：" + ex.Message;
        }

        FinishLoop();
    }

    private void DrawClickOverlays(byte[] frame, int width, int height, IntPtr hwnd)
    {
        if (_clickEngine == null)
            return;

        long now = Environment.TickCount64;
        var clicks = _clickEngine.GetActiveClicks(now);
        if (clicks.Count == 0)
            return;

        int pitch = width * 4;
        Rectangle? cropForMap = _cropped ? _crop : null;
        Func<int, int, (int X, int Y)>? toClient = null;
        if (_opts.Mode == RecordMode.Window && hwnd != IntPtr.Zero)
        {
            toClient = (sx, sy) =>
            {
                var pt = new Interop.Win32Native.POINT { X = sx, Y = sy };
                _ = Interop.Win32Native.ScreenToClient(hwnd, ref pt);
                return (pt.X, pt.Y);
            };
        }

        foreach (var (sx, sy, t) in clicks)
        {
            var (bigA, rippleR, rippleA) = Overlays.ClickHighlightEngine.Animate(t, now);
            if (bigA <= 0 && rippleA <= 0)
                continue;

            var (fx, fy) = Overlays.ClickHighlightEngine.ScreenToFrame(
                sx, sy, _opts.Mode, _monitorRect, _opts.Scale, cropForMap, toClient);

            if (bigA > 0)
            {
                Overlays.ClickHighlightEngine.DrawCircleFill(
                    frame, width, height, pitch,
                    fx, fy, Overlays.ClickHighlightEngine.BigRadius,
                    Math.Max(1, bigA / 3), bigA, _clickB, _clickG, _clickR);
            }
            if (rippleA > 0 && rippleR > 0)
            {
                Overlays.ClickHighlightEngine.DrawCircleRing(
                    frame, width, height, pitch,
                    fx, fy, rippleR, rippleA, _clickB, _clickG, _clickR);
            }
        }
    }

    private void DrawWebcamPip(byte[] frame, int width, int height, PipCorner corner)
    {
        if (_webcam == null)
            return;
        if (!_webcam.TryCopyLatestFrame(out var cam, out int cw, out int ch) || cw < 1 || ch < 1)
            return;

        Rectangle rect;
        if (_opts.WebcamCustomPosition)
            rect = PipCompositor.ComputeCustomRect(width, height,
                _opts.WebcamPosX, _opts.WebcamPosY, _opts.WebcamSizeW, _opts.WebcamSizeH);
        else
            rect = PipCompositor.ComputeRect(width, height, corner, _opts.WebcamSizeIndex,
                marginPx: 12, sourceW: cw, sourceH: ch);

        if (rect.Width < 2 || rect.Height < 2)
            return;

        // 把实时摄像头帧写进输出缓冲前，先校验像素内容（防止 NV12/BGRA 误判导致全黑/偏色）
        if (IsFramePlausible(cam, cw, ch))
            PipCompositor.Blit(frame, width, height, cam, cw, ch, rect, _opts.WebcamMirror, drawBorder: true);
    }

    /// <summary>粗略校验一帧是否包含合理像素内容（非全黑/全白/异常纯色）。</summary>
    private static bool IsFramePlausible(byte[] bgra, int w, int h)
    {
        if (bgra == null || bgra.Length < w * h * 4)
            return false;
        // 采样四个角 + 中心，至少有一个通道 > 16
        int[] offsetsX = [0, w - 1, 0, w - 1, w / 2];
        int[] offsetsY = [0, 0, h - 1, h - 1, h / 2];
        for (int k = 0; k < offsetsX.Length; k++)
        {
            int x = offsetsX[k], y = offsetsY[k];
            int i = (y * w + x) * 4;
            if (bgra[i] > 16 || bgra[i + 1] > 16 || bgra[i + 2] > 16)
                return true;
        }
        return false;
    }

    private void FinishLoop()
    {
        bool encodeOk = false;
        try
        {
            _writer?.Complete();
            encodeOk = _encoder!.Finish();
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
        if (_writer?.WriteError is string writerError)
            _error ??= writerError;
        if (!encodeOk && _error == null)
            _error = "ffmpeg 编码失败：" + _encoder!.ErrorTail;

        // 视频编码完成但输出无有效视频流（典型：双显卡机器上 NVENC 间歇性失败，
        // 但 ffmpeg 进程仍以退出码 0 结束）：必须视为编码失败，否则合成阶段
        // 会用"只有音频"的结果覆盖掉唯一有效的视频
        if (encodeOk && (!File.Exists(_outputPath)
            || new FileInfo(_outputPath).Length < 1000
            || !HasVideoStream(_outputPath)))
        {
            encodeOk = false;
            _error = "视频编码输出无效（无视频流），将自动改用软件编码重录";
        }

        if (!encodeOk)
            _encoder?.DumpDiagnostics();

        double writeMs = _writer?.WriteMs ?? 0;
        long dropped = _writer?.DroppedFrames ?? 0;
        _writer?.Dispose();
        _writer = null;
        _capture?.Dispose();
        _capture = null;
        _d3d?.Dispose();
        _d3d = null;
        _encoder?.Dispose();
        _encoder = null;

        // 停止音频 / 摄像头（仅内部创建的才 dispose）
        _audioCapture?.Stop(); _audioCapture?.Dispose(); _audioCapture = null;
        _systemAudioCapture?.Stop(); _systemAudioCapture?.Dispose(); _systemAudioCapture = null;
        if (_webcam != null)
        {
            _webcam.Stop();
            if (_ownsWebcam)
                _webcam.Dispose();
            _webcam = null;
        }
        _composeBuffer = null;

        if (encodeOk && AudioHasContent())
        {
            // 音频通道有效性：文件存在且大于 100 字节（WAV 头 44 字节 + 少量数据），
            // 避免设备异常时留下 46 字节的空文件导致 ffmpeg 合成失败
            bool hasMic = !string.IsNullOrEmpty(_audioWavePath) && File.Exists(_audioWavePath)
                && new FileInfo(_audioWavePath!).Length > 100;
            bool hasSys = !string.IsNullOrEmpty(_systemAudioWavePath) && File.Exists(_systemAudioWavePath)
                && new FileInfo(_systemAudioWavePath!).Length > 100;

            // 视频源必须有效（存在、够大、含视频流），否则跳过合成保留原视频
            bool videoValid = File.Exists(_outputPath)
                && new FileInfo(_outputPath).Length > 1000
                && HasVideoStream(_outputPath);

            if (!videoValid)
            {
                _audioMuxWarning = "视频文件无效，已跳过音频合成";
            }
            else
            {
                _tempMuxPath = Path.Combine(Path.GetTempPath(), $"qsrc_mux_{Guid.NewGuid():N}.mp4");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath, UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardError = true,
                    };
                    string sysFilter = BuildSysAudioFilter();
                    string micFilter = BuildMicAudioFilter();

                    if (hasMic && hasSys)
                    {
                        var args = new[] {
                            "-y", "-hide_banner", "-loglevel", "error",
                            "-i", _outputPath, "-i", _systemAudioWavePath, "-i", _audioWavePath,
                            "-c:v", "copy",
                            "-filter_complex", BuildDualChannelFilterComplex(sysFilter, micFilter),
                            "-map", "0:v:0", "-map", "[aout]",
                            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", _tempMuxPath };
                        foreach (var a in args) psi.ArgumentList.Add(a);
                    }
                    else if (hasSys)
                    {
                        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", _outputPath, "-i", _systemAudioWavePath, "-c:v", "copy" };
                        // 单通道滤镜必须带 [1:a] 输入标记（输入 1 = 系统声音），
                        // 并且要同时 -map 0:v:0（视频）和 [aout]（音频），否则输出只含一轨
                        if (!string.IsNullOrEmpty(sysFilter)) args.AddRange(new[] { "-filter_complex", "[1:a]" + sysFilter + "[aout]", "-map", "0:v:0", "-map", "[aout]" });
                        else args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
                        args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", _tempMuxPath });
                        foreach (var a in args) psi.ArgumentList.Add(a);
                    }
                    else
                    {
                        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", _outputPath, "-i", _audioWavePath, "-c:v", "copy" };
                        // 单通道滤镜必须带 [1:a] 输入标记（输入 1 = 麦克风），
                        // 并且要同时 -map 0:v:0（视频）和 [aout]（音频），否则输出只含一轨
                        if (!string.IsNullOrEmpty(micFilter)) args.AddRange(new[] { "-filter_complex", "[1:a]" + micFilter + "[aout]", "-map", "0:v:0", "-map", "[aout]" });
                        else args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
                        args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", _tempMuxPath });
                        foreach (var a in args) psi.ArgumentList.Add(a);
                    }

                    var proc = Process.Start(psi)!;
                    _ = proc.StandardError.ReadToEnd();
                    bool muxOk = proc.WaitForExit(30000) && proc.ExitCode == 0;
                    if (!muxOk)
                    {
                        proc.Kill(true);
                        _audioMuxWarning = "音频合成失败，视频已保存（不含音频）";
                    }
                    else
                    {
                        // 关键防御：mux 输出必须同时含视频流和音频流才替换原视频，
                        // 防止 ffmpeg 在视频源异常时只输出音频、覆盖掉唯一有效的视频
                        if (HasVideoStream(_tempMuxPath) && HasAudioStream(_tempMuxPath))
                        {
                            try
                            {
                                if (File.Exists(_outputPath)) File.Delete(_outputPath);
                                File.Move(_tempMuxPath, _outputPath);
                            }
                            catch (Exception ex) { _audioMuxWarning = "音频合成后替换文件失败：" + ex.Message; }
                        }
                        else
                        {
                            _audioMuxWarning = "音频合成输出异常，已保留原视频（不含音频）";
                        }
                    }
                }
                catch (Exception ex) { _audioMuxWarning = "音频合成出错：" + ex.Message; }
                finally
                {
                    try { if (!string.IsNullOrEmpty(_audioWavePath) && File.Exists(_audioWavePath)) File.Delete(_audioWavePath); } catch { }
                    _audioWavePath = null;
                    try { if (!string.IsNullOrEmpty(_systemAudioWavePath) && File.Exists(_systemAudioWavePath)) File.Delete(_systemAudioWavePath); } catch { }
                    _systemAudioWavePath = null;
                    try { if (!string.IsNullOrEmpty(_tempMuxPath) && File.Exists(_tempMuxPath)) File.Delete(_tempMuxPath); } catch { }
                }
            }
        }

        IsRecording = false;

        var result = new RecordingResult
        {
            OutputPath = _outputPath,
            FrameCount = _framesWritten,
            Duration = RecordedDuration,
            StopReason = _stopReason,
            Error = _error,
            AudioWarning = _audioMuxWarning,
            WebcamWarning = _webcamWarning,
            Success = encodeOk && _error == null && File.Exists(_outputPath),
            EncoderUsed = _encoderUsed,
            CaptureMs = _captureMs,
            WriteMs = writeMs,
            DroppedFrames = dropped,
        };

        // 失败时清掉没有内容的残留文件，避免留下打不开的 mp4
        if (!result.Success)
        {
            try { if (File.Exists(_outputPath)) File.Delete(_outputPath); } catch { }
        }

        Completed?.Invoke(result);
    }

    private bool AudioHasContent() =>
        (!string.IsNullOrEmpty(_audioWavePath) && File.Exists(_audioWavePath ?? ""))
        || (!string.IsNullOrEmpty(_systemAudioWavePath) && File.Exists(_systemAudioWavePath ?? ""));

    /// <summary>用 ffmpeg 探测文件是否含视频流（避免把只有音频的结果当作成功）。</summary>
    private bool HasVideoStream(string path) => ProbeHasStream(path, "Video:");

    /// <summary>用 ffmpeg 探测文件是否含音频流。</summary>
    private bool HasAudioStream(string path) => ProbeHasStream(path, "Audio:");

    private bool ProbeHasStream(string path, string streamKind)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-hide_banner");
            var p = Process.Start(psi);
            if (p == null) return false;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(3000);
            return err.Contains("Stream #") && err.Contains(streamKind);
        }
        catch
        {
            return false;
        }
    }

    private string BuildSysAudioFilter()
    {
        // 系统声音为设备原生格式（可能 48000Hz float），统一转换到 44100Hz 16bit 立体声
        // 以便与麦克风 amix 混音（amix 要求输入格式一致）
        var parts = new List<string> { "aformat=channel_layouts=stereo:sample_fmts=s16:sample_rates=44100" };
        if (Math.Abs(_opts.SysVolume - 1.0) > 0.01) parts.Add($"volume={_opts.SysVolume:N3}");
        if (_opts.SysBass != 0) parts.Add($"bass=g={_opts.SysBass}:f=100:w=0.7");
        if (_opts.SysTreble != 0) parts.Add($"treble=g={_opts.SysTreble}:f=3000:w=0.7");
        return string.Join(",", parts);
    }

    private string BuildMicAudioFilter()
    {
        // 麦克风为设备原生格式（可能 48000Hz float），统一转换到 44100Hz 16bit 立体声
        var parts = new List<string> { "aformat=channel_layouts=stereo:sample_fmts=s16:sample_rates=44100" };
        if (Math.Abs(_opts.MicVolume - 1.0) > 0.01) parts.Add($"volume={_opts.MicVolume:N3}");
        if (_opts.MicNoiseGate)
            // 阈值压到 -60dB：只过滤绝对静音，避免把正常说话（尤其音量偏小）当噪声关掉。
            // attack 从 200ms 缩短到 20ms，避免吞掉语音开头的辅音。
            parts.Add("agate=threshold=0.001:attack=20:release=400");
        return string.Join(",", parts);
    }

    private string BuildDualChannelFilterComplex(string sysFilter, string micFilter)
    {
        // 滤镜可能为空（未启用音效）；ffmpeg 不接受空滤镜名，用 anull 占位保证语法合法
        string sysPart = string.IsNullOrEmpty(sysFilter) ? "[1:a]anull[sys]" : $"[1:a]{sysFilter}[sys]";
        string micPart = string.IsNullOrEmpty(micFilter) ? "[2:a]anull[mic]" : $"[2:a]{micFilter}[mic]";
        return $"{sysPart};{micPart};[sys][mic]amix=inputs=2:duration=shortest:dropout_transition=2[aout]";
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void Stop()
    {
        if (!IsRecording)
            return;
        _stop = true;
        _loop?.Join(20000);
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
    }
}
