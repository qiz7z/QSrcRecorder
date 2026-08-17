using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenRecorder.Overlays;

/// <summary>
/// 鼠标高亮覆盖层（WPF 版）：全屏透明置顶窗口，实时在鼠标位置绘制半透明色斑。
/// WPF 原生支持每像素 alpha（AllowsTransparency），无 UpdateLayeredWindow 的
/// 位图生命周期/残留问题；鼠标不动时不重绘，绝不闪烁。
/// </summary>
public sealed class MouseHighlightOverlay : System.Windows.Window
{
    private readonly DispatcherTimer _timer;
    private readonly Ellipse _fill;
    private readonly Ellipse _edge;
    private int _radius = 30;
    private System.Windows.Media.Color _color = System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26);
    private double _fillAlpha = 110 / 255.0;
    private double _edgeAlpha = 230 / 255.0;
    private System.Windows.Point _lastPos = new(-9999, -9999);

    public MouseHighlightOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false; // 鼠标穿透，不挡桌面操作

        // 全屏覆盖（主屏；Screen.Bounds 是物理像素，WPF 坐标是 DIP，需换算）
        var area = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = area.X / dpiScale;
        Top = area.Y / dpiScale;
        Width = area.Width / dpiScale;
        Height = area.Height / dpiScale;

        _fill = new Ellipse { StrokeThickness = 0 };
        _edge = new Ellipse { StrokeThickness = 1.5 };
        var canvas = new System.Windows.Controls.Canvas();
        canvas.Children.Add(_fill);
        canvas.Children.Add(_edge);
        Content = canvas;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Render();
    }

    public void SetColor(string hex)
    {
        var (b, g, r) = ClickHighlightEngine.ParseColor(hex);
        _color = System.Windows.Media.Color.FromRgb(r, g, b);
        _fill.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(_fillAlpha * 255), _color.R, _color.G, _color.B));
        _edge.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(_edgeAlpha * 255), _color.R, _color.G, _color.B));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 不激活、不进 Alt-Tab
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _ = ScreenRecorder.Interop.Win32Native.SetWindowLong(
            hwnd, ScreenRecorder.Interop.Win32Native.GWL_EXSTYLE,
            ScreenRecorder.Interop.Win32Native.GetWindowLongEx(hwnd)
            | 0x00000020 /* WS_EX_TRANSPARENT */ | 0x08000000 /* WS_EX_NOACTIVATE */);
        _timer.Start();
        Render();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void Render()
    {
        if (!IsVisible)
            return;
        var physical = System.Windows.Forms.Cursor.Position; // 屏幕物理坐标
        // 物理像素 → WPF DIP 坐标
        var dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pos = new System.Windows.Point(physical.X / dpiScale, physical.Y / dpiScale);
        // 死区：鼠标传感器在"手不动"时也有 1-3px 抖动，小于死区视为没动，
        // 避免圆高频微移造成"颤抖/闪烁"（重绘本身昂贵且可见）
        if (Math.Abs(pos.X - _lastPos.X) < DeadZone && Math.Abs(pos.Y - _lastPos.Y) < DeadZone)
            return;
        _lastPos = pos;

        // 圆以鼠标为中心；Canvas 坐标 = 屏幕坐标 - 窗口左上角
        double cx = pos.X - Left;
        double cy = pos.Y - Top;
        double d = _radius * 2;
        System.Windows.Controls.Canvas.SetLeft(_fill, cx - _radius);
        System.Windows.Controls.Canvas.SetTop(_fill, cy - _radius);
        _fill.Width = d;
        _fill.Height = d;
        System.Windows.Controls.Canvas.SetLeft(_edge, cx - _radius + 2);
        System.Windows.Controls.Canvas.SetTop(_edge, cy - _radius + 2);
        _edge.Width = d - 4;
        _edge.Height = d - 4;
    }

    /// <summary>鼠标移动死区（DIP 像素）：小于此距离不重绘，抑制传感器抖动造成的闪烁。</summary>
    private const double DeadZone = 3.0;
}
