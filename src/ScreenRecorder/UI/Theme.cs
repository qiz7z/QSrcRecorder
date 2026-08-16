using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ScreenRecorder.Interop;

namespace ScreenRecorder.UI;

/// <summary>
/// 设计系统（ui-ux-pro-max design_system.py 生成结果，桌面端适配）：
/// 风格 Swiss Minimalism；配色策略 "Recording red + waveform blue"。
/// 语义化 token + 8pt 网格 + 字阶 + 统一圆角；字体映射 Poppins/Open Sans → Segoe UI。
/// </summary>
internal static class Theme
{
    // ── 色板（技能生成：录制红 + 波形蓝，正文对比度 ≥ 4.5:1）──
    public static readonly Color Surface = Color.White;                              // 页面底（Swiss 纯白）
    public static readonly Color Container = Color.White;                            // 卡片
    public static readonly Color BorderSubtle = Color.FromArgb(0xFA, 0xE4, 0xE4);    // 卡片暖描边 #FAE4E4
    public static readonly Color BorderStrong = Color.FromArgb(0xE2, 0xE8, 0xF0);    // 输入框描边
    public static readonly Color TextPrimary = Color.FromArgb(0x0F, 0x17, 0x2A);     // #0F172A
    public static readonly Color TextSecondary = Color.FromArgb(0x47, 0x55, 0x69);   // #475569
    public static readonly Color TextTertiary = Color.FromArgb(0x94, 0xA3, 0xB8);    // #94A3B8
    public static readonly Color Brand = Color.FromArgb(0xDC, 0x26, 0x26);           // 录制红 #DC2626
    public static readonly Color BrandHover = Color.FromArgb(0xB9, 0x1C, 0x1C);      // 悬停 #B91C1C
    public static readonly Color BrandSubtle = Color.FromArgb(0xFC, 0xF1, 0xF1);     // 弱化底 #FCF1F1
    public static readonly Color Accent = Color.FromArgb(0x25, 0x63, 0xEB);          // 波形蓝 #2563EB
    public static readonly Color AccentHover = Color.FromArgb(0x1D, 0x4E, 0xD8);     // 蓝·悬停
    public static readonly Color AccentSubtle = Color.FromArgb(0xEF, 0xF6, 0xFF);    // 蓝·浅底
    public static readonly Color FieldBg = Color.FromArgb(0xFC, 0xF1, 0xF1);         // 输入底 #FCF1F1

    // ── 字阶（12/13/14/16/20，正文字重规则：标题 600+，正文 400，标签 500）──
    public static Font Display() => Body(20f, FontStyle.Bold);       // 窗口标题
    public static Font Title() => Body(14f, FontStyle.Bold);         // 卡片标题
    public static Font BodyStrong() => Body(13f, FontStyle.Bold);    // 按钮文字
    public static Font Label() => Body(12f, FontStyle.Regular);      // 字段标签
    public static Font Body(float size = 13f, FontStyle style = FontStyle.Regular)
        => new(SystemFonts.MessageBoxFont.FontFamily, size, style);
    public static Font Mono(float size, FontStyle style = FontStyle.Bold)
        => new("Consolas", size, style);

    // ── 间距（8pt 网格：只用这些值）──
    public const int Space1 = 4;
    public const int Space2 = 8;
    public const int Space3 = 12;
    public const int Space4 = 16;
    public const int Space6 = 24;

    // ── 圆角（统一三档）──
    public const int RadiusInput = 6;    // 输入框/小按钮
    public const int RadiusCard = 10;    // 卡片/大按钮
    public const int RadiusChip = 8;     // 图标块

    // ── DWM 外观 ──
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void ApplyLightTitleBar(IntPtr hwnd)
    {
        int dark = 0;
        _ = Win32Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int caption = (Surface.B << 16) | (Surface.G << 8) | Surface.R;
        _ = Win32Native.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    public static void RoundWindow(IntPtr hwnd)
    {
        int pref = DWMWCP_ROUND;
        _ = Win32Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    // ── 圆角路径 ──
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── 控件样式 ──
    public static void StyleFlatButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Container;
        b.ForeColor = TextPrimary;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = BorderStrong;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xF9, 0xFA, 0xFB);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xF2, 0xF4, 0xF7);
        b.Cursor = Cursors.Hand;
        b.Font = Label();
    }

    public static void StyleCombo(ComboBox cbo)
    {
        cbo.DropDownStyle = ComboBoxStyle.DropDownList;
        cbo.FlatStyle = FlatStyle.Flat;
        cbo.BackColor = FieldBg;
        cbo.ForeColor = TextPrimary;
        cbo.Font = Label();
        cbo.DrawMode = DrawMode.OwnerDrawFixed;
        cbo.ItemHeight = cbo.Font.Height + 8;
        cbo.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
                return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(selected ? AccentSubtle : FieldBg);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, cbo.GetItemText(cbo.Items[e.Index]), e.Font!, e.Bounds,
                selected ? Accent : TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
    }

    public static void StyleTextBox(TextBox t)
    {
        t.BackColor = FieldBg;
        t.ForeColor = TextPrimary;
        t.BorderStyle = BorderStyle.FixedSingle;
        t.Font = Label();
    }

    public static void StyleLink(LinkLabel l)
    {
        // 链接属于辅助操作 → 波形蓝（红只留给录制主操作）
        l.LinkColor = Accent;
        l.ActiveLinkColor = AccentHover;
        l.VisitedLinkColor = Accent;
        l.Font = Label();
    }
}

/// <summary>白色卡片：暖色细描边、无阴影（Swiss 风格：用线条不用投影）。</summary>
internal sealed class WhiteCard : Panel
{
    public WhiteCard()
    {
        BackColor = Theme.Container;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedPath(rect, Theme.RadiusCard);
        using var pen = new Pen(Theme.BorderSubtle);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// 模式卡片：图标块 + 标题/副题。
/// 选中态纪律（不过度用色）：品牌描边 + 图标块反白 + 标题品牌色，底色只做极浅提示。
/// </summary>
internal sealed class ModeCard : Control
{
    public enum CardIcon { Monitor, Crop, Window }

    private readonly CardIcon _icon;
    private readonly string _subtitle;
    private bool _hover;
    private bool _selected;

    public event Action? SelectedChanged;

    public ModeCard(CardIcon icon, string title, string subtitle)
    {
        _icon = icon;
        Text = title;
        _subtitle = subtitle;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
    }

    public void SetSelected(bool value)
    {
        if (_selected == value)
            return;
        _selected = value;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { SelectedChanged?.Invoke(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 2);
        using (var path = Theme.RoundedPath(rect, Theme.RadiusCard))
        {
            using var fill = new SolidBrush(_selected
                ? Theme.AccentSubtle
                : (_hover ? Color.FromArgb(0xF8, 0xFA, 0xFC) : Theme.Container));
            g.FillPath(fill, path);
            using var pen = new Pen(_selected ? Theme.Accent : Theme.BorderSubtle, _selected ? 1.6f : 1f);
            g.DrawPath(pen, path);
        }

        // 图标块 36×36：未选中冷灰底；选中波形蓝底白图标
        int chip = 36;
        var chipRect = new Rectangle(Theme.Space3, Height / 2 - chip / 2, chip, chip);
        using (var chipPath = Theme.RoundedPath(chipRect, Theme.RadiusChip))
        {
            using var chipFill = new SolidBrush(_selected ? Theme.Accent : Color.FromArgb(0xF1, 0xF5, 0xF9));
            g.FillPath(chipFill, chipPath);
        }
        DrawIcon(g, chipRect.X + chip / 2, chipRect.Y + chip / 2, _selected ? Color.White : Theme.TextSecondary);

        int tx = chipRect.Right + Theme.Space3;
        TextRenderer.DrawText(g, Text, Theme.Body(13f, _selected ? FontStyle.Bold : FontStyle.Regular),
            new Rectangle(tx, Height / 2 - 24, Width - tx - Theme.Space1, 22),
            _selected ? Theme.Accent : Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _subtitle, Theme.Body(11f),
            new Rectangle(tx, Height / 2 + 2, Width - tx - Theme.Space1, 18),
            Theme.TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawIcon(Graphics g, int cx, int cy, Color color)
    {
        using var pen = new Pen(color, 1.8f);
        switch (_icon)
        {
            case CardIcon.Monitor:
                g.DrawRoundedRectangle(pen, cx - 8, cy - 7, 16, 11, 2);
                g.DrawLine(pen, cx - 3, cy + 7, cx + 3, cy + 7);
                g.DrawLine(pen, cx, cy + 4, cx, cy + 7);
                break;
            case CardIcon.Crop:
                int s = 4;
                g.DrawLine(pen, cx - 8, cy - 8 + s, cx - 8, cy - 8);
                g.DrawLine(pen, cx - 8, cy - 8, cx - 8 + s, cy - 8);
                g.DrawLine(pen, cx + 8 - s, cy - 8, cx + 8, cy - 8);
                g.DrawLine(pen, cx + 8, cy - 8, cx + 8, cy - 8 + s);
                g.DrawLine(pen, cx + 8, cy + 8 - s, cx + 8, cy + 8);
                g.DrawLine(pen, cx + 8, cy + 8, cx + 8 - s, cy + 8);
                g.DrawLine(pen, cx - 8 + s, cy + 8, cx - 8, cy + 8);
                g.DrawLine(pen, cx - 8, cy + 8, cx - 8, cy + 8 - s);
                break;
            case CardIcon.Window:
                g.DrawRoundedRectangle(pen, cx - 8, cy - 7, 16, 14, 2);
                g.DrawLine(pen, cx - 8, cy - 3, cx + 8, cy - 3);
                g.DrawEllipse(pen, cx + 3, cy - 6, 2, 2);
                g.DrawEllipse(pen, cx + 6, cy - 6, 2, 2);
                break;
        }
    }
}

/// <summary>主操作按钮：品牌底白字，圆角统一 10，明确悬停/按下态。</summary>
internal sealed class PrimaryButton : Control
{
    private bool _hover;
    private bool _down;

    public PrimaryButton()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Font = Theme.Body(14f, FontStyle.Bold);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 2);
        using var path = Theme.RoundedPath(rect, Theme.RadiusCard);
        using var fill = new SolidBrush(_down || _hover ? Theme.BrandHover : Theme.Brand);
        g.FillPath(fill, path);
        TextRenderer.DrawText(g, Text, Font, rect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        using var path = Theme.RoundedPath(new Rectangle((int)x, (int)y, (int)w, (int)h), (int)r);
        g.DrawPath(pen, path);
    }
}
