using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenRecorder.Interop;
using ScreenRecorder.Settings;

namespace ScreenRecorder.Overlays;

/// <summary>
/// 鼠标点击高亮引擎：全局低层钩子捕获左键按下，把点击事件投递到采集线程，
/// 由 RecordingSession 在每帧合帧时绘制扩散光圈（软件合帧，窗口/区域/全屏都进画面）。
/// </summary>
public sealed class ClickHighlightEngine : IDisposable
{
    // ── 动画参数（点击高亮：大圆 + 圆内波纹，点一下波动一下） ──
    // 大圆固定半径，点击后出现并与波纹同步淡出；波纹从圆心向外扩散一次即结束
    public const double DurationMs = 450;          // 大圆总时长（与波纹几乎同步结束）
    public const double BigRadius = 40;            // 大圆半径
    public const int BigAlphaStart = 170;          // 大圆初始透明度
    public const double RippleDurationMs = 380;    // 波纹扩散一次时长
    public const double RippleStartRadius = 6;     // 波纹起始半径
    public const double RippleEndRadius = 34;      // 波纹最终半径（大圆内）
    public const int RippleAlphaStart = 230;       // 波纹初始透明度

    private readonly ConcurrentQueue<(int X, int Y, long TimeMs)> _clicks = new();
    private readonly List<(int X, int Y, long TimeMs)> _active = new(); // 采集线程独占
    private readonly AutoResetEvent _threadReady = new(false);
    private readonly object _lifeLock = new();
    private Win32Native.LowLevelMouseProc? _procRef; // 防止委托被 GC
    private IntPtr _hook = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile uint _threadIdStore;

    /// <summary>安装低层鼠标钩子并启动消息循环线程。</summary>
    public void Start()
    {
        lock (_lifeLock)
        {
            if (_thread != null)
                return;
            _stop = false;
            _thread = new Thread(HookThread) { IsBackground = true, Name = "click-highlight-hook" };
            _thread.Start();
            _threadReady.WaitOne(2000);
        }
    }

    /// <summary>卸载钩子并退出线程。</summary>
    public void Stop()
    {
        lock (_lifeLock)
        {
            if (_thread == null)
                return;
            _stop = true;
            var handle = _thread;
            _thread = null;
            if (handle != null && handle.IsAlive && _threadIdStore != 0)
            {
                _ = Win32Native.PostThreadMessage(_threadIdStore, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                handle.Join(1500);
            }
        }
    }

    private void HookThread()
    {
        _threadIdStore = Win32Native.GetCurrentThreadId();
        _procRef = OnLowLevelMouse;
        // WH_MOUSE_LL 全局钩子：hMod 必须为 NULL，过程由委托函数指针承载
        _hook = Win32Native.SetWindowsHookEx(Win32Native.WH_MOUSE_LL, _procRef, IntPtr.Zero, 0);

        _threadReady.Set();

        // 消息循环：低层钩子回调需要线程处理消息才能被分发
        while (!_stop)
        {
            if (!Win32Native.GetMessage(out var msg, IntPtr.Zero, 0, 0))
                break; // WM_QUIT
            _ = Win32Native.TranslateMessage(ref msg);
            _ = Win32Native.DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero)
        {
            _ = Win32Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Win32Native.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Win32Native.MSLLHOOKSTRUCT>(lParam);
            _clicks.Enqueue((info.pt.X, info.pt.Y, Environment.TickCount64));
        }
        return Win32Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 供采集线程每帧调用：吸收新点击，返回当前仍在动画期内的活跃点击。
    /// 注意：此方法必须只从采集线程调用（_active 非线程安全）。
    /// </summary>
    internal List<(int X, int Y, long TimeMs)> GetActiveClicks(long nowMs)
    {
        while (_clicks.TryDequeue(out var c))
            _active.Add(c);

        // 清理已结束动画的点击；其余保留，供后续帧继续绘制扩散动画
        _active.RemoveAll(c => nowMs - c.TimeMs >= (long)DurationMs);
        return _active;
    }

    public void Dispose() => Stop();

    // ═══════════ 纯逻辑（可单测） ═══════════

    /// <summary>解析 "#RRGGBB" → (B,G,R)；非法输入回退品牌红 #DC2626。</summary>
    public static (byte B, byte G, byte R) ParseColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 6 && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return ((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
        }
        return (0x26, 0x26, 0xDC); // #DC2626
    }

    /// <summary>
    /// 点击高亮动画（500ms）：返回 (大圆alpha, 波纹半径, 波纹alpha)。
    /// 大圆固定 40px 缓慢淡出；波纹从圆心向外扩散。超时返回 (0,0,0) 表示已结束。
    /// </summary>
    public static (int BigAlpha, double RippleRadius, int RippleAlpha) Animate(long clickTimeMs, long nowMs)
    {
        double elapsed = nowMs - clickTimeMs;
        if (elapsed < 0 || elapsed >= DurationMs)
            return (0, 0, 0);

        int bigAlpha = (int)(BigAlphaStart * (1.0 - elapsed / DurationMs));

        double rt = elapsed / RippleDurationMs;
        if (rt >= 1.0)
            return (bigAlpha, 0, 0);
        double rippleRadius = RippleStartRadius + (RippleEndRadius - RippleStartRadius) * rt;
        int rippleAlpha = (int)(RippleAlphaStart * (1.0 - rt));
        return (bigAlpha, rippleRadius, rippleAlpha);
    }

    /// <summary>
    /// 屏幕坐标 → 帧坐标。
    /// FullScreen/Region 以 monitorRect 为原点；Window 模式用 screenToClient 回调
    /// （实际调用 user32.ScreenToClient，测试可注入模拟）。缩放与裁剪随后应用。
    /// </summary>
    public static (int X, int Y) ScreenToFrame(
        int screenX, int screenY,
        RecordMode mode,
        System.Drawing.Rectangle monitorRect,
        double scale,
        System.Drawing.Rectangle? cropRect,
        Func<int, int, (int X, int Y)>? screenToClient = null)
    {
        int baseX, baseY;
        if (mode == RecordMode.Window && screenToClient != null)
        {
            (baseX, baseY) = screenToClient(screenX, screenY);
        }
        else
        {
            baseX = screenX - monitorRect.X;
            baseY = screenY - monitorRect.Y;
        }

        double sx = baseX * scale;
        double sy = baseY * scale;

        if (cropRect is { } crop)
        {
            sx -= crop.X;
            sy -= crop.Y;
        }
        return ((int)Math.Round(sx), (int)Math.Round(sy));
    }

    /// <summary>
    /// 在 BGRA 帧缓冲上画半透明填充圆（鼠标跟随高亮用）。
    /// 中心区低 alpha 填充，边缘环带高 alpha 描边，形成清晰的色斑。
    /// </summary>
    public static void DrawCircleFill(
        byte[] frame, int width, int height, int pitch,
        int cx, int cy, double radius, int fillAlpha, int edgeAlpha, byte b, byte g, byte r)
    {
        if (radius < 1 || fillAlpha <= 0)
            return;
        int ri = (int)Math.Round(radius);
        int minX = Math.Max(0, cx - ri), maxX = Math.Min(width - 1, cx + ri);
        int minY = Math.Max(0, cy - ri), maxY = Math.Min(height - 1, cy + ri);

        double inner = radius * 0.82;         // 边缘描边带
        double fillSq = inner * inner;
        double edgeSq = radius * radius;

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            int row = y * pitch;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - cx;
                double d2 = dx * dx + dy * dy;
                if (d2 > edgeSq)
                    continue;

                int i = row + x * 4;
                double a = (d2 >= fillSq ? edgeAlpha : fillAlpha) / 255.0;
                frame[i] = (byte)(b * a + frame[i] * (1.0 - a));       // B
                frame[i + 1] = (byte)(g * a + frame[i + 1] * (1.0 - a)); // G
                frame[i + 2] = (byte)(r * a + frame[i + 2] * (1.0 - a)); // R
            }
        }
    }

    /// <summary>
    /// 在 BGRA 帧缓冲上画圆环（中点画圆，像素级 alpha 混合）。
    /// 颜色 (B,G,R)；alpha 0-255。
    /// </summary>
    public static void DrawCircleRing(
        byte[] frame, int width, int height, int pitch,
        int cx, int cy, double radius, int alpha, byte b, byte g, byte r)
    {
        if (radius < 1 || alpha <= 0)
            return;
        int ri = (int)Math.Round(radius);
        int minX = Math.Max(0, cx - ri), maxX = Math.Min(width - 1, cx + ri);
        int minY = Math.Max(0, cy - ri), maxY = Math.Min(height - 1, cy + ri);

        // 环带：内半径 70% 到外半径，落在环带内的像素做 alpha 混合
        double inner = radius * 0.72;
        double outerSq = radius * radius;
        double innerSq = inner * inner;

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            int row = y * pitch;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - cx;
                double d2 = dx * dx + dy * dy;
                if (d2 < innerSq || d2 > outerSq)
                    continue;

                int i = row + x * 4;
                double a = alpha / 255.0;
                frame[i] = (byte)(b * a + frame[i] * (1.0 - a));       // B
                frame[i + 1] = (byte)(g * a + frame[i + 1] * (1.0 - a)); // G
                frame[i + 2] = (byte)(r * a + frame[i + 2] * (1.0 - a)); // R
            }
        }
    }
}
