using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using ScreenRecorder.Capture;
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
    private ScreenRecorder.Overlays.MouseHighlightOverlay? _spot;
    private PipOverlayWindow? _pipOverlay;
    private RecordingOptions? _lastOptions;
    private bool _softwareRetryUsed;
    private bool _starting; // 防止启动过程中重复点击/热键
    private Win32Native.WinWindowInfo? _window;
    private AppSettings _settings = new();
    private bool _loadingSettings; // 初始化期间阻止事件处理器触发 SaveSettings
    private readonly ScreenRecorder.Overlays.ClickHighlightEngine _clickEngine = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private bool _trayVisible;

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

        CboWebcamCorner.Items.Add("右下");
        CboWebcamCorner.Items.Add("左下");
        CboWebcamCorner.Items.Add("右上");
        CboWebcamCorner.Items.Add("左上");
        CboWebcamCorner.SelectedIndex = 0;
        CboWebcamSize.Items.Add("小");
        CboWebcamSize.Items.Add("中");
        CboWebcamSize.Items.Add("大");
        CboWebcamSize.SelectedIndex = 1;

        Loaded += (_, _) =>
        {
            RefreshScreens();
            LoadSettings();
            _ = DetectEncoderAsync();
            _ = LoadWebcamDevicesAsync();
            _clickEngine.Start();
            InitTray();
        };
        Closing += (_, _) =>
        {
            SaveSettings();
            _clickEngine.Stop();
            _tray?.Dispose();
            _tray = null;
        };
        StateChanged += (_, _) =>
        {
            // 最小化 → 隐藏到托盘（录制中悬浮条一起隐藏，画面干净）
            if (WindowState == WindowState.Minimized && !_trayVisible)
            {
                _trayVisible = true;
                _tray!.Visible = true;
                Hide();
                _bar?.Hide();
            }
        };
    }

    // ── 系统托盘 ─────────────────────────────────
    private System.Windows.Forms.ToolStripMenuItem? _trayPauseItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayStopItem;

    private void InitTray()
    {
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayPauseItem = new System.Windows.Forms.ToolStripMenuItem("暂停");
        _trayPauseItem.Click += (_, _) => TogglePause();
        _trayStopItem = new System.Windows.Forms.ToolStripMenuItem("结束录制");
        _trayStopItem.Click += (_, _) => ToggleRecord();
        var miShow = new System.Windows.Forms.ToolStripMenuItem("显示主界面");
        miShow.Click += (_, _) => RestoreFromTray();
        var miExit = new System.Windows.Forms.ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitApp();
        _trayMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { _trayPauseItem, _trayStopItem, miShow, new System.Windows.Forms.ToolStripSeparator(), miExit });
        // 菜单打开时按录制状态刷新"暂停/结束"可用性与文案
        _trayMenu.Opening += (_, _) => RefreshTrayMenu();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "QSrcRecorder 拾光留影",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!,
            ContextMenuStrip = _trayMenu,
            Visible = false,
        };
        // 左键点击托盘：显示计时器悬浮条（未录制则显示主界面）
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if (_session is { IsRecording: true })
                    ShowBarFromTray();
                else
                    RestoreFromTray();
            }
        };
    }

    private void RefreshTrayMenu()
    {
        bool recording = _session is { IsRecording: true };
        if (_trayPauseItem != null)
        {
            _trayPauseItem.Enabled = recording;
            _trayPauseItem.Text = _session is { IsPaused: true } ? "继续" : "暂停";
        }
        if (_trayStopItem != null)
        {
            _trayStopItem.Enabled = recording;
            _trayStopItem.Text = "结束录制";
        }
    }

    private void RestoreFromTray()
    {
        _trayVisible = false;
        if (_tray != null)
            _tray.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // 录制中恢复窗口时，悬浮条也恢复（方便控制录制）
        if (_session is { IsRecording: true } && _bar != null)
            _bar.Show();
    }

    /// <summary>托盘左键/菜单"显示计时器"：恢复悬浮条，托盘图标保留以便随时再隐藏。</summary>
    private void ShowBarFromTray()
    {
        if (_session is not { IsRecording: true } || _bar == null)
            return;
        _bar.Show();
        _trayVisible = false; // 悬浮条已恢复，录制结束走正常提示
        // 托盘图标保留（录制期间可随时再点隐藏）
        if (_tray != null)
            _tray.Visible = true;
        UpdateTrayText("正在录制…");
    }

    private void ExitApp()
    {
        try
        {
            _session?.Stop();
        }
        catch
        {
            // 停止失败也照常退出
        }
        _clickEngine.Stop();
        _tray?.Dispose();
        _tray = null;
        Close();
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
    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] CLICK\n");
        try { MessageBox.Show(this, "按钮点击了！开始录制...", "DEBUG", MessageBoxButton.OK); } catch { }
        ToggleRecord();
    }

    /// <summary>必须用 async void，确保异常能传播到 DispatcherUnhandledException 并弹窗。</summary>
    private async void ToggleRecord()
    {
        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] TOGGLE: starting={_starting}, session={_session != null}\n");
        if (_starting)
            return;
        if (_session is { IsRecording: true })
        {
            _session.Stop();
            return;
        }
        try
        {
            await StartRecordingAsync();
            System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: OK\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: THREW {ex.GetType().Name}: {ex.Message}\n");
            MessageBox.Show(this, "录制启动失败：" + ex.Message, "QSrcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    private void StartRecording(RecordingOptions? preset = null) => _ = StartRecordingAsync(preset);

    /// <summary>
    /// 启动录制：UI 立即响应（状态 + 隐藏主窗），重初始化（D3D/WGC/等首帧/硬编探测/ffmpeg）放到后台线程，避免点按钮卡顿。
    /// </summary>
    private async Task StartRecordingAsync(RecordingOptions? preset = null)
    {
        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: enter\n");
        if (_starting || _session is { IsRecording: true })
        {
            System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: early return\n");
            return;
        }

        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: building opts\n");
        RecordingOptions opts;
        if (preset != null)
        {
            opts = preset;
        }
        else
        {
            var built = BuildOptions();
            if (built == null)
            {
                System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: BuildOptions returned null\n");
                return;
            }
            opts = built;
            _softwareRetryUsed = false;
        }
        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: opts={opts.Mode}\n");
        _lastOptions = opts;

        bool clickHighlight = _settings.ClickHighlight;
        string clickColor = _settings.ClickHighlightColor;
        bool mouseHighlight = _settings.MouseHighlight;
        bool webcamEnabled = _settings.WebcamEnabled;
        var clickEngine = clickHighlight ? _clickEngine : null;
        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: webcamEnabled={webcamEnabled}\n");

        // 共享摄像头实例：session 和 overlay 共用同一个，避免双重占用
        // 摄像头初始化放后台线程，避免阻塞 UI 线程导致死锁
        WebcamCapture? sharedWebcam = null;
        if (webcamEnabled)
        {
            try
            {
                System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: starting webcam async\n");
                sharedWebcam = await Task.Run(() =>
                {
                    System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: webcam Task.Run start\n");
                    var wc = new WebcamCapture();
                    wc.Start(string.IsNullOrWhiteSpace(_settings.WebcamDeviceId) ? null : _settings.WebcamDeviceId);
                    System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: webcam Task.Run OK\n");
                    return wc;
                }).ConfigureAwait(true);
                System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: webcam await completed\n");
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: webcam FAILED: {ex.GetType().Name}: {ex.Message}\n");
                MessageBox.Show(this, "摄像头打开失败：" + ex.Message, "QSrcRecorder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                sharedWebcam = null;
            }
        }
        System.IO.File.AppendAllText(@"C:\Users\liuqi\Desktop\qsrc_diag.txt", $"[{DateTime.Now:HH:mm:ss.fff}] SAR: webcam={sharedWebcam != null}\n");

        _starting = true;
        SetStatus("正在启动录制…");
        UpdateTrayText("正在启动录制…");
        SaveSettings();
        // 先隐藏主窗，让用户立刻感到已响应；失败时再恢复
        if (!_trayVisible)
            Hide();

        RecordingSession? session = null;
        try
        {
            string ffmpeg = FfmpegVideoEncoder.LocateFfmpeg();
            // D3D/WGC/等首帧/硬编探测/ffmpeg 拉起都较重；WGC 使用 CreateFreeThreaded，可在后台线程初始化
            session = await Task.Run(() =>
            {
                var s = new RecordingSession(opts, ffmpeg, clickEngine, clickColor, mouseHighlight, sharedWebcam);
                s.Start();
                return s;
            }).ConfigureAwait(true);

            session.Completed += OnSessionCompleted;
            _session = session;

            // 托盘模式（窗口已隐藏）录制时不显示悬浮条——画面干净；
            // 正常模式录制时显示悬浮条，可点悬浮条"隐藏"按钮收进托盘
            if (!_trayVisible)
            {
                _bar = new RecordingBarForm(_session);
                _bar.HideRequested += OnBarHideRequested;
                _bar.Show();
            }
            SetStatus("正在录制…");
            UpdateTrayText("正在录制…");

            // 鼠标跟随圆：屏幕实时覆盖层（全屏/区域模式会随画面录进成片；窗口模式由软件合帧兜底）
            if (mouseHighlight)
            {
                try
                {
                    _spot = new ScreenRecorder.Overlays.MouseHighlightOverlay();
                    _spot.SetColor(clickColor);
                    _spot.Show();
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "spot_diag.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] _spot 创建失败: {ex}\n");
                }
            }

            // 画中画预览覆盖层：录制时可见位置，用户可拖动/缩放调整框位
            if (sharedWebcam != null && _session != null)
            {
                try
                {
                    _pipOverlay = new PipOverlayWindow(sharedWebcam);
                    _pipOverlay.Show();
                    _pipOverlay.Start();
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "pip_overlay_diag.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] 画中画覆盖层创建失败: {ex}\n");
                }
            }
        }
        catch (Exception ex)
        {
            try { session?.Dispose(); } catch { /* ignore */ }
            _session = null;
            _bar?.Close();
            _bar = null;
            _spot?.Close();
            _spot = null;
            _pipOverlay?.HideAndClear();
            _pipOverlay = null;
            if (!_trayVisible)
            {
                Show();
                Activate();
            }
            MessageBox.Show(this, "启动录制失败：" + ex.Message, "QSrcRecorder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("就绪");
            UpdateTrayText("就绪");
        }
        finally
        {
            _starting = false;
        }
    }

    private void UpdateTrayText(string text)
    {
        if (_tray != null)
            _tray.Text = "QSrcRecorder · " + text;
    }

    /// <summary>悬浮条"隐藏"按钮：收进托盘，录制画面干净；托盘可恢复。</summary>
    private void OnBarHideRequested()
    {
        _bar?.Hide();
        _trayVisible = true;
        if (_tray != null)
            _tray.Visible = true;
        UpdateTrayText("正在录制…（悬浮条已隐藏，双击托盘恢复）");
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
            RecordAudio = _settings.RecordAudio,
            RecordSystemAudio = _settings.RecordSystemAudio,
            MicVolume = _settings.MicVolume,
            SysVolume = _settings.SysVolume,
            MicNoiseGate = _settings.MicNoiseGate,
            SysBass = _settings.SysBass,
            SysTreble = _settings.SysTreble,
            WebcamEnabled = _settings.WebcamEnabled,
            WebcamDeviceId = _settings.WebcamDeviceId,
            WebcamCorner = _settings.WebcamCorner,
            WebcamSizeIndex = Math.Clamp(_settings.WebcamSizeIndex, 0, 2),
            WebcamMirror = _settings.WebcamMirror,
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
            _spot?.Close();
            _spot = null;
            _pipOverlay?.HideAndClear();
            _pipOverlay = null;
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

            // 托盘模式下结束录制：不弹窗不打扰，状态通过托盘提示
            if (_trayVisible)
            {
                UpdateTrayText(r.Success ? "录制完成" : "录制失败");
                if (!r.Success)
                    _tray?.ShowBalloonTip(3000, "QSrcRecorder", "录制失败：" + (r.Error ?? r.StopReason ?? "未知原因"), System.Windows.Forms.ToolTipIcon.Warning);
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
                if (!string.IsNullOrEmpty(r.AudioWarning))
                    message = "⚠ " + r.AudioWarning + "\n\n" + message;
                if (!string.IsNullOrEmpty(r.WebcamWarning))
                    message = "⚠ " + r.WebcamWarning + "\n\n" + message;
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

    // ── 点击高亮设置 ─────────────────────────────
    private void ClickHighlight_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool on = ChkClickHighlight.IsChecked == true;
        _settings.ClickHighlight = on;
        RefreshColorRow();
        SaveSettings();
    }

    private void MouseHighlight_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool on = ChkMouseHighlight.IsChecked == true;
        _settings.MouseHighlight = on;
        RefreshColorRow();
        SaveSettings();
    }

    private void AudioRecord_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.RecordAudio = ChkAudioRecord.IsChecked == true;
        SaveSettings();
        AudioStatusText.Visibility = _settings.RecordAudio ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.RecordAudio)
            AudioStatusText.Text = "● 开启麦克风录制（结束合成时可能需几秒钟）";
    }

    private void SystemAudioRecord_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.RecordSystemAudio = ChkSystemAudioRecord.IsChecked == true;
        SaveSettings();
    }

    // ── 摄像头人像 ─────────────────────────────────
    private async Task LoadWebcamDevicesAsync()
    {
        try
        {
            var devices = await Task.Run(() => WebcamCapture.EnumerateDevices()).ConfigureAwait(true);
            _loadingSettings = true;
            CboWebcamDevice.Items.Clear();
            if (devices.Count == 0)
            {
                CboWebcamDevice.Items.Add(new WebcamDeviceInfo("", "（未检测到摄像头）"));
                CboWebcamDevice.SelectedIndex = 0;
                WebcamStatusText.Text = "未检测到摄像头设备";
                WebcamStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                int sel = 0;
                for (int i = 0; i < devices.Count; i++)
                {
                    CboWebcamDevice.Items.Add(devices[i]);
                    if (!string.IsNullOrEmpty(_settings.WebcamDeviceId)
                        && devices[i].Id == _settings.WebcamDeviceId)
                        sel = i;
                }
                CboWebcamDevice.SelectedIndex = sel;
                CboWebcamDevice.DisplayMemberPath = nameof(WebcamDeviceInfo.Name);
                WebcamStatusText.Visibility = Visibility.Collapsed;
            }
            _loadingSettings = false;
        }
        catch (Exception ex)
        {
            _loadingSettings = false;
            WebcamStatusText.Text = "枚举摄像头失败：" + ex.Message;
            WebcamStatusText.Visibility = Visibility.Visible;
        }
    }

    private void Webcam_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.WebcamEnabled = ChkWebcam.IsChecked == true;
        WebcamOptionsPanel.Visibility = _settings.WebcamEnabled ? Visibility.Visible : Visibility.Collapsed;
        SaveSettings();
    }

    private void WebcamOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        PullWebcamSettingsFromUi();
        SaveSettings();
    }

    private void WebcamOption_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        PullWebcamSettingsFromUi();
        SaveSettings();
    }

    private void PullWebcamSettingsFromUi()
    {
        _settings.WebcamEnabled = ChkWebcam.IsChecked == true;
        if (CboWebcamDevice.SelectedItem is WebcamDeviceInfo dev && !string.IsNullOrEmpty(dev.Id))
            _settings.WebcamDeviceId = dev.Id;
        _settings.WebcamCorner = CboWebcamCorner.SelectedIndex switch
        {
            1 => "BottomLeft",
            2 => "TopRight",
            3 => "TopLeft",
            _ => "BottomRight",
        };
        _settings.WebcamSizeIndex = Math.Clamp(CboWebcamSize.SelectedIndex, 0, 2);
        _settings.WebcamMirror = ChkWebcamMirror.IsChecked != false;
    }

    private void ApplyWebcamSettingsToUi()
    {
        ChkWebcam.IsChecked = _settings.WebcamEnabled;
        WebcamOptionsPanel.Visibility = _settings.WebcamEnabled ? Visibility.Visible : Visibility.Collapsed;
        CboWebcamCorner.SelectedIndex = _settings.WebcamCorner switch
        {
            "BottomLeft" => 1,
            "TopRight" => 2,
            "TopLeft" => 3,
            _ => 0,
        };
        CboWebcamSize.SelectedIndex = Math.Clamp(_settings.WebcamSizeIndex, 0, 2);
        ChkWebcamMirror.IsChecked = _settings.WebcamMirror;
    }

    // ── 音效调节 ──────────────────────────────────
    private void SldMicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings) return;
        _settings.MicVolume = Math.Round(e.NewValue, 1);
        MicVolText.Text = $"音量 {_settings.MicVolume:N1}×";
        SaveSettings();
    }

    private void SldSysVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings) return;
        _settings.SysVolume = Math.Round(e.NewValue, 1);
        SysVolText.Text = $"音量 {_settings.SysVolume:N1}×";
        SaveSettings();
    }

    private void MicNoiseGate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.MicNoiseGate = ChkMicNoiseGate.IsChecked == true;
        SaveSettings();
    }

    private void BtnBassDown_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.SysBass = Math.Max(-5, _settings.SysBass - 1);
        SysBassText.Text = _settings.SysBass.ToString();
        SaveSettings();
    }

    private void BtnBassUp_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.SysBass = Math.Min(5, _settings.SysBass + 1);
        SysBassText.Text = _settings.SysBass.ToString();
        SaveSettings();
    }

    private void BtnTrebleDown_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.SysTreble = Math.Max(-5, _settings.SysTreble - 1);
        SysTrebleText.Text = _settings.SysTreble.ToString();
        SaveSettings();
    }

    private void BtnTrebleUp_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.SysTreble = Math.Min(5, _settings.SysTreble + 1);
        SysTrebleText.Text = _settings.SysTreble.ToString();
        SaveSettings();
    }

    private void RefreshColorRow()
    {
        ColorRow.Visibility = (_settings.ClickHighlight || _settings.MouseHighlight)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ColorWheel_ColorChanged(object? sender, EventArgs e)
    {
        if (ColorWheel is { } wheel)
        {
            _settings.ClickHighlightColor = wheel.HexValue;
            SaveSettings();
            UpdateColorDot(wheel.HexValue);
            UpdatePresetSelection(wheel.HexValue);
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex })
            return;
        _settings.ClickHighlightColor = hex;
        SaveSettings();
        UpdateColorDot(hex);
        UpdatePresetSelection(hex);
        ColorWheel.SetColor(hex); // 色环指示点同步
    }

    private void UpdatePresetSelection(string hex)
    {
        foreach (var btn in new[]
                 {
                     Preset1, Preset2, Preset3, Preset4,
                     Preset5, Preset6, Preset7, Preset8,
                 })
        {
            bool selected = string.Equals(btn.Tag as string, hex, StringComparison.OrdinalIgnoreCase);
            btn.BorderBrush = new System.Windows.Media.SolidColorBrush(
                selected
                    ? System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A) // 深色描边 = 选中
                    : System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
            btn.BorderThickness = selected ? new Thickness(2) : new Thickness(1.5);
        }
    }

    private void ColorToggle_Click(object sender, RoutedEventArgs e)
    {
        bool expanded = ColorWheel.Visibility != Visibility.Visible;
        _settings.ColorWheelExpanded = expanded;
        SaveSettings();
        ApplyColorWheelState(expanded);
    }

    private void ApplyColorWheelState(bool expanded)
    {
        ColorWheel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ColorToggleText.Text = expanded ? "颜色 ▴" : "颜色 ▾";
    }

    private void AudioEffectToggle_Click(object sender, RoutedEventArgs e)
    {
        bool expanded = AudioEffectPanel.Visibility != Visibility.Visible;
        _settings.AudioEffectExpanded = expanded;
        SaveSettings();
        ApplyAudioEffectState(expanded);
    }

    private void ApplyAudioEffectState(bool expanded)
    {
        AudioEffectPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AudioEffectToggleText.Text = expanded ? "音效 ▴" : "音效 ▾";
    }

    private void UpdateColorDot(string hex)
    {
        if (ColorDot is not { } dot)
            return;
        var (b, g, r) = Overlays.ClickHighlightEngine.ParseColor(hex);
        dot.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(r, g, b));
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
        _loadingSettings = true;
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
        ChkClickHighlight.IsChecked = _settings.ClickHighlight;
        ChkMouseHighlight.IsChecked = _settings.MouseHighlight;
        ColorRow.Visibility = (_settings.ClickHighlight || _settings.MouseHighlight)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyColorWheelState(_settings.ColorWheelExpanded);
        ApplyAudioEffectState(_settings.AudioEffectExpanded);
        ColorWheel.SetColor(_settings.ClickHighlightColor);
        UpdateColorDot(_settings.ClickHighlightColor);
        UpdatePresetSelection(_settings.ClickHighlightColor);
        ChkAudioRecord.IsChecked = _settings.RecordAudio;
        ChkSystemAudioRecord.IsChecked = _settings.RecordSystemAudio;
        SldMicVolume.Value = _settings.MicVolume;
        SldSysVolume.Value = _settings.SysVolume;
        MicVolText.Text = $"音量 {_settings.MicVolume:N1}×";
        SysVolText.Text = $"音量 {_settings.SysVolume:N1}×";
        ChkMicNoiseGate.IsChecked = _settings.MicNoiseGate;
        SysBassText.Text = _settings.SysBass.ToString();
        SysTrebleText.Text = _settings.SysTreble.ToString();
        ApplyWebcamSettingsToUi();
        SldMicVolume.ValueChanged += (s, e) => SldMicVolume_ValueChanged(s, e);
        SldSysVolume.ValueChanged += (s, e) => SldSysVolume_ValueChanged(s, e);
        CboFps.SelectionChanged += (_, _) => SaveSettings();
        CboScale.SelectionChanged += (_, _) => SaveSettings();
        CboQuality.SelectionChanged += (_, _) => SaveSettings();
        _loadingSettings = false;
    }

    private void SaveSettings()
    {
        if (!IsLoaded || _loadingSettings)
            return;
        _settings.Mode = ModeWindow.IsChecked == true ? "Window"
            : ModeRegion.IsChecked == true ? "Region" : "FullScreen";
        _settings.ScreenIndex = CboScreen.SelectedIndex;
        _settings.Fps = int.Parse((string)(CboFps.SelectedItem ?? "30"));
        _settings.ScaleIndex = CboScale.SelectedIndex;
        _settings.Quality = CboQuality.SelectedIndex;
        _settings.OutputFolder = TxtFolder.Text.Trim();
        _settings.RecordAudio = ChkAudioRecord.IsChecked == true;
        _settings.RecordSystemAudio = ChkSystemAudioRecord.IsChecked == true;
        PullWebcamSettingsFromUi();
        _settings.Save();
    }
}

/// <summary>WPF 里枚举显示器仍借 WinForms 的 Screen。</summary>
internal static class WinFormsScreenHelper
{
    public static System.Windows.Forms.Screen[] Screens => System.Windows.Forms.Screen.AllScreens;
}
