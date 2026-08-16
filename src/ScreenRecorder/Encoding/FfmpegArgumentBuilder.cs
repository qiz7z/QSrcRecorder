namespace ScreenRecorder.Encoding;

/// <summary>
/// 生成 ffmpeg 命令行参数（以数组形式返回，经 ProcessStartInfo.ArgumentList 传入，无空格转义问题）。
/// </summary>
public static class FfmpegArgumentBuilder
{
    public static string[] BuildArgs(EncoderSettings s, string outputPath)
    {
        var list = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", $"{s.Width}x{s.Height}",
            "-framerate", s.Fps.ToString(),
            "-i", "pipe:0",
        };
        list.AddRange(CodecArgs(s));
        list.Add("-movflags");
        list.Add("+faststart");
        list.Add("-y");
        list.Add(outputPath);
        return list.ToArray();
    }

    /// <summary>只生成编码部分参数（便于单元测试）。</summary>
    public static string[] CodecArgs(EncoderSettings s) => s.Encoder switch
    {
        EncoderKind.Nvenc =>
        [
            "-c:v", "h264_nvenc",
            "-preset", "p4",
            "-rc", "vbr",
            "-cq", CqFor(s.Quality).ToString(),
            "-b:v", "0",
            // 直接上传 BGRA 给 NVENC，色彩转换在 GPU 内完成，
            // 避免 ffmpeg 在读入侧做 CPU 色彩转换（那是管道吞吐的瓶颈）
        ],
        EncoderKind.Qsv =>
        [
            "-c:v", "h264_qsv",
            "-global_quality", GqFor(s.Quality).ToString(),
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",
        ],
        EncoderKind.Amf =>
        [
            "-c:v", "h264_amf",
            "-quality", "balanced",
            "-rc", "vbr_peak",
            "-b:v", $"{AmfBitrateMbps(s)}M",
            "-maxrate", $"{AmfBitrateMbps(s) * 2}M",
            "-pix_fmt", "yuv420p",
        ],
        _ =>
        [
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", CrfFor(s.Quality).ToString(),
            // 限制软编线程数，给采集与界面留出 CPU 余量
            "-threads", SoftwareThreadCount().ToString(),
            "-pix_fmt", "yuv420p",
        ],
    };

    /// <summary>软编线程数：物理核心的一半，2~6 之间。全开会把界面时间器饿死。</summary>
    public static int SoftwareThreadCount()
        => Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private static int CrfFor(QualityPreset q) => q switch
    {
        QualityPreset.High => 18,
        QualityPreset.Medium => 23,
        _ => 28,
    };

    private static int CqFor(QualityPreset q) => q switch
    {
        QualityPreset.High => 19,
        QualityPreset.Medium => 23,
        _ => 27,
    };

    private static int GqFor(QualityPreset q) => q switch
    {
        QualityPreset.High => 20,
        QualityPreset.Medium => 24,
        _ => 28,
    };

    /// <summary>AMF 无恒定质量模式，按“每像素比特率 × 像素吞吐”估算目标码率（Mbps）。</summary>
    public static int AmfBitrateMbps(EncoderSettings s)
    {
        double bitsPerPixel = s.Quality switch
        {
            QualityPreset.High => 0.15,
            QualityPreset.Medium => 0.10,
            _ => 0.06,
        };
        double pixelsPerSecond = (double)s.Width * s.Height * s.Fps;
        int mbps = (int)Math.Round(pixelsPerSecond * bitsPerPixel / 1_000_000.0);
        return Math.Clamp(mbps, 2, 50);
    }
}
