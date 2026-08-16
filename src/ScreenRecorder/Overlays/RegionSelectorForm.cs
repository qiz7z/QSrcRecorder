using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScreenRecorder.Overlays;

/// <summary>
/// 区域选择覆盖层：先冻结当前屏幕截图，再在其上拖框选择录制区域。
/// 返回屏幕物理坐标的矩形。
/// </summary>
public sealed class RegionSelectorForm : Form
{
    private readonly Screen _screen;
    private Bitmap? _frozen;
    private Point _start;
    private Rectangle _sel;
    private bool _dragging;

    /// <summary>选中的矩形（屏幕物理坐标）。</summary>
    public Rectangle SelectedRect { get; private set; }

    public RegionSelectorForm(Screen screen)
    {
        _screen = screen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var b = _screen.Bounds;
        _frozen = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(_frozen);
        g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(b.Width, b.Height));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _start = e.Location;
            _sel = new Rectangle(e.Location, Size.Empty);
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;
        _sel = Normalize(_start, e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            _sel = Normalize(_start, e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Confirm();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            Confirm();
        }
    }

    private void Confirm()
    {
        var r = _sel;
        if (r.Width < 8 || r.Height < 8)
            return;
        r.Width -= r.Width % 2;   // yuv420p 需要偶数尺寸
        r.Height -= r.Height % 2;
        if (r.Width < 8 || r.Height < 8)
            return;
        SelectedRect = new Rectangle(_screen.Bounds.X + r.X, _screen.Bounds.Y + r.Y, r.Width, r.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Rectangle Normalize(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 全部绘制放到 OnPaint，避免闪烁
        if (_frozen != null)
            e.Graphics.DrawImage(_frozen, Point.Empty);
        else
            base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_frozen == null)
            return;

        using var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        g.FillRectangle(dim, ClientRectangle);

        // 选中区域显示原图
        if (_sel.Width >= 2 && _sel.Height >= 2)
        {
            g.DrawImage(_frozen, _sel, _sel, GraphicsUnit.Pixel);
            using var border = new Pen(Color.FromArgb(224, 72, 62), 2);
            g.DrawRectangle(border, _sel);
            string sizeText = $"{_sel.Width} × {_sel.Height}";
            using var font = new Font("Segoe UI", 11f);
            var sz = g.MeasureString(sizeText, font);
            var pos = new PointF(_sel.X, _sel.Y - sz.Height - 6);
            if (pos.Y < 0)
                pos.Y = _sel.Bottom + 6;
            g.FillRectangle(Brushes.Black, new RectangleF(pos, sz));
            g.DrawString(sizeText, font, Brushes.White, pos);
        }

        using var hintFont = UI.Theme.Body(13f);
        string hint = "拖动鼠标框选录制区域 · Enter 或双击确认 · Esc 取消";
        var hintSize = g.MeasureString(hint, hintFont);
        var hintPos = new PointF((ClientSize.Width - hintSize.Width) / 2, 24);
        g.FillRectangle(Brushes.Black, new RectangleF(hintPos - new SizeF(12, 6), hintSize + new SizeF(24, 12)));
        g.DrawString(hint, hintFont, Brushes.White, hintPos);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _frozen?.Dispose();
        _frozen = null;
        base.OnFormClosed(e);
    }
}
