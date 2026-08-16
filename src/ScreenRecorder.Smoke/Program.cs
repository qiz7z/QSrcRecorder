using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenRecorder;
using ScreenRecorder.Capture;
using ScreenRecorder.Encoding;
using ScreenRecorder.Interop;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

// 端到端冒烟测试：录制主显示器 3 秒（含 0.7 秒暂停）→ 验证 MP4 生成与时长。
// 用法：dotnet run --project src/ScreenRecorder.Smoke [-c Release] [--probe]

Console.WriteLine("== 屏幕录制主链路冒烟测试 ==");

if (args.Contains("--probe"))
    return Probe();

if (args.Contains("--killffmpeg"))
    return RunKillFfmpegResilience();

if (args.Contains("--timer"))
    return RunTimerDiag();

if (args.Contains("--nvencfail"))
    return RunNvencFailAndRetry();

return RunSmoke();

// 诊断探针：检查帧 Surface 的真实类型与各条互操作路径
static int Probe()
{
    var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    IntPtr hmon = Win32Native.HMonitorFromRectangle(screen.Bounds);

    using var d3d = new D3DContext();
    Console.WriteLine($"D3D 设备创建 OK, WinrtDevice={d3d.WinrtDevice != null}");

    var item = WgcInterop.CreateItemForMonitor(hmon);
    Console.WriteLine($"Item 创建 OK, Size={item.Size.Width}x{item.Size.Height}");

    var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
        d3d.WinrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
    var session = pool.CreateCaptureSession(item);
    session.StartCapture();
    Thread.Sleep(500);

    var frame = pool.TryGetNextFrame();
    Console.WriteLine($"frame null? {frame == null}");
    if (frame == null)
        return 1;

    var surf = frame.Surface;
    Console.WriteLine($"surface CLR 类型: {surf.GetType().FullName}");
    Console.WriteLine($"surface is IWinRTObject: {surf is WinRT.IWinRTObject}");

    Guid iidAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    Guid iidUnknown = new("00000000-0000-0000-C000-000000000046");

    if (surf is WinRT.IWinRTObject o)
    {
        int hr = Marshal.QueryInterface(o.NativeObject.ThisPtr, ref iidAccess, out _);
        Console.WriteLine($"QI(ThisPtr, DxgiAccess)  hr=0x{hr:X8}");
        int hrU = Marshal.QueryInterface(o.NativeObject.ThisPtr, ref iidUnknown, out _);
        Console.WriteLine($"QI(ThisPtr, IUnknown)   hr=0x{hrU:X8}");
    }

    IntPtr unk = Marshal.GetIUnknownForObject(surf);
    int hr2 = Marshal.QueryInterface(unk, ref iidAccess, out _);
    Console.WriteLine($"QI(RCW, DxgiAccess)      hr=0x{hr2:X8}");
    Marshal.Release(unk);

    return 0;
}

// 诊断：真实会话 + 真实悬浮条，观察时长与标签是否更新
static int RunTimerDiag()
{
    var ui = new Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.Run(new TimerDiagForm());
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    return 0;
}

// 复现用户故障：双显卡机器上 NVENC 间歇性 CUDA_ERROR_NO_DEVICE。
// 用 CUDA_VISIBLE_DEVICES=-1 确定性复现，验证：探测仍通过 → 真录失败 →
// 判定为“起步即编码器失败” → 换软件编码重录成功（对应 UI 的自动重试路径）。
static int RunNvencFailAndRetry()
{
    Console.WriteLine("-- 场景：NVENC 初始化失败 → 自动降级软件编码重录 --");
    string ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();

    var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    IntPtr hmon = Win32Native.HMonitorFromRectangle(screen.Bounds);

    RecordingOptions Opts(EncoderKind kind) => new()
    {
        Mode = RecordMode.FullScreen,
        MonitorHandle = hmon,
        Fps = 30,
        Quality = QualityPreset.Medium,
        Encoder = kind,
        Scale = 0.5,
        OutputFolder = Path.GetTempPath(),
    };

    RecordingResult RunOnce(RecordingOptions opts)
    {
        RecordingResult? result = null;
        using var session = new RecordingSession(opts, ffmpeg);
        session.Completed += r => result = r;
        session.Start();
        Thread.Sleep(2500);
        session.Stop();
        return result!;
    }

    // 1) 正常环境下探测 → Nvenc（模拟用户机器上探测通过）
    var detected = HardwareEncoderDetector.Detect(ffmpeg);
    Console.WriteLine($"探测到的编码器: {detected}");
    if (detected == EncoderKind.SoftwareX264)
    {
        Console.WriteLine("本机探测不到硬件编码器，直接以软编路径通过。");
        return 0;
    }

    // 2) 下毒：让子进程 ffmpeg 的 CUDA 找不到任何设备（精确复现用户报错）
    Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "-1");
    Console.WriteLine("已设置 CUDA_VISIBLE_DEVICES=-1（复现 CUDA_ERROR_NO_DEVICE）");

    var r1 = RunOnce(Opts(detected));
    Console.WriteLine($"第一次录制: Success={r1.Success}  EncoderUsed={r1.EncoderUsed}  帧数={r1.FrameCount}  " +
                      $"IsEarlyEncoderFailure={r1.IsEarlyEncoderFailure}");
    if (r1.Success)
    {
        Console.WriteLine("✗ 异常：预期失败但成功了（毒化未生效）");
        return 1;
    }
    if (!r1.IsEarlyEncoderFailure)
    {
        Console.WriteLine("✗ 异常：应判定为起步即编码器失败");
        return 1;
    }

    // 3) 模拟 UI 自动重试：软编重录
    var r2 = RunOnce(Opts(EncoderKind.SoftwareX264));
    Console.WriteLine($"软编重录: Success={r2.Success}  时长={r2.Duration:mm\\:ss}  错误={r2.Error ?? "无"}");

    Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
    return r2.Success ? 0 : 1;
}

// 回归测试：录制中途强杀 ffmpeg，程序必须体面报错而不是崩溃
// （对应真实故障：写线程未捕获“管道已结束”导致整个进程崩溃）
static int RunKillFfmpegResilience()
{
    Console.WriteLine("-- 场景：录制中途杀死 ffmpeg，验证程序存活并报错 --");
    string ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();
    var encoder = HardwareEncoderDetector.Detect(ffmpeg);

    var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    IntPtr hmon = Win32Native.HMonitorFromRectangle(screen.Bounds);

    var opts = new RecordingOptions
    {
        Mode = RecordMode.FullScreen,
        MonitorHandle = hmon,
        Fps = 30,
        Quality = QualityPreset.Medium,
        Encoder = encoder,
        Scale = 0.5,
        OutputFolder = Path.GetTempPath(),
    };

    RecordingResult? result = null;
    Exception? startError = null;
    var session = new RecordingSession(opts, ffmpeg);
    session.Completed += r => result = r;
    try
    {
        session.Start();
    }
    catch (Exception ex)
    {
        startError = ex;
    }

    if (startError != null)
    {
        Console.WriteLine($"启动失败（不符合预期）: {startError.Message}");
        session.Dispose();
        return 1;
    }

    Thread.Sleep(1200);
    Console.WriteLine(">> 强杀 ffmpeg 进程…");
    foreach (var p in Process.GetProcessesByName("ffmpeg"))
    {
        try { p.Kill(); } catch { }
    }

    var done = new AutoResetEvent(false);
    session.Completed += _ => done.Set();
    bool finished = done.WaitOne(20000);
    session.Dispose();

    Console.WriteLine($"会话结束: {(result != null ? "是" : "否")}");
    if (result != null)
    {
        Console.WriteLine($"Success={result.Success} Error={(result.Error ?? "无").Split('\n')[0]}");
        Console.WriteLine(result.Success
            ? "异常：ffmpeg 被杀后居然还 Success=True"
            : "✓ 程序存活、错误被捕获并上报（未崩溃）");
        return result.Success ? 1 : 0;
    }
    Console.WriteLine(finished ? "结果缺失" : "20 秒内未收到完成事件");
    return 1;
}

static int RunSmoke()
{
    string ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();
    Console.WriteLine($"ffmpeg: {ffmpeg}");
    double scale = Environment.GetEnvironmentVariable("SR_SCALE") is string s && double.TryParse(s, out var v) ? v : 1.0;
    Console.WriteLine($"缩放: {scale}");

    var encoder = HardwareEncoderDetector.Detect(ffmpeg);
    Console.WriteLine($"探测到的编码器: {encoder}");

    var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    IntPtr hmon = Win32Native.HMonitorFromRectangle(screen.Bounds);
    Console.WriteLine($"目标显示器: {screen.Bounds.Width}x{screen.Bounds.Height}");

    var opts = new RecordingOptions
    {
        Mode = RecordMode.FullScreen,
        MonitorHandle = hmon,
        Fps = 30,
        Quality = QualityPreset.Medium,
        Encoder = encoder,
        Scale = scale,
        OutputFolder = Environment.GetEnvironmentVariable("SR_OUT") ?? Path.GetTempPath(),
    };

    RecordingResult? result = null;
    using (var session = new RecordingSession(opts, ffmpeg))
    {
        session.Completed += r => result = r;
        session.Start();
        Console.WriteLine("录制中（3 秒，其中 0.7 秒暂停）…");
        Thread.Sleep(2300);
        session.Pause();
        Thread.Sleep(700);
        session.Stop();
    }

    Console.WriteLine();
    if (result == null)
    {
        Console.WriteLine("失败：会话未产生结果。");
        return 1;
    }

    Console.WriteLine($"Success={result.Success}  帧数={result.FrameCount}  时长={result.Duration:hh\\:mm\\:ss}  错误={result.Error ?? "无"}");
    Console.WriteLine($"诊断: 采集回读累计 {result.CaptureMs:F0}ms, 管道写入累计 {result.WriteMs:F0}ms, 丢帧 {result.DroppedFrames} (共 {result.FrameCount} 帧)");
    if (!result.Success)
        return 1;

    string produced = result.OutputPath;
    Console.WriteLine($"输出文件: {produced} ({new FileInfo(produced).Length / 1024 / 1024.0:F2} MB)");

    // 用 ffprobe 验证时长与编码器
    string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
    if (File.Exists(ffprobe))
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration:stream=codec_name,width,height,avg_frame_rate");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1");
        psi.ArgumentList.Add(produced);
        using var p = Process.Start(psi)!;
        string probeOut = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        Console.WriteLine("-- ffprobe --");
        Console.WriteLine(probeOut.Trim());

        foreach (var line in probeOut.Split('\n'))
        {
            if (line.StartsWith("duration=") &&
                double.TryParse(line["duration=".Length..].Trim(), out double dur))
            {
                // 一致性校验：成片时长应等于 实际写入帧数 / 帧率
                double expected = (result.FrameCount - result.DroppedFrames) / 30.0;
                bool ok = Math.Abs(dur - expected) < 0.35;
                Console.WriteLine(ok
                    ? $"时长校验通过（{dur:F2}s ≈ 写入 {result.FrameCount - result.DroppedFrames} 帧 / 30fps；丢帧率 {result.DroppedFrames * 100.0 / result.FrameCount:F0}%，受 GPU 频率波动影响）"
                    : $"时长校验失败：{dur:F2}s，预期 {expected:F2}s");
                return ok ? 0 : 1;
            }
        }
    }

    Console.WriteLine("冒烟测试完成（未找到 ffprobe，跳过时长校验）");
    return 0;
}

internal sealed class TimerDiagForm : Form
{
    private RecordingSession? _session;
    private ScreenRecorder.Overlays.RecordingBarForm? _bar;
    private readonly System.Windows.Forms.Timer _sample = new();
    private int _ticks;
    private readonly string _ffmpeg;
    private readonly EncoderKind _encoder;

    public TimerDiagForm()
    {
        ShowInTaskbar = false;
        Opacity = 0;
        _ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();
        _encoder = HardwareEncoderDetector.Detect(_ffmpeg);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var opts = new RecordingOptions
        {
            Mode = RecordMode.FullScreen,
            MonitorHandle = Win32Native.HMonitorFromRectangle(screen.Bounds),
            Fps = 30,
            Quality = QualityPreset.Medium,
            Encoder = _encoder,
            Scale = 0.5,
            OutputFolder = Path.GetTempPath(),
        };
        try
        {
            _session = new RecordingSession(opts, _ffmpeg);
            _session.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("会话启动失败: " + ex.Message);
            Close();
            return;
        }
        _bar = new ScreenRecorder.Overlays.RecordingBarForm(_session);
        _bar.Show();
        Console.WriteLine(">> 会话与悬浮条已启动，开始采样…");

        _sample.Interval = 500;
        _sample.Tick += (_, _) =>
        {
            _ticks++;
            var f = typeof(ScreenRecorder.Overlays.RecordingBarForm).GetField("_lblTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var label = f?.GetValue(_bar) as Label;
            var frames = _session!.GetType().GetField("_framesWritten",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_session);
            string clip = "";
            if (label != null)
            {
                var need = TextRenderer.MeasureText("00:00", label.Font).Width;
                clip = label.Width < need ? $"  ✗裁字(宽{label.Width}<需{need})" : $"  ✓无裁字(宽{label.Width}≥需{need})";
            }
            Console.WriteLine($"[{_ticks * 0.5:F1}s] 会话时长={_session.RecordedDuration:mm\\:ss\\.ff}  已写帧={frames}  悬浮条标签={label?.Text}{clip}");
            if (_ticks >= 8)
            {
                _sample.Stop();
                _session.Stop();
                _session.Dispose();
                _bar?.Close();
                Console.WriteLine(">> 采样结束");
                Close();
            }
        };
        _sample.Start();
    }
}
