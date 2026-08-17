using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenRecorder.UI.Wpf;

/// <summary>
/// HSV 色环选择器：色环选色相+饱和度，右侧亮度条调明暗，实时预览与 hex 同步。
/// 自绘 WriteableBitmap 渲染，无第三方依赖。
/// </summary>
public partial class ColorWheelControl : System.Windows.Controls.UserControl
{
    private const int WheelSize = 92;
    private const double InnerRadius = 2.0; // 中心留一点纯色半径

    private WriteableBitmap? _wheel;
    private double _hue;         // 0-360
    private double _sat;         // 0-1
    private double _value;       // 0-1
    private bool _draggingWheel;
    private bool _draggingBrightness;

    public event EventHandler? ColorChanged;

    public ColorWheelControl()
    {
        InitializeComponent();
        _value = 1.0;
        RenderWheel();
        UpdateBrightnessBar();
        UpdatePreview();
    }

    /// <summary>设置当前颜色（外部恢复设置时调用）。</summary>
    public void SetColor(string hex)
    {
        var (b, g, r) = Overlays.ClickHighlightEngine.ParseColor(hex);
        RgbToHsv(r, g, b, out _hue, out _sat, out _value);
        RenderWheel();
        UpdateBrightnessBar();
        UpdatePreview();
    }

    public string HexValue
    {
        get
        {
            var (r, g, b) = HsvToRgb(_hue, _sat, _value);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
    }

    // ── 色环渲染 ────────────────────────────────
    private void RenderWheel()
    {
        if (_wheel == null)
            _wheel = new WriteableBitmap(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
        _wheel.Lock();
        var px = _wheel.BackBuffer;
        int stride = _wheel.BackBufferStride;
        int cx = WheelSize / 2, cy = WheelSize / 2;
        double outerR = WheelSize / 2.0 - 1;

        unsafe
        {
            byte* p = (byte*)px;
            for (int y = 0; y < WheelSize; y++)
            {
                for (int x = 0; x < WheelSize; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    byte* dst = p + y * stride + x * 4;
                    if (dist > outerR)
                    {
                        dst[0] = dst[1] = dst[2] = 0;
                        dst[3] = 0;
                        continue;
                    }
                    double sat = dist >= InnerRadius
                        ? Math.Min(1.0, dist / outerR)
                        : 0.0;
                    double hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    if (hue < 0)
                        hue += 360.0;
                    var (r, g, b) = HsvToRgb(hue, sat, _value);
                    dst[0] = b; dst[1] = g; dst[2] = r;
                    dst[3] = 255;
                }
            }
        }
        _wheel.AddDirtyRect(new Int32Rect(0, 0, WheelSize, WheelSize));
        _wheel.Unlock();
        WheelImage.Source = _wheel;
        DrawIndicator();
    }

    private void DrawIndicator()
    {
        // 用一个小圆点叠加在色环上：简化实现——直接重渲染时把指示点画进位图
        if (_wheel == null)
            return;
        _wheel.Lock();
        var px = _wheel.BackBuffer;
        int stride = _wheel.BackBufferStride;
        int cx = WheelSize / 2, cy = WheelSize / 2;
        double outerR = WheelSize / 2.0 - 1;
        double angle = _hue * Math.PI / 180.0;
        double radius = _sat * outerR;
        int ix = cx + (int)Math.Round(Math.Cos(angle) * radius);
        int iy = cy + (int)Math.Round(Math.Sin(angle) * radius);

        unsafe
        {
            byte* p = (byte*)px;
            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (dx * dx + dy * dy > 9)
                        continue;
                    int x = ix + dx, y = iy + dy;
                    if (x < 0 || y < 0 || x >= WheelSize || y >= WheelSize)
                        continue;
                    byte* dst = p + y * stride + x * 4;
                    dst[0] = dst[1] = dst[2] = 0;
                    dst[3] = 255;
                }
            }
        }
        _wheel.AddDirtyRect(new Int32Rect(0, 0, WheelSize, WheelSize));
        _wheel.Unlock();
    }

    // ── 亮度条 ──────────────────────────────────
    private void UpdateBrightnessBar()
    {
        var (r, g, b) = HsvToRgb(_hue, _sat, 1.0);
        var top = System.Windows.Media.Color.FromRgb(r, g, b);
        var grad = new LinearGradientBrush(top, Colors.Black, 90.0);
        BrightnessGradient.Background = grad;

        // 亮度条高 92（含 1px 边框），thumb 高 6，留出边距
        double track = 92 - 8 - 6;
        BrightnessThumb.Margin = new Thickness(0, 4 + (1.0 - _value) * track, 0, 0);
    }

    private void UpdatePreview()
    {
        var (r, g, b) = HsvToRgb(_hue, _sat, _value);
        PreviewBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        HexText.Text = $"#{r:X2}{g:X2}{b:X2}";
    }

    private void NotifyChanged()
    {
        UpdatePreview();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 鼠标交互 ────────────────────────────────
    private void Wheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingWheel = true;
        WheelImage.CaptureMouse();
        PickWheel(e.GetPosition(WheelImage));
    }

    private void Wheel_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingWheel)
            PickWheel(e.GetPosition(WheelImage));
    }

    private void Wheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingWheel = false;
        WheelImage.ReleaseMouseCapture();
    }

    private void PickWheel(System.Windows.Point pos)
    {
        double cx = WheelImage.ActualWidth / 2.0;
        double cy = WheelImage.ActualHeight / 2.0;
        double outerR = Math.Min(cx, cy) - 1;
        double dx = pos.X - cx, dy = pos.Y - cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > outerR)
            return;

        _hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (_hue < 0)
            _hue += 360.0;
        _sat = Math.Min(1.0, dist / outerR);
        RenderWheel();
        UpdateBrightnessBar();
        NotifyChanged();
    }

    private void Brightness_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingBrightness = true;
        ((Border)sender).CaptureMouse();
        PickBrightness(e.GetPosition(BrightnessGradient));
    }

    private void Brightness_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingBrightness)
            PickBrightness(e.GetPosition(BrightnessGradient));
    }

    private void Brightness_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingBrightness = false;
        ((Border)sender).ReleaseMouseCapture();
    }

    private void PickBrightness(System.Windows.Point pos)
    {
        double h = BrightnessGradient.ActualHeight;
        if (h <= 0)
            return;
        _value = Math.Clamp(1.0 - pos.Y / h, 0.0, 1.0);
        RenderWheel();
        UpdateBrightnessBar();
        NotifyChanged();
    }

    // ── HSV ↔ RGB（全 0-255 分量） ──────────────
    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;
        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == rd)
        {
            h = 60.0 * (((gd - bd) / delta) % 6.0);
        }
        else if (max == gd)
        {
            h = 60.0 * (((bd - rd) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((rd - gd) / delta) + 4.0);
        }
        if (h < 0)
            h += 360.0;
    }
}
