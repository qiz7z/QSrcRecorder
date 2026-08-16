using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
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

    /// <summary>录制刚起步就因编码器失败（典型：双显卡机器上 NVENC 间歇不可用），适合自动降级重录。</summary>
    public bool IsEarlyEncoderFailure =>
        !Success && Error != null && EncoderUsed != EncoderKind.SoftwareX264 && FrameCount <= 90;
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
    private D3DContext? _d3d;
    private WgcCapture? _capture;
    private FfmpegVideoEncoder? _encoder;
    private FrameWriterQueue? _writer;
    private Thread? _loop;
    private volatile bool _stop;
    private volatile bool _paused;
    private long _framesWritten;
    private int _fullWidth;
    private Rectangle _crop;
    private bool _cropped;
    private string _outputPath = "";
    private string? _stopReason;
    private string? _error;
    private double _captureMs;
    private double _writeMs;
    private EncoderKind _encoderUsed;

    public event Action<RecordingResult>? Completed;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public string OutputPath => _outputPath;
    public TimeSpan RecordedDuration => TimeSpan.FromSeconds(_framesWritten / (double)_opts.Fps);

    public RecordingSession(RecordingOptions opts, string ffmpegPath)
    {
        _opts = opts;
        _ffmpegPath = ffmpegPath;
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

            // 等第一帧，确认画面可用
            var sw = Stopwatch.StartNew();
            while (!_capture.TryReadFrame())
            {
                if (sw.ElapsedMilliseconds > 3000)
                    throw new InvalidOperationException("无法获取屏幕画面（可能显示器不支持捕获）。");
                Thread.Sleep(10);
            }

            var kind = _opts.Encoder == EncoderKind.Auto
                ? HardwareEncoderDetector.Detect(_ffmpegPath)
                : _opts.Encoder;
            _encoderUsed = kind;
            var settings = new EncoderSettings(
                kind,
                _opts.Fps,
                _opts.Quality,
                _cropped ? _crop.Width : size.Width,
                _cropped ? _crop.Height : size.Height);

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
        }
        catch
        {
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
        var buffer = capture.FrameBuffer;
        long interval = Stopwatch.Frequency / _opts.Fps;
        long next = Stopwatch.GetTimestamp();

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

                if (_cropped)
                {
                    _writer!.EnqueueCropped(buffer, _crop.X, _crop.Y, _crop.Width, _crop.Height, _fullWidth);
                }
                else
                {
                    _writer!.EnqueueFull(buffer);
                }
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
        IsRecording = false;

        var result = new RecordingResult
        {
            OutputPath = _outputPath,
            FrameCount = _framesWritten,
            Duration = RecordedDuration,
            StopReason = _stopReason,
            Error = _error,
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
