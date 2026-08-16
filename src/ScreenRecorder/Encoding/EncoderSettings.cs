namespace ScreenRecorder.Encoding;

public enum EncoderKind
{
    Auto,
    SoftwareX264,
    Nvenc,
    Qsv,
    Amf,
}

public enum QualityPreset
{
    High,
    Medium,
    Low,
}

public sealed record EncoderSettings(EncoderKind Encoder, int Fps, QualityPreset Quality, int Width, int Height);

public static class EncoderNames
{
    public static string Display(EncoderKind kind) => kind switch
    {
        EncoderKind.Auto => "自动（优先硬件编码）",
        EncoderKind.SoftwareX264 => "软件编码 x264（兼容性最好）",
        EncoderKind.Nvenc => "硬件编码 NVIDIA NVENC",
        EncoderKind.Qsv => "硬件编码 Intel 核显 QuickSync",
        EncoderKind.Amf => "硬件编码 AMD AMF",
        _ => kind.ToString(),
    };
}
