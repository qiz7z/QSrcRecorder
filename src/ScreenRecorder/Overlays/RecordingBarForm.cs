using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenRecorder.Interop;
using ScreenRecorder.UI;

namespace ScreenRecorder.Overlays;

/// <summary>
/// 录制中的悬浮控制条：时长显示、暂停/继续、停止。
/// 布局按当前 DPI 实测文字尺寸计算（高分屏缩放下绝不裁字）。
/// </summary>
public sealed class RecordingBarForm : Form
{
    private const int HTCAPTION = 0x2;

    private readonly RecordingSession _session;
    private readonly Label _lblDot = new();
    private readonly Label _lblTime = new();
    private readonly Button _btnPause = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnHide = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private long _lastActiveTick;

    /// <summary>用户点击"隐藏"按钮：把悬浮条收进系统托盘（录制画面因此干净）。</summary>
    public event Action? HideRequested;

    public RecordingBarForm(RecordingSession session)
    {
        _session = session;

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Container;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None; // 布局自己按 DPI 算，不做自动缩放
        _lastActiveTick = Environment.TickCount64;

        _lblDot.Text = "●";
        _lblDot.ForeColor = Theme.Brand;
        _lblDot.Font = new Font("Segoe UI", 12f);
        _lblDot.TextAlign = ContentAlignment.MiddleCenter;

        _lblTime.Text = "00:00";
        _lblTime.ForeColor = Theme.TextPrimary;
        _lblTime.Font = new Font("Consolas", 12f, FontStyle.Bold);
        _lblTime.TextAlign = ContentAlignment.MiddleLeft;

        _btnPause.Text = "暂停 F10";
        Theme.StyleFlatButton(_btnPause);
        _btnPause.Click += (_, _) => TogglePause();

        _btnStop.Text = "停止 F9";
        Theme.StyleFlatButton(_btnStop);
        _btnStop.BackColor = Theme.Brand;
        _btnStop.ForeColor = Color.White;
        _btnStop.FlatAppearance.MouseOverBackColor = Theme.BrandHover;
        _btnStop.FlatAppearance.MouseDownBackColor = Theme.BrandHover;
        _btnStop.FlatAppearance.BorderColor = Theme.Brand;
        _btnStop.Click += (_, _) => _session.Stop();

        _btnHide.Text = "隐藏";
        Theme.StyleFlatButton(_btnHide);
        new ToolTip().SetToolTip(_btnHide, "隐藏到系统托盘（录制画面不显示悬浮条）");
        _btnHide.Click += (_, _) => HideRequested?.Invoke();

        Controls.AddRange(new Control[] { _lblDot, _lblTime, _btnPause, _btnStop, _btnHide });

        _timer.Interval = 250;
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        // 鼠标靠近恢复不透明，闲置 5 秒淡化（录制画面里更不显眼）
        MouseEnter += (_, _) => _lastActiveTick = Environment.TickCount64;
        MouseMove += (_, _) => _lastActiveTick = Environment.TickCount64;

        FormClosed += (_, _) => _timer.Stop();

        _session.Completed += OnSessionCompleted;
    }

    private int Scale(int v) => Math.Max(1, (int)Math.Round(v * DeviceDpi / 96.0));

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Theme.RoundWindow(Handle); // Win11 圆角

        // 所有尺寸来自实际文字测量 + DPI 缩放，任何缩放比例下都不会裁字
        var dotSize = TextRenderer.MeasureText(_lblDot.Text, _lblDot.Font);
        var timeSize = TextRenderer.MeasureText("00:00", _lblTime.Font);
        var pauseSize = TextRenderer.MeasureText(_btnPause.Text, _btnPause.Font);
        var stopSize = TextRenderer.MeasureText(_btnStop.Text, _btnStop.Font);
        var hideSize = TextRenderer.MeasureText(_btnHide.Text, _btnHide.Font);

        int pad = Scale(10);
        int gap = Scale(8);
        int btnH = Math.Max(pauseSize.Height, Math.Max(stopSize.Height, hideSize.Height)) + Scale(10);
        int barH = btnH + Scale(16);
        int btnPauseW = pauseSize.Width + Scale(24);
        int btnStopW = stopSize.Width + Scale(24);
        int btnHideW = hideSize.Width + Scale(24);

        int x = pad;
        _lblDot.SetBounds(x, 0, dotSize.Width, barH);
        x += dotSize.Width + Scale(6);
        _lblTime.SetBounds(x, 0, timeSize.Width + Scale(4), barH);
        x += timeSize.Width + Scale(4) + Scale(10);
        _btnPause.SetBounds(x, (barH - btnH) / 2, btnPauseW, btnH);
        x += btnPauseW + gap;
        _btnStop.SetBounds(x, (barH - btnH) / 2, btnStopW, btnH);
        x += btnStopW + gap;
        _btnHide.SetBounds(x, (barH - btnH) / 2, btnHideW, btnH);
        x += btnHideW + pad;

        Size = new Size(x, barH);

        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - Scale(24), area.Bottom - Height - Scale(24));
    }

    private void OnSessionCompleted(RecordingResult _)
    {
        if (InvokeRequired)
        {
            BeginInvoke(Close);
        }
        else
        {
            Close();
        }
    }

    private void TogglePause()
    {
        if (_session.IsPaused)
            _session.Resume();
        else
            _session.Pause();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        _lblTime.Text = _session.RecordedDuration.ToString(@"mm\:ss");
        _btnPause.Text = _session.IsPaused ? "继续 F10" : "暂停 F10";
        double target = Environment.TickCount64 - _lastActiveTick > 5000 ? 0.6 : 1.0;
        if (Math.Abs(Opacity - target) > 0.01)
            Opacity = target;
        // 显式要求立即重绘，确保时长变化即时上屏
        Invalidate();
        Update();
    }

    /// <summary>让整个条（除按钮外）可以用鼠标拖动。</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)1) // HTCLIENT
            {
                var pos = PointToClient(Cursor.Position);
                bool onButton = _btnPause.Bounds.Contains(pos) || _btnStop.Bounds.Contains(pos);
                if (!onButton)
                    m.Result = (IntPtr)HTCAPTION;
            }
            return;
        }
        base.WndProc(ref m);
    }
}
