using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Interop;

/// <summary>零散的 Win32 原生函数（窗口枚举、热键、DWM 属性等）。</summary>
public static partial class Win32Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_EXCLUDED_FROM_CAPTURE = 33;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int WM_HOTKEY = 0x0312;
    public const uint VK_F9 = 0x78;
    public const uint VK_F10 = 0x79;

    public sealed record WinWindowInfo(IntPtr Hwnd, string Title, string ProcessName);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromRect(ref RECT lpRect, uint dwFlags);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>枚举当前桌面上可选择的顶层窗口（Alt-Tab 风格过滤）。</summary>
    public static List<WinWindowInfo> EnumerateWindows()
    {
        var result = new List<WinWindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0)
                return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title))
                return true;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return true;

            // 过滤被挂起的 UWP 窗口（如最小化到任务栏的商店应用）
            int cloaked = 0;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ref cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string procName = "";
            try { procName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

            result.Add(new WinWindowInfo(hwnd, title, procName));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>让窗口不出现在任何屏幕捕获中（录制悬浮条使用，避免被录进画面）。</summary>
    public static void ExcludeFromCapture(IntPtr hwnd)
    {
        int v = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_CAPTURE, ref v, sizeof(int));
    }

    public static IntPtr HMonitorFromRectangle(System.Drawing.Rectangle bounds)
    {
        var r = new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        return MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
    }
}
