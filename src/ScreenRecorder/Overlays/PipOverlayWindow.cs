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
    private const int HandleSize = 8;
    private const double RefreshIntervalMs = 33;

    private readonly WebcamCapture _capture;
    private readonly System.Windows.Controls.Image _previewImage;
    private readonly Canvas _canvas;
    private readonly DispatcherTimer _timer;
    private readonly double _dpiScale;

    private bool _dragging;
    private ResizeEdge _resizeEdge;
    private System.Windows.Point _dragStart;
    private double _winStartLeft, _winStartTop, _winStartW, _winStartH;

    public bool IsActive => IsVisible;

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

        // 排除 WGC 捕获：此窗口不会出现在录制的成片中
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                Win32Native.ExcludeFromCapture(hwnd);
        }
        catch { /* 忽略，旧系统不支持 */ }

        _dpiScale = 1.0;
        try { _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }

        _capture.TryCopyLatestFrame(out var _, out int fw, out int fh);
        double w = fw > 0 ? fw / _dpiScale : 320;
        double h = fh > 0 ? fh / _dpiScale : 240;
        Width = Math.Min(w, SystemParameters.PrimaryScreenWidth / 2);
        Height = Math.Min(h, SystemParameters.PrimaryScreenHeight / 2);
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 20;

        _canvas = new Canvas { Background = System.Windows.Media.Brushes.Transparent };
        _previewImage = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        _canvas.Children.Add(_previewImage);
        Content = _canvas;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseLeave += OnMouseLeave;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs) };
        _timer.Tick += (_, _) => RefreshPreview();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void HideAndClear()
    {
        Stop();
        _previewImage.Source = null;
        Visibility = Visibility.Collapsed;
    }
    public void RestoreAndResume()
    {
        Visibility = Visibility.Visible;
        _timer.Start();
        RefreshPreview();
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
        _dragStart = e.GetPosition(this);
        _winStartLeft = Left;
        _winStartTop = Top;
        _winStartW = Width;
        _winStartH = Height;
        _resizeEdge = DetectResizeEdge(e.GetPosition(this));
        if (_resizeEdge == ResizeEdge.None)
            _dragging = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;

        if (_resizeEdge != ResizeEdge.None)
        {
            SetCursor(ResizeCursor(_resizeEdge));
            switch (_resizeEdge)
            {
                case ResizeEdge.Left:    Left = _winStartLeft + dx; Width = Math.Max(80, _winStartW - dx); break;
                case ResizeEdge.Right:   Width = Math.Max(80, _winStartW + dx); break;
                case ResizeEdge.Top:     Top = _winStartTop + dy; Height = Math.Max(60, _winStartH - dy); break;
                case ResizeEdge.Bottom:  Height = Math.Max(60, _winStartH + dy); break;
                case ResizeEdge.TopLeft: Top = _winStartTop + dy; Left = _winStartLeft + dx;
                                          Width = Math.Max(80, _winStartW - dx); Height = Math.Max(60, _winStartH - dy); break;
                case ResizeEdge.TopRight: Top = _winStartTop + dy; Width = Math.Max(80, _winStartW + dx); Height = Math.Max(60, _winStartH - dy); break;
                case ResizeEdge.BottomLeft: Left = _winStartLeft + dx; Width = Math.Max(80, _winStartW - dx); Height = Math.Max(60, _winStartH + dy); break;
                case ResizeEdge.BottomRight: Width = Math.Max(80, _winStartW + dx); Height = Math.Max(60, _winStartH + dy); break;
            }
        }
        else if (_dragging)
        {
            SetCursor(System.Windows.Input.Cursors.SizeAll);
            Left = _winStartLeft + dx;
            Top = _winStartTop + dy;
        }
        else
        {
            _resizeEdge = DetectResizeEdge(pos);
            SetCursor(ResizeCursor(_resizeEdge));
        }
    }

    private void OnMouseUp(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        _dragging = false;
        _resizeEdge = ResizeEdge.None;
        SetCursor(System.Windows.Input.Cursors.Hand);
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        _dragging = false;
        _resizeEdge = ResizeEdge.None;
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
