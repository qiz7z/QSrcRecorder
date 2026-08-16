using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenRecorder;

internal static class Program
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QSrcRecorder", "logs");

    [STAThread]
    private static void Main()
    {
        // 兼容仍使用的 WinForms 覆盖层（悬浮条/区域选择器/窗口选择器）
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        try { System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2); } catch { }

        // 三层兜底：UI / 后台线程 / Task，全部记录并提示，绝不静默崩溃
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "未知"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(e.Exception);
            e.SetObserved();
        };

        var app = new System.Windows.Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Fatal(e.Exception);
            e.Handled = true;
        };

        app.Run(new UI.Wpf.MainView());
    }

    private static void Fatal(Exception ex)
    {
        Log(ex);
        try
        {
            System.Windows.MessageBox.Show(
                "程序遇到未处理的错误，已写入日志：\n" + LogDir + "\n\n" + ex.Message,
                "QSrcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 弹窗失败时日志已写好
        }
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(
                Path.Combine(LogDir, "errors.log"),
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n");
        }
        catch
        {
            // 日志失败无能为力，不再抛出
        }
    }
}
