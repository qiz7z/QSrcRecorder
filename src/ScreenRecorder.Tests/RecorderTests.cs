using System;
using System.Drawing;
using System.IO;
using ScreenRecorder;
using ScreenRecorder.Encoding;
using Xunit;

namespace ScreenRecorder.Tests;

public class FfmpegArgumentBuilderTests
{
    private static EncoderSettings S(EncoderKind kind = EncoderKind.SoftwareX264,
        int fps = 30, QualityPreset q = QualityPreset.Medium, int w = 1920, int h = 1080)
        => new(kind, fps, q, w, h);

    [Fact]
    public void BuildArgs_包含输入规格与输出参数()
    {
        var args = FfmpegArgumentBuilder.BuildArgs(S(w: 1280, h: 720), "out test.mp4");

        Assert.Contains("rawvideo", args);
        Assert.Contains("bgra", args);
        Assert.Equal("1280x720", args[Array.IndexOf(args, "-video_size") + 1]);
        Assert.Equal("30", args[Array.IndexOf(args, "-framerate") + 1]);
        Assert.Contains("pipe:0", args);
        Assert.Contains("+faststart", args);
        Assert.Equal("out test.mp4", args[^1]); // 路径作为最后一个参数原样传入
    }

    [Fact]
    public void 软件编码_使用libx264与对应crf()
    {
        var args = FfmpegArgumentBuilder.CodecArgs(S(EncoderKind.SoftwareX264, q: QualityPreset.Medium));

        Assert.Contains("libx264", args);
        Assert.Equal("23", args[Array.IndexOf(args, "-crf") + 1]);
        Assert.Contains("yuv420p", args);
    }

    [Theory]
    [InlineData(QualityPreset.High, "18")]
    [InlineData(QualityPreset.Medium, "23")]
    [InlineData(QualityPreset.Low, "28")]
    public void 软件编码_画质映射crf(QualityPreset q, string crf)
    {
        var args = FfmpegArgumentBuilder.CodecArgs(S(EncoderKind.SoftwareX264, q: q));
        Assert.Equal(crf, args[Array.IndexOf(args, "-crf") + 1]);
    }

    [Fact]
    public void NVENC_使用h264_nvenc与cq()
    {
        var args = FfmpegArgumentBuilder.CodecArgs(S(EncoderKind.Nvenc));
        Assert.Contains("h264_nvenc", args);
        Assert.Contains("-cq", args);
    }

    [Fact]
    public void QSV_使用h264_qsv与global_quality()
    {
        var args = FfmpegArgumentBuilder.CodecArgs(S(EncoderKind.Qsv));
        Assert.Contains("h264_qsv", args);
        Assert.Contains("-global_quality", args);
    }

    [Fact]
    public void AMF_码率在合理区间()
    {
        // 1080p30 中等画质 ≈ 6Mbps
        Assert.Equal(6, FfmpegArgumentBuilder.AmfBitrateMbps(S(EncoderKind.Amf)));
        // 8K60 也会被钳制到 50
        Assert.Equal(50, FfmpegArgumentBuilder.AmfBitrateMbps(S(EncoderKind.Amf, fps: 60, w: 7680, h: 4320)));
        // 极小画面不小于 2
        Assert.Equal(2, FfmpegArgumentBuilder.AmfBitrateMbps(S(EncoderKind.Amf, w: 320, h: 240)));
    }
}

public class RegionMathTests
{
    [Fact]
    public void 正常矩形_原样保留()
    {
        var r = RegionMath.PrepareCrop(new Rectangle(100, 50, 640, 480), 1920, 1080);
        Assert.Equal(new Rectangle(100, 50, 640, 480), r);
    }

    [Fact]
    public void 奇数尺寸_取偶()
    {
        var r = RegionMath.PrepareCrop(new Rectangle(0, 0, 641, 481), 1920, 1080);
        Assert.Equal(640, r.Width);
        Assert.Equal(480, r.Height);
    }

    [Fact]
    public void 超出边界_钳制()
    {
        var r = RegionMath.PrepareCrop(new Rectangle(1800, 1000, 500, 500), 1920, 1080);
        Assert.True(r.Right <= 1920);
        Assert.True(r.Bottom <= 1080);
        Assert.Equal(0, r.Width % 2);
        Assert.Equal(0, r.Height % 2);
    }

    [Fact]
    public void 过小区域_抛异常()
    {
        Assert.Throws<InvalidOperationException>(
            () => RegionMath.PrepareCrop(new Rectangle(0, 0, 1, 1), 1920, 1080));
    }

    [Fact]
    public void 极小但合法的区域_钳制到最小偶数尺寸()
    {
        var r = RegionMath.PrepareCrop(new Rectangle(10, 10, 3, 3), 1920, 1080);
        Assert.Equal(2, r.Width);
        Assert.Equal(2, r.Height);
    }
}

public class OutputFileTests
{
    [Fact]
    public void 输出路径_命名与目录创建()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sr_test_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string path = OutputFile.BuildPath(folder);
            Assert.True(Directory.Exists(folder));
            Assert.StartsWith("屏幕录制_", Path.GetFileName(path));
            Assert.EndsWith(".mp4", Path.GetFileName(path));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}

public class RecordingResultTests
{
    private static RecordingResult Make(EncoderKind kind, long frames, string? error, bool success = false)
        => new()
        {
            EncoderUsed = kind,
            FrameCount = frames,
            Error = error,
            Success = success,
        };

    [Fact]
    public void 硬件编码_刚起步失败_应触发自动降级重录()
    {
        var r = Make(EncoderKind.Nvenc, 30, "ffmpeg 中途退出，写入管道失败");
        Assert.True(r.IsEarlyEncoderFailure);
    }

    [Fact]
    public void 软件编码失败_不重录()
    {
        var r = Make(EncoderKind.SoftwareX264, 30, "ffmpeg 中途退出");
        Assert.False(r.IsEarlyEncoderFailure);
    }

    [Fact]
    public void 已录制较长后失败_不自动重录()
    {
        var r = Make(EncoderKind.Nvenc, 900, "ffmpeg 中途退出");
        Assert.False(r.IsEarlyEncoderFailure);
    }

    [Fact]
    public void 无错误信息_不重录()
    {
        var r = Make(EncoderKind.Nvenc, 30, null);
        Assert.False(r.IsEarlyEncoderFailure);
    }
}
