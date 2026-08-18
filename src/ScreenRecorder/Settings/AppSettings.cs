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
    /// <summary>录制时显示鼠标点击高亮光圈。</summary>
    public bool ClickHighlight { get; set; } = true;
    /// <summary>点击高亮光圈颜色（#RRGGBB）。</summary>
    public string ClickHighlightColor { get; set; } = "#DC2626";
    /// <summary>录制时鼠标周围常驻半透明跟随圆。</summary>
    public bool MouseHighlight { get; set; } = true;
    /// <summary>颜色选择色环是否展开。</summary>
    public bool ColorWheelExpanded { get; set; }
    /// <summary>同时录制麦克风声音（音频单独合成，需几秒钟处理时间）。</summary>
    public bool RecordAudio { get; set; }
    /// <summary>同时录制系统声音（WASAPI 回环）。与麦克风独立合成。</summary>
    public bool RecordSystemAudio { get; set; }
    /// <summary>麦克风音量倍率（0.1 ~ 5.0，默认 1.0）。</summary>
    public double MicVolume { get; set; } = 1.0;
    /// <summary>系统声音音量倍率（0.1 ~ 5.0，默认 1.0）。</summary>
    public double SysVolume { get; set; } = 1.0;
    /// <summary>麦克风降噪门（过滤低于阈值的环境噪音）。</summary>
    public bool MicNoiseGate { get; set; }
    /// <summary>音效调节面板是否展开。</summary>
    public bool AudioEffectExpanded { get; set; }
    /// <summary>系统声音低音增强（-5 ~ +5 dB，默认 0）。</summary>
    public int SysBass { get; set; }
    /// <summary>系统声音高音增强（-5 ~ +5 dB，默认 0）。</summary>
    public int SysTreble { get; set; }
    /// <summary>录制时在成片叠摄像头人像（画中画）。</summary>
    public bool WebcamEnabled { get; set; }
    /// <summary>摄像头设备 Id（空=默认第一台）。</summary>
    public string WebcamDeviceId { get; set; } = "";
    /// <summary>画中画角落：TopLeft / TopRight / BottomLeft / BottomRight。</summary>
    public string WebcamCorner { get; set; } = "BottomRight";
    /// <summary>画中画大小：0 小 / 1 中 / 2 大。</summary>
    public int WebcamSizeIndex { get; set; } = 1;
    /// <summary>摄像头画面水平镜像。</summary>
    public bool WebcamMirror { get; set; } = true;
    /// <summary>画中画使用自定义位置（true）还是预设角落（false）。</summary>
    public bool WebcamCustomPosition { get; set; }
    /// <summary>画中画左上角 X 相对屏幕宽度的比例（0~1），自定义模式下有效。</summary>
    public double WebcamPosX { get; set; } = 0.75;
    /// <summary>画中画左上角 Y 相对屏幕高度的比例（0~1），自定义模式下有效。</summary>
    public double WebcamPosY { get; set; } = 0.75;
    /// <summary>画中画宽度相对屏幕宽度的比例（0~1），自定义模式下有效。</summary>
    public double WebcamSizeW { get; set; } = 0.22;
    /// <summary>画中画高度相对屏幕高度的比例（0~1），自定义模式下有效。</summary>
    public double WebcamSizeH { get; set; } = 0.16;

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
