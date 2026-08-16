using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ScreenRecorder.Encoding;

/// <summary>
/// 管理 ffmpeg 子进程：原始 BGRA 帧从 stdin 管道送入，编码为 MP4。
/// 任何异常（含 ffmpeg 提前退出导致的断管）都会以带 stderr 信息的异常抛出，绝不让进程崩溃。
/// </summary>
public sealed class FfmpegVideoEncoder : IDisposable
{
    private const int StderrKeepLines = 400;

    private readonly string _ffmpegPath;
    private readonly string[] _args;
    private readonly Queue<string> _stderrLines = new();
    private readonly object _stderrLock = new();
    private Process? _proc;

    public string OutputPath { get; }
    public bool Failed { get; private set; }

    public string ErrorTail
    {
        get
        {
            lock (_stderrLock)
                return string.Join("\n", _stderrLines);
        }
    }

    public FfmpegVideoEncoder(string ffmpegPath, string[] args, string outputPath)
    {
        _ffmpegPath = ffmpegPath;
        _args = args;
        OutputPath = outputPath;
    }

    /// <summary>查找 ffmpeg.exe：程序目录 → 逐级向上的 tools/ffmpeg → PATH。</summary>
    public static string LocateFfmpeg()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside))
            return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent!)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string dir_ in pathVar.Split(';'))
            {
                try
                {
                    string candidate = Path.Combine(dir_.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // 非法路径字符，忽略
                }
            }
        }

        throw new InvalidOperationException(
            "找不到 ffmpeg.exe。请把它放在程序目录，或项目根目录的 tools/ffmpeg 下，或加入 PATH。");
    }

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in _args)
            psi.ArgumentList.Add(arg);

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 ffmpeg 进程。");

        // 编码是后台批处理活：低于正常优先级，避免软编吃满 CPU 时卡住界面
        try { _proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            lock (_stderrLock)
            {
                _stderrLines.Enqueue(e.Data);
                while (_stderrLines.Count > StderrKeepLines)
                    _stderrLines.Dequeue();
            }
        };
        _proc.BeginErrorReadLine();

        // 立刻失败（参数错误/驱动拒绝）时提前暴露，而不是等到断管
        if (_proc.WaitForExit(300))
        {
            Failed = true;
            throw new InvalidOperationException(
                $"ffmpeg 启动即退出（退出码 {_proc.ExitCode}）：{ErrorTail}");
        }
    }

    public bool HasExited => _proc?.HasExited ?? true;

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_proc == null)
            throw new InvalidOperationException("编码器尚未启动。");
        if (_proc.HasExited)
        {
            Failed = true;
            throw new IOException($"ffmpeg 已退出（退出码 {_proc.ExitCode}），无法继续写入。{CollectStderr()}");
        }

        try
        {
            var stdin = _proc.StandardInput.BaseStream;
            stdin.Write(buffer, offset, count);
            stdin.Flush();
        }
        catch (IOException ex)
        {
            Failed = true;
            throw new IOException($"ffmpeg 中途退出，写入管道失败：{CollectStderr()}", ex);
        }
    }

    /// <summary>等待进程收尾并收集 stderr，用于拼进异常信息。</summary>
    private string CollectStderr()
    {
        try
        {
            if (!_proc!.WaitForExit(3000))
            {
                try { _proc.Kill(true); } catch { }
            }
        }
        catch { }
        string tail = ErrorTail;
        return string.IsNullOrWhiteSpace(tail) ? "（ffmpeg 未输出错误信息）" : "\nffmpeg 输出：\n" + tail;
    }

    /// <summary>结束编码：关闭管道，等待 ffmpeg 收尾（faststart 需要）。</summary>
    public bool Finish(int timeoutMs = 30000)
    {
        if (_proc == null || Failed)
            return false;
        try
        {
            _proc.StandardInput.Close();
        }
        catch
        {
            // 进程可能已退出
        }

        if (!_proc.WaitForExit(timeoutMs))
        {
            try { _proc.Kill(true); } catch { }
            Failed = true;
            return false;
        }
        try { _proc.WaitForExit(); } catch { } // 等异步输出收尾
        if (_proc.ExitCode != 0)
            Failed = true;
        return _proc.ExitCode == 0;
    }

    /// <summary>失败时把完整命令行与 stderr 落盘，便于排查。</summary>
    public void DumpDiagnostics()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QSrcRecorder", "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"failed_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
            File.WriteAllText(file,
                "时间: " + DateTime.Now + "\n" +
                "命令: " + _ffmpegPath + " " + string.Join(" ", _args) + "\n\n" +
                "stderr:\n" + ErrorTail + "\n");
        }
        catch
        {
            // 诊断失败不影响主流程
        }
    }

    public void Dispose()
    {
        if (_proc != null)
        {
            try
            {
                if (!_proc.HasExited)
                    _proc.Kill(true);
            }
            catch { }
            _proc.Dispose();
            _proc = null;
        }
    }
}
