using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using ScreenRecorder.Encoding;
using ScreenRecorder.Interop;
using ScreenRecorder.Overlays;
using ScreenRecorder.Settings;
using MessageBox = System.Windows.MessageBox;

namespace ScreenRecorder.UI.Wpf;

/// <summary>
/// QSrcRecorder 主界面（WPF 壳层；录制逻辑与 WinForms 版完全一致）。
/// </summary>
public partial class MainView : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyToggle = 1;
    private const int HotkeyPause = 2;

    private RecordingSession? _session;
    private RecordingBarForm? _bar;
    private RecordingOptions? _lastOptions;
    private bool _softwareRetryUsed;
    private Win32Native.WinWindowInfo? _window;
    private AppSettings _settings = new();

    public MainView()
    {
        InitializeComponent();

        // 静态选项（构造期填充，LoadSettings 之前必须就绪）
        CboFps.Items.Add("24");
        CboFps.Items.Add("30");
        CboFps.Items.Add("60");
        CboFps.SelectedIndex = 1;
        CboScale.Items.Add("100%");
        CboScale.Items.Add("75%");
        CboScale.Items.Add("50%");
        CboScale.SelectedIndex = 0;
        CboQuality.Items.Add("高");
        CboQuality.Items.Add("中");
        CboQuality.Items.Add("低");
        CboQuality.SelectedIndex = 1;

        Loaded += (_, _) =>
        {
            RefreshScreens();
            LoadSettings();
            _ = DetectEncoderAsync();
        };
        Closing += (_, _) => SaveSettings();
    }

    // ── 热键（挂在 WPF 窗口句柄上） ────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _ = Win32Native.RegisterHotKey(hwnd, HotkeyToggle, 0, Win32Native.VK_F9);
        _ = Win32Native.RegisterHotKey(hwnd, HotkeyPause, 0, Win32Native.VK_F10);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyToggle)
                ToggleRecord();
            else if (id == HotkeyPause)
                TogglePause();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ── 编码器后台探测 ─────────────────────────────
    private async Task DetectEncoderAsync()
    {
        string? detected = null;
        try
        {
            var ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();
            var kind = await Task.Run(() => HardwareEncoderDetector.Detect(ffmpeg));
            detected = EncoderNames.Display(kind);
        }
        catch
        {
            detected = "未找到 ffmpeg";
        }
        int sel = CboEncoder.SelectedIndex;
        CboEncoder.Items.Clear();
        CboEncoder.Items.Add(EncoderNames.Display(EncoderKind.Auto));
        CboEncoder.Items.Add(detected);
        CboEncoder.Items.Add(EncoderNames.Display(EncoderKind.SoftwareX264));
        CboEncoder.SelectedIndex = sel >= 0 ? sel : 0;
    }

    // ── 录制流程 ───────────────────────────────────
    private void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleRecord();

    private void ToggleRecord()
    {
        if (_session is { IsRecording: true })
        {
            _session.Stop();
            return;
        }
        StartRecording();
    }

    private void TogglePause()
    {
        if (_session is not { IsRecording: true })
            return;
        if (_session.IsPaused)
            _session.Resume();
        else
            _session.Pause();
    }

    private void StartRecording(RecordingOptions? preset = null)
    {
        if (_session is { IsRecording: true })
            return;

        RecordingOptions opts;
        if (preset != null)
        {
            opts = preset;
        }
        else
        {
            var built = BuildOptions();
            if (built == null)
                return;
            opts = built;
            _softwareRetryUsed = false;
        }
        _lastOptions = opts;

        try
        {
            var ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();
            _session = new RecordingSession(opts, ffmpeg);
            _session.Completed += OnSessionCompleted;
            _session.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动录制失败：" + ex.Message, "QSrcRecorder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _session = null;
            return;
        }

        SaveSettings();
        Hide();
        _bar = new RecordingBarForm(_session);
        _bar.Show();
        SetStatus("正在录制…");
    }

    private RecordingOptions? BuildOptions()
    {
        int screenIndex = CboScreen.SelectedIndex >= 0 ? CboScreen.SelectedIndex : 0;
        var folder = TxtFolder.Text.Trim();
        var baseOpts = new RecordingOptions
        {
            Fps = int.Parse((string)(CboFps.SelectedItem ?? "30")),
            Quality = (QualityPreset)Math.Max(0, CboQuality.SelectedIndex),
            Scale = CboScale.SelectedIndex switch { 1 => 0.75, 2 => 0.5, _ => 1.0 },
            Encoder = CboEncoder.SelectedIndex switch
            {
                1 => EncoderKind.Auto,   // 探测结果按 Auto 处理（含降级机制）
                2 => EncoderKind.SoftwareX264,
                _ => EncoderKind.Auto,
            },
            OutputFolder = folder,
        };

        if (ModeWindow.IsChecked == true)
        {
            if (_window == null || _window.Hwnd == IntPtr.Zero || !Win32Native.IsWindowVisible(_window.Hwnd))
            {
                MessageBox.Show(this, "请先点击“选择…”指定要录制的窗口。", "QSrcRecorder",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return baseOpts with { Mode = RecordMode.Window, WindowHandle = _window.Hwnd };
        }

        var screens = WinFormsScreenHelper.Screens;
        var screen = screens.Length > 0
            ? screens[Math.Clamp(screenIndex, 0, screens.Length - 1)]
            : screens[0];
        var monitorHandle = Win32Native.HMonitorFromRectangle(screen.Bounds);

        if (ModeRegion.IsChecked == true)
        {
            using var selector = new RegionSelectorForm(screen);
            if (selector.ShowDialog() != System.Windows.Forms.DialogResult.OK || selector.SelectedRect.Width < 8)
                return null;
            var rect = selector.SelectedRect;
            rect.Offset(-screen.Bounds.X, -screen.Bounds.Y); // 转为显示器相对坐标
            return baseOpts with { Mode = RecordMode.Region, MonitorHandle = monitorHandle, Region = rect };
        }

        return baseOpts with { Mode = RecordMode.FullScreen, MonitorHandle = monitorHandle };
    }

    private void OnSessionCompleted(RecordingResult r)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _bar?.Close();
            _bar = null;
            _session = null;

            // 双显卡笔记本上硬编可能间歇不可用：刚起步失败时自动改软编按原参数重录一次
            if (r.IsEarlyEncoderFailure && !_softwareRetryUsed && _lastOptions != null)
            {
                _softwareRetryUsed = true;
                HardwareEncoderDetector.DemoteToSoftware();
                SetStatus("硬件编码暂不可用，已自动改用软件编码重新开始录制…");
                StartRecording(_lastOptions with { Encoder = EncoderKind.SoftwareX264 });
                return;
            }

            Show();
            Activate();

            if (r.Success)
            {
                SetStatus($"已保存：{Path.GetFileName(r.OutputPath)}（{r.Duration:hh\\:mm\\:ss}）");
                var message = $"录制完成，是否打开所在文件夹？\n{r.OutputPath}";
                if (r.StopReason != null)
                    message = r.StopReason + "\n\n" + message;
                if (MessageBox.Show(this, message, "QSrcRecorder",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start("explorer.exe", $"/select,\"{r.OutputPath}\"");
                }
            }
            else
            {
                SetStatus("录制失败");
                string detail = r.Error ?? r.StopReason ?? "未知原因（输出文件未生成）";
                MessageBox.Show(this, "录制失败：\n" + detail, "QSrcRecorder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    // ── 界面事件 ───────────────────────────────────
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 IsChecked=True 会触发本事件，此时后续字段尚未初始化
        if (ModeWindow == null || ScreenRow == null || WindowRow == null)
            return;
        bool windowMode = ModeWindow.IsChecked == true;
        ScreenRow.Visibility = windowMode ? Visibility.Collapsed : Visibility.Visible;
        WindowRow.Visibility = windowMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PickWindow_Click(object sender, RoutedEventArgs e)
    {
        // WPF 原生窗口选择器：WinForms 版在 WPF 宿主里 ShowDialog 会抛未处理异常
        var picker = new WindowPickerView { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected != null)
        {
            _window = picker.Selected;
            string title = _window.Title.Length > 30 ? _window.Title[..30] + "…" : _window.Title;
            WindowName.Text = $"{_window.ProcessName} — {title}";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // WPF 原生文件夹选择（.NET 8 OpenFolderDialog，模态、自带前置，
        // 旧版 WinForms 对话框在 WPF 宿主里弹不出来，是"路径无法显现"的根源）
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择录制的保存位置",
        };
        if (Directory.Exists(TxtFolder.Text))
            dlg.InitialDirectory = TxtFolder.Text;
        if (dlg.ShowDialog(this) == true)
            TxtFolder.Text = dlg.FolderName;
    }

    private void TxtFolder_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // 悬停显示完整路径（框内显示不下时）
        TxtFolder.ToolTip = TxtFolder.Text;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(TxtFolder.Text))
            Process.Start(new ProcessStartInfo(TxtFolder.Text) { UseShellExecute = true });
    }

    // ── 辅助 ───────────────────────────────────────
    private void SetStatus(string text) => StatusText.Text = text;

    private void RefreshScreens()
    {
        var screens = WinFormsScreenHelper.Screens;
        CboScreen.Items.Clear();
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = s.Primary ? "主显示器" : $"显示器 {i + 1}";
            CboScreen.Items.Add($"{label}  ({s.Bounds.Width}×{s.Bounds.Height})");
        }
        CboScreen.SelectedIndex = 0;
    }

    private void LoadSettings()
    {
        _settings = AppSettings.Load();
        (_settings.Mode switch
        {
            "Region" => ModeRegion,
            "Window" => ModeWindow,
            _ => ModeFull,
        }).IsChecked = true;
        if (CboScreen.Items.Count > _settings.ScreenIndex && _settings.ScreenIndex >= 0)
            CboScreen.SelectedIndex = _settings.ScreenIndex;
        int fpsIdx = CboFps.Items.IndexOf(_settings.Fps.ToString());
        if (fpsIdx >= 0 && fpsIdx < CboFps.Items.Count)
            CboFps.SelectedIndex = fpsIdx;
        if (CboScale.Items.Count > Math.Clamp(_settings.ScaleIndex, 0, 2))
            CboScale.SelectedIndex = Math.Clamp(_settings.ScaleIndex, 0, 2);
        if (CboQuality.Items.Count > Math.Clamp(_settings.Quality, 0, 2))
            CboQuality.SelectedIndex = Math.Clamp(_settings.Quality, 0, 2);
        TxtFolder.Text = string.IsNullOrWhiteSpace(_settings.OutputFolder)
            ? OutputFile.DefaultFolder()
            : _settings.OutputFolder;
        CboFps.SelectionChanged += (_, _) => SaveSettings();
        CboScale.SelectionChanged += (_, _) => SaveSettings();
        CboQuality.SelectionChanged += (_, _) => SaveSettings();
    }

    private void SaveSettings()
    {
        if (!IsLoaded)
            return;
        _settings.Mode = ModeWindow.IsChecked == true ? "Window"
            : ModeRegion.IsChecked == true ? "Region" : "FullScreen";
        _settings.ScreenIndex = CboScreen.SelectedIndex;
        _settings.Fps = int.Parse((string)(CboFps.SelectedItem ?? "30"));
        _settings.ScaleIndex = CboScale.SelectedIndex;
        _settings.Quality = CboQuality.SelectedIndex;
        _settings.OutputFolder = TxtFolder.Text.Trim();
        _settings.Save();
    }
}

/// <summary>WPF 里枚举显示器仍借 WinForms 的 Screen。</summary>
internal static class WinFormsScreenHelper
{
    public static System.Windows.Forms.Screen[] Screens => System.Windows.Forms.Screen.AllScreens;
}
