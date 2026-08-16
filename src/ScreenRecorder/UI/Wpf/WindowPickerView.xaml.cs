using System.Linq;
using System.Windows;
using System.Windows.Input;
using ScreenRecorder.Interop;

namespace ScreenRecorder.UI.Wpf;

/// <summary>
/// 窗口选择对话框（WPF 原生版）。
/// 说明：旧 WinForms 版在 WPF 宿主里 ShowDialog 会抛未处理异常（模态消息循环不兼容），
/// 此版与主窗体同一套设计 token，规避该问题。
/// </summary>
public partial class WindowPickerView : Window
{
    public Win32Native.WinWindowInfo? Selected { get; private set; }

    public WindowPickerView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        var windows = Win32Native.EnumerateWindows().OrderBy(w => w.ProcessName).ToList();
        WindowList.ItemsSource = windows;
        CountText.Text = $"{windows.Count} 个窗口";
    }

    private void Confirm()
    {
        if (WindowList.SelectedItem is Win32Native.WinWindowInfo w)
        {
            Selected = w;
            DialogResult = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void WindowList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Confirm();
    }
}
