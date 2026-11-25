using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenRecorder.Capture;
using ScreenRecorder.Interop;

namespace ScreenRecorder.Overlays;

public sealed class PipOverlayWindow : Window
{
    private const int HandleSize = 14;
    private const double RefreshIntervalMs = 33;

    private readonly WebcamCapture _capture;
    private readonly System.Windows.Controls.Image _previewImage;
    private readonly DispatcherTimer _timer;
    private readonly double _dpiScale;

    private bool _dragging;
    private ResizeEdge _resizeEdge;
    private System.Windows.Point _dragStart;
    private double _winStartLeft, _winStartTop, _winStartW, _winStartH;

    public bool IsActive => IsVisible;

    /// <summary>拖拽或缩放结束，位置已稳定。</summary>
    public event EventHandler? GeometryCommitted;

    public PipOverlayWindow(WebcamCapture capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        // 句柄要等 SourceInitialized（Show 之后）才存在，那时才能真正排除捕获
        SourceInitialized += (_, _) => ExcludeFromCapture();

        _dpiScale = 1.0;
        try { _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }

        _capture.TryCopyLatestFrame(out var _, out int fw, out int fh);
        double w = fw > 0 ? fw / _dpiScale : 320;
        double h = fh > 0 ? fh / _dpiScale : 240;
        Width = Math.Min(w, SystemParameters.PrimaryScreenWidth / 2);
        Height = Math.Min(h, SystemParameters.PrimaryScreenHeight / 2);
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 20;

        _previewImage = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xD3, 0xEE)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = System.Windows.Media.Brushes.Black,
            Margin = new Thickness(6),
            Child = _previewImage,
        };
        var root = new Grid();
        root.Children.Add(frame);
        foreach (var (hx, hy) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
        {
            var grip = new System.Windows.Shapes.Rectangle
            {
                Width = 10,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xD3, 0xEE)),
                IsHitTestVisible = false,
                HorizontalAlignment = hx == 0 ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = hy == 0 ? System.Windows.VerticalAlignment.Top : System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(2),
            };
            root.Children.Add(grip);
        }
        Content = root;
        Cursor = System.Windows.Input.Cursors.Hand;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseLeave += OnMouseLeave;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs) };
        _timer.Tick += (_, _) => RefreshPreview();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>隐藏并停止刷新。不会再把定时器打开。</summary>
    public void HideAndClear()
    {
        Stop();
        _previewImage.Source = null;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>显示并恢复预览刷新。调用方也可先 Show，这里会保证窗口可见。</summary>
    public void RestoreAndResume()
    {
        Visibility = Visibility.Visible;
        if (!IsVisible)
            Show();
        _timer.Start();
        RefreshPreview();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _previewImage.Source = null;
        base.OnClosed(e);
    }

    private void ExcludeFromCapture()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32Native.ExcludeFromCapture(hwnd);
        }
        catch
        {
            // 旧系统不支持时忽略
        }
    }

    private void RefreshPreview()
    {
        if (!_capture.TryCopyLatestFrame(out var frame, out int w, out int h))
            return;
        if (w < 1 || h < 1 || frame == null || frame.Length < w * h * 4)
            return;

        WriteableBitmap wb = _previewImage.Source as WriteableBitmap;
        if (wb == null || wb.PixelWidth != w || wb.PixelHeight != h)
        {
            wb = new WriteableBitmap(w, h, 96 * _dpiScale, 96 * _dpiScale, PixelFormats.Bgra32, null);
            _previewImage.Source = wb;
        }
        wb.WritePixels(new Int32Rect(0, 0, w, h), frame, w * 4, 0);
    }

    private void OnMouseDown(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        CaptureMouse();
        _dragging = false;
        _dragStart = PointToScreen(e.GetPosition(this));
        _winStartLeft = Left;
        _timer.Stop();
        _winStartTop = Top;
        _winStartW = Width;
        _winStartH = Height;
        _resizeEdge = DetectResizeEdge(e.GetPosition(this));
        if (_resizeEdge == ResizeEdge.None)
            _dragging = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var local = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            SetCursor(ResizeCursor(DetectResizeEdge(local)));
            return;
        }

        var screen = PointToScreen(local);
        double scale = CurrentDpi();
        double dx = (screen.X - _dragStart.X) / scale;
        double dy = (screen.Y - _dragStart.Y) / scale;

        if (_resizeEdge != ResizeEdge.None)
        {
            SetCursor(ResizeCursor(_resizeEdge));
            switch (_resizeEdge)
            {
                case ResizeEdge.Left:
                    Left = _winStartLeft + dx;
                    Width = Math.Max(80, _winStartW - dx);
                    break;
                case ResizeEdge.Right:
                    Width = Math.Max(80, _winStartW + dx);
                    break;
                case ResizeEdge.Top:
                    Top = _winStartTop + dy;
                    Height = Math.Max(60, _winStartH - dy);
                    break;
                case ResizeEdge.Bottom:
                    Height = Math.Max(60, _winStartH + dy);
                    break;
                case ResizeEdge.TopLeft:
                    Top = _winStartTop + dy;
                    Left = _winStartLeft + dx;
                    Width = Math.Max(80, _winStartW - dx);
                    Height = Math.Max(60, _winStartH - dy);
                    break;
                case ResizeEdge.TopRight:
                    Top = _winStartTop + dy;
                    Width = Math.Max(80, _winStartW + dx);
                    Height = Math.Max(60, _winStartH - dy);
                    break;
                case ResizeEdge.BottomLeft:
                    Left = _winStartLeft + dx;
                    Width = Math.Max(80, _winStartW - dx);
                    Height = Math.Max(60, _winStartH + dy);
                    break;
                case ResizeEdge.BottomRight:
                    Width = Math.Max(80, _winStartW + dx);
                    Height = Math.Max(60, _winStartH + dy);
                    break;
            }
        }
        else if (_dragging)
        {
            SetCursor(System.Windows.Input.Cursors.SizeAll);
            Left = _winStartLeft + dx;
            Top = _winStartTop + dy;
        }
    }

    private double CurrentDpi()
    {
        try
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale > 0) return scale;
        }
        catch { /* 用构造时的比例 */ }
        return _dpiScale > 0 ? _dpiScale : 1;
    }

    private void OnMouseUp(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        EndInteraction(commit: true);
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        EndInteraction(commit: true);
    }

    private void EndInteraction(bool commit)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        bool moved = _dragging || _resizeEdge != ResizeEdge.None;
        _dragging = false;
        _resizeEdge = ResizeEdge.None;
        Cursor = System.Windows.Input.Cursors.Hand;
        // 只有可见时才恢复刷新；隐藏期间保持停止
        if (Visibility == Visibility.Visible)
            _timer.Start();
        if (commit && moved)
            GeometryCommitted?.Invoke(this, EventArgs.Empty);
    }

    private ResizeEdge DetectResizeEdge(System.Windows.Point pos)
    {
        double hw = HandleSize;
        bool left = pos.X < hw;
        bool right = pos.X > Width - hw;
        bool top = pos.Y < hw;
        bool bottom = pos.Y > Height - hw;
        if (left && top) return ResizeEdge.TopLeft;
        if (right && top) return ResizeEdge.TopRight;
        if (left && bottom) return ResizeEdge.BottomLeft;
        if (right && bottom) return ResizeEdge.BottomRight;
        if (left) return ResizeEdge.Left;
        if (right) return ResizeEdge.Right;
        if (top) return ResizeEdge.Top;
        if (bottom) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }

    private void SetCursor(System.Windows.Input.Cursor c) => this.Cursor = c;

    private static System.Windows.Input.Cursor ResizeCursor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => System.Windows.Input.Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => System.Windows.Input.Cursors.SizeNS,
        ResizeEdge.TopLeft or ResizeEdge.BottomRight => System.Windows.Input.Cursors.SizeNWSE,
        ResizeEdge.TopRight or ResizeEdge.BottomLeft => System.Windows.Input.Cursors.SizeNESW,
        _ => System.Windows.Input.Cursors.Hand,
    };

    private enum ResizeEdge { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
}
