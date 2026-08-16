using System;
using System.IO;
using System.Text.Json;

namespace ScreenRecorder.Settings;

public sealed class AppSettings
{
    public string Mode { get; set; } = "FullScreen";
    public int ScreenIndex { get; set; }
    public int Fps { get; set; } = 30;
    public int ScaleIndex { get; set; } = 0;  // 0=100% 1=75% 2=50%
    public int Quality { get; set; } = 1;     // QualityPreset 下标
    public int Encoder { get; set; } = 0;     // EncoderKind 下标
    public string OutputFolder { get; set; } = "";

    private static string FolderPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QSrcRecorder");

    private static string LegacyFolderPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();

            // 软件曾用名 ScreenRecorder：迁移旧配置，保留用户已有的选择
            string legacy = Path.Combine(LegacyFolderPath, "settings.json");
            if (File.Exists(legacy))
            {
                var old = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacy));
                if (old != null)
                {
                    old.Save();
                    return old;
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不影响使用
        }
    }
}
