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

public class ClickHighlightTests
{
    private static (int BigAlpha, double RippleR, int RippleA) Animate(long t) =>
        ScreenRecorder.Overlays.ClickHighlightEngine.Animate(0, t);

    [Fact]
    public void 动画起始_大圆全亮且波纹从圆心开始()
    {
        var (big, rr, ra) = Animate(0);
        Assert.Equal(ScreenRecorder.Overlays.ClickHighlightEngine.BigAlphaStart, big);
        Assert.Equal(ScreenRecorder.Overlays.ClickHighlightEngine.RippleStartRadius, rr, 1);
        Assert.Equal(ScreenRecorder.Overlays.ClickHighlightEngine.RippleAlphaStart, ra);
    }

    [Fact]
    public void 动画中段_大圆变淡波纹扩大()
    {
        var mid = (long)(ScreenRecorder.Overlays.ClickHighlightEngine.DurationMs / 2);
        var (big, rr, ra) = Animate(mid);
        Assert.True(big < ScreenRecorder.Overlays.ClickHighlightEngine.BigAlphaStart);
        Assert.True(rr > ScreenRecorder.Overlays.ClickHighlightEngine.RippleStartRadius);
        Assert.True(rr <= ScreenRecorder.Overlays.ClickHighlightEngine.RippleEndRadius);
        Assert.True(ra < ScreenRecorder.Overlays.ClickHighlightEngine.RippleAlphaStart);
        Assert.True(ra > 0);
    }

    [Fact]
    public void 动画结束_返回零即不再绘制()
    {
        var (big, rr, ra) = Animate((long)(ScreenRecorder.Overlays.ClickHighlightEngine.DurationMs + 1));
        Assert.Equal(0, big);
        Assert.Equal(0, rr);
        Assert.Equal(0, ra);
    }

    [Fact]
    public void 波纹先于大圆结束_波纹归零后大圆仍在淡出()
    {
        var t = (long)ScreenRecorder.Overlays.ClickHighlightEngine.RippleDurationMs + 10;
        var (big, rr, ra) = Animate(t);
        Assert.Equal(0, rr);
        Assert.Equal(0, ra);
        Assert.True(big > 0, "大圆应继续淡出直到总时长结束");
    }

    [Theory]
    [InlineData("#DC2626", 0x26, 0x26, 0xDC)]
    [InlineData("#3B82F6", 0xF6, 0x82, 0x3B)]
    [InlineData("", 0x26, 0x26, 0xDC)]      // 空 → 默认红
    [InlineData("xyz", 0x26, 0x26, 0xDC)]   // 非法 → 默认红
    public void 颜色解析_hex转BGRA(string hex, byte b, byte g, byte r)
    {
        var (pb, pg, pr) = ScreenRecorder.Overlays.ClickHighlightEngine.ParseColor(hex);
        Assert.Equal(b, pb);
        Assert.Equal(g, pg);
        Assert.Equal(r, pr);
    }

    [Fact]
    public void 全屏模式_坐标减监视器原点再乘缩放()
    {
        var rect = new Rectangle(100, 50, 1920, 1080);
        var (x, y) = ScreenRecorder.Overlays.ClickHighlightEngine.ScreenToFrame(
            2100, 590, RecordMode.FullScreen, rect, 0.5, null, null);
        Assert.Equal(1000, x); // (2100-100)*0.5
        Assert.Equal(270, y);  // (590-50)*0.5
    }

    [Fact]
    public void 区域模式_先缩放再减裁剪原点()
    {
        var rect = new Rectangle(0, 0, 1920, 1080);
        var crop = new Rectangle(100, 50, 800, 600);
        var (x, y) = ScreenRecorder.Overlays.ClickHighlightEngine.ScreenToFrame(
            500, 300, RecordMode.Region, rect, 1.0, crop, null);
        Assert.Equal(400, x); // 500-100
        Assert.Equal(250, y); // 300-50
    }

    [Fact]
    public void 窗口模式_使用客户区坐标回调()
    {
        var rect = new Rectangle(0, 0, 1920, 1080);
        var (x, y) = ScreenRecorder.Overlays.ClickHighlightEngine.ScreenToFrame(
            100, 100, RecordMode.Window, rect, 1.0, null,
            (sx, sy) => (sx - 10, sy - 20)); // 模拟窗口在屏幕 (10,20)
        Assert.Equal(90, x);
        Assert.Equal(80, y);
    }

    [Fact]
    public void 画圆环_中心像素被写入混合颜色()
    {
        // 帧 100x60，圆环中心 (50,30)，半径 10 → 中心是环上点（d=0 不在环带）？
        // 用半径 10 环带 inner=7.2：测试取环上点 (50+9, 30) 即 d=9 在 [7.2,10]
        var frame = new byte[100 * 60 * 4];
        ScreenRecorder.Overlays.ClickHighlightEngine.DrawCircleRing(
            frame, 100, 60, 400, 50, 30, 10, 200, 0x26, 0x26, 0xDC);

        int i = (30 * 400) + (59 * 4); // (59,30)：d=9 在环带上
        Assert.True(frame[i + 2] > 0x80, "R 分量应显著混合（红色通道提升）");
    }
}

public class MouseHighlightFillTests
{
    [Fact]
    public void 填充圆_中心像素被混合上颜色()
    {
        var frame = new byte[100 * 60 * 4];
        ScreenRecorder.Overlays.ClickHighlightEngine.DrawCircleFill(
            frame, 100, 60, 400, 50, 30, 10, 110, 230, 0x26, 0x26, 0xDC);

        int i = (30 * 400) + (50 * 4); // 圆心
        // fillAlpha=110/255 → R = 0xDC*110/255 ≈ 95
        Assert.Equal(94, frame[i + 2]);
    }

    [Fact]
    public void 填充圆_边缘描边比中心更浓()
    {
        var frame = new byte[100 * 60 * 4];
        ScreenRecorder.Overlays.ClickHighlightEngine.DrawCircleFill(
            frame, 100, 60, 400, 50, 30, 10, 110, 230, 0x26, 0x26, 0xDC);

        int center = (30 * 400) + (50 * 4);
        int edge = (30 * 400) + (59 * 4);   // 距圆心 9px，处于边缘描边带
        int outside = (30 * 400) + (20 * 4); // 距圆心 30px，圆外
        Assert.True(frame[edge + 2] > frame[center + 2], "边缘 alpha 更高 → R 更浓");
        Assert.Equal(0, frame[outside + 2]); // 圆外不混合
    }

    [Fact]
    public void 填充圆_零透明度_不改动像素()
    {
        var frame = new byte[100 * 60 * 4];
        ScreenRecorder.Overlays.ClickHighlightEngine.DrawCircleFill(
            frame, 100, 60, 400, 50, 30, 10, 0, 0, 0xFF, 0, 0);
        int i = (30 * 400) + (50 * 4);
        Assert.Equal(0, frame[i + 2]);
    }
}

public class PipCompositorTests
{
    [Fact]
    public void ComputeRect_右下角贴边且偶数尺寸()
    {
        var r = ScreenRecorder.Capture.PipCompositor.ComputeRect(1920, 1080,
            ScreenRecorder.Capture.PipCorner.BottomRight, sizeIndex: 1, marginPx: 12);
        Assert.True(r.Width >= 2 && r.Height >= 2);
        Assert.Equal(0, r.Width % 2);
        Assert.Equal(0, r.Height % 2);
        Assert.Equal(1920 - 12 - r.Width, r.X);
        Assert.Equal(1080 - 12 - r.Height, r.Y);
    }

    [Theory]
    [InlineData(ScreenRecorder.Capture.PipCorner.TopLeft, true, true)]
    [InlineData(ScreenRecorder.Capture.PipCorner.TopRight, false, true)]
    [InlineData(ScreenRecorder.Capture.PipCorner.BottomLeft, true, false)]
    [InlineData(ScreenRecorder.Capture.PipCorner.BottomRight, false, false)]
    public void ComputeRect_四角位置(
        ScreenRecorder.Capture.PipCorner corner, bool nearLeft, bool nearTop)
    {
        var r = ScreenRecorder.Capture.PipCompositor.ComputeRect(800, 600, corner, 0, 10);
        if (nearLeft) Assert.True(r.X <= 10);
        else Assert.True(r.X + r.Width >= 800 - 10);
        if (nearTop) Assert.True(r.Y <= 10);
        else Assert.True(r.Y + r.Height >= 600 - 10);
    }

    [Fact]
    public void Blit_缩放写入目标区且不越界()
    {
        int dw = 100, dh = 80;
        var dest = new byte[dw * dh * 4];
        // 2x2 源：纯红
        var src = new byte[]
        {
            0, 0, 255, 255,  0, 0, 255, 255,
            0, 0, 255, 255,  0, 0, 255, 255,
        };
        var rect = new System.Drawing.Rectangle(10, 10, 20, 16);
        ScreenRecorder.Capture.PipCompositor.Blit(dest, dw, dh, src, 2, 2, rect, mirrorX: false, drawBorder: false);

        int i = (18 * dw + 15) * 4; // 矩形内部
        Assert.Equal(255, dest[i + 2]); // R
        // 矩形外仍为 0
        Assert.Equal(0, dest[0]);
    }

    [Fact]
    public void Blit_镜像后左右像素对调()
    {
        int dw = 40, dh = 20;
        var dest = new byte[dw * dh * 4];
        // 2x1：左蓝右红
        var src = new byte[]
        {
            255, 0, 0, 255, // B
            0, 0, 255, 255, // R
        };
        var rect = new System.Drawing.Rectangle(0, 0, 2, 1);
        ScreenRecorder.Capture.PipCompositor.Blit(dest, dw, dh, src, 2, 1, rect, mirrorX: true, drawBorder: false);
        // 镜像后 dest[0] 应为原右边红，dest[1] 为原左边蓝
        Assert.Equal(255, dest[2]); // R at x=0
        Assert.Equal(255, dest[4]); // B at x=1
    }

    [Fact]
    public void ParseCorner_非法回退右下()
    {
        Assert.Equal(ScreenRecorder.Capture.PipCorner.BottomRight,
            ScreenRecorder.Capture.PipCompositor.ParseCorner(null));
        Assert.Equal(ScreenRecorder.Capture.PipCorner.TopLeft,
            ScreenRecorder.Capture.PipCompositor.ParseCorner("TopLeft"));
    }
}
