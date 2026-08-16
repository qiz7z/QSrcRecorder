using System;
using System.Diagnostics;

namespace ScreenRecorder.Encoding;

/// <summary>
/// 启动时用一小段测试编码探测可用的硬件编码器（NVENC / QuickSync / AMF），结果按进程缓存。
/// </summary>
public static class HardwareEncoderDetector
{
    private static EncoderKind? _cached;

    public static EncoderKind Detect(string ffmpegPath)
    {
        if (_cached.HasValue)
            return _cached.Value;

        (EncoderKind Kind, string Codec)[] probes =
        [
            (EncoderKind.Nvenc, "h264_nvenc"),
            (EncoderKind.Qsv, "h264_qsv"),
            (EncoderKind.Amf, "h264_amf"),
        ];

        foreach (var (kind, codec) in probes)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                foreach (string arg in new[]
                {
                    "-hide_banner", "-loglevel", "error",
                    "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.3",
                    "-c:v", codec, "-f", "null", "-",
                })
                {
                    psi.ArgumentList.Add(arg);
                }

                using var p = Process.Start(psi);
                if (p == null)
                    continue;
                p.WaitForExit(15000);
                if (p.HasExited && p.ExitCode == 0)
                {
                    _cached = kind;
                    return kind;
                }
            }
            catch
            {
                // 探测失败（超时/找不到设备），尝试下一个
            }
        }

        _cached = EncoderKind.SoftwareX264;
        return EncoderKind.SoftwareX264;
    }

    public static void ResetCache() => _cached = null;

    /// <summary>
    /// 运行期发现硬件编码器实际不可用（探测通过但真录失败，双显卡机器常见）后调用：
    /// 本次进程内 Auto 一律解析为软件编码，避免每次录制都先失败一次。
    /// </summary>
    public static void DemoteToSoftware() => _cached = EncoderKind.SoftwareX264;
}
