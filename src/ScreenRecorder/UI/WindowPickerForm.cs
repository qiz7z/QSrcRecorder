using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenRecorder.Interop;
using ScreenRecorder.UI;

namespace ScreenRecorder.UI;

/// <summary>窗口选择对话框（墨韵主题）：列出当前可录制的顶层窗口。</summary>
public sealed class WindowPickerForm : Form
{
    private readonly ListView _list = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnRefresh = new();

    public Win32Native.WinWindowInfo? Selected { get; private set; }

    private int S(int v) => Math.Max(1, (int)Math.Round(v * DeviceDpi / 96.0));

    public WindowPickerForm()
    {
        Text = "QSrcRecorder · 选择窗口";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body();
        _ = Handle;   // 拿真实 DPI 再布局
        ClientSize = new Size(S(620), S(420));

        _list.SetBounds(S(12), S(12), S(596), S(352));
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.BackColor = Theme.Container;
        _list.ForeColor = Theme.TextPrimary;
        _list.Columns.Add("窗口标题", S(440));
        _list.Columns.Add("进程", S(140));
        _list.DoubleClick += (_, _) => Confirm();

        Theme.StyleFlatButton(_btnRefresh);
        _btnRefresh.Text = "刷新";
        _btnRefresh.SetBounds(S(12), S(378), S(96), S(32));
        _btnRefresh.Click += (_, _) => RefreshList();

        Theme.StyleFlatButton(_btnOk);
        _btnOk.BackColor = Theme.Brand;
        _btnOk.ForeColor = Color.White;
        _btnOk.FlatAppearance.MouseOverBackColor = Theme.BrandHover;
        _btnOk.FlatAppearance.MouseDownBackColor = Theme.BrandHover;
        _btnOk.FlatAppearance.BorderColor = Theme.Brand;
        _btnOk.Text = "确定";
        _btnOk.SetBounds(S(512), S(378), S(96), S(32));
        _btnOk.Click += (_, _) => Confirm();

        Controls.AddRange(new Control[] { _list, _btnRefresh, _btnOk });
        Load += (_, _) => { Theme.ApplyLightTitleBar(Handle); RefreshList(); };
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var w in Win32Native.EnumerateWindows())
        {
            var item = new ListViewItem(w.Title) { Tag = w, UseItemStyleForSubItems = true };
            item.SubItems.Add(w.ProcessName);
            _list.Items.Add(item);
        }
    }

    private void Confirm()
    {
        if (_list.SelectedItems.Count == 0)
            return;
        Selected = _list.SelectedItems[0].Tag as Win32Native.WinWindowInfo;
        DialogResult = DialogResult.OK;
        Close();
    }
}
