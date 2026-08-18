using System;
using System.Drawing;

namespace ScreenRecorder.Capture;

/// <summary>画中画位置（相对输出帧）。</summary>
public enum PipCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// 摄像头画中画纯逻辑：布局矩形 + BGRA blit（最近邻缩放、可选水平镜像、细白边）。
/// </summary>
public static class PipCompositor
{
    /// <summary>大小档：短边百分比。</summary>
    public static double SizePercent(int sizeIndex) => sizeIndex switch
    {
        0 => 0.15,
        2 => 0.30,
        _ => 0.22,
    };

    /// <summary>
    /// 计算 PiP 目标矩形（偶数宽高，贴边留 margin）。
    /// 以输出帧短边 * sizePercent 为基准高度，按摄像头宽高比（默认 16:9）定宽。
    /// </summary>
    public static Rectangle ComputeRect(
        int frameW, int frameH, PipCorner corner, int sizeIndex, int marginPx = 12,
        int sourceW = 0, int sourceH = 0)
    {
        if (frameW < 4 || frameH < 4)
            return Rectangle.Empty;

        int margin = Math.Clamp(marginPx, 0, Math.Min(frameW, frameH) / 4);
        double pct = SizePercent(sizeIndex);
        int baseSide = Math.Max(2, (int)(Math.Min(frameW, frameH) * pct));

        double aspect = 16.0 / 9.0;
        if (sourceW > 0 && sourceH > 0)
            aspect = sourceW / (double)sourceH;

        // 以高度为基准，再按真实宽高比算宽（避免 4:3 摄像头被拉扁）
        int h = baseSide & ~1;
        int w = Math.Max(2, (int)Math.Round(h * aspect) & ~1);

        int maxW = Math.Max(2, (frameW - margin * 2) & ~1);
        int maxH = Math.Max(2, (frameH - margin * 2) & ~1);
        if (w > maxW || h > maxH)
        {
            double sx = maxW / (double)w;
            double sy = maxH / (double)h;
            double s = Math.Min(sx, sy);
            w = Math.Max(2, (int)(w * s) & ~1);
            h = Math.Max(2, (int)(h * s) & ~1);
        }

        int x = corner is PipCorner.TopRight or PipCorner.BottomRight
            ? frameW - margin - w
            : margin;
        int y = corner is PipCorner.BottomLeft or PipCorner.BottomRight
            ? frameH - margin - h
            : margin;

        x = Math.Clamp(x, 0, Math.Max(0, frameW - w));
        y = Math.Clamp(y, 0, Math.Max(0, frameH - h));
        return new Rectangle(x, y, w, h);
    }

    public static PipCorner ParseCorner(string? s) => s switch
    {
        "TopLeft" => PipCorner.TopLeft,
        "TopRight" => PipCorner.TopRight,
        "BottomLeft" => PipCorner.BottomLeft,
        _ => PipCorner.BottomRight,
    };

    public static string CornerToString(PipCorner c) => c switch
    {
        PipCorner.TopLeft => "TopLeft",
        PipCorner.TopRight => "TopRight",
        PipCorner.BottomLeft => "BottomLeft",
        _ => "BottomRight",
    };

    /// <summary>
    /// 将 src(BGRA) 最近邻缩放到 destRect，可选水平镜像；外圈画 2px 近白描边。
    /// </summary>
    public static void Blit(
        byte[] dest, int destW, int destH,
        byte[] src, int srcW, int srcH,
        Rectangle destRect,
        bool mirrorX,
        bool drawBorder = true)
    {
        if (dest == null || src == null || srcW < 1 || srcH < 1 || destW < 1 || destH < 1)
            return;
        if (destRect.Width < 1 || destRect.Height < 1)
            return;

        int destPitch = destW * 4;
        int srcPitch = srcW * 4;
        if (dest.Length < destPitch * destH || src.Length < srcPitch * srcH)
            return;

        int x0 = Math.Max(0, destRect.X);
        int y0 = Math.Max(0, destRect.Y);
        int x1 = Math.Min(destW, destRect.X + destRect.Width);
        int y1 = Math.Min(destH, destRect.Y + destRect.Height);
        if (x0 >= x1 || y0 >= y1)
            return;

        for (int y = y0; y < y1; y++)
        {
            int sy = (int)((long)(y - destRect.Y) * srcH / destRect.Height);
            if (sy < 0) sy = 0;
            if (sy >= srcH) sy = srcH - 1;
            int srcRow = sy * srcPitch;
            int dstRow = y * destPitch;

            for (int x = x0; x < x1; x++)
            {
                int dx = x - destRect.X;
                int sx = (int)((long)dx * srcW / destRect.Width);
                if (mirrorX)
                    sx = srcW - 1 - sx;
                if (sx < 0) sx = 0;
                if (sx >= srcW) sx = srcW - 1;

                int si = srcRow + sx * 4;
                int di = dstRow + x * 4;
                dest[di] = src[si];
                dest[di + 1] = src[si + 1];
                dest[di + 2] = src[si + 2];
                dest[di + 3] = 255;
            }
        }

        if (drawBorder)
            DrawBorder(dest, destW, destH, destRect, borderPx: 2);
    }

    /// <summary>在 destRect 内侧画近白描边（不越界）。</summary>
    public static void DrawBorder(byte[] dest, int destW, int destH, Rectangle rect, int borderPx = 2)
    {
        if (borderPx < 1 || rect.Width < 1 || rect.Height < 1)
            return;

        int pitch = destW * 4;
        byte b = 245, g = 245, r = 250;
        int x0 = Math.Max(0, rect.X);
        int y0 = Math.Max(0, rect.Y);
        int x1 = Math.Min(destW, rect.X + rect.Width);
        int y1 = Math.Min(destH, rect.Y + rect.Height);
        if (x0 >= x1 || y0 >= y1)
            return;

        for (int y = y0; y < y1; y++)
        {
            bool edgeY = (y - rect.Y) < borderPx || (rect.Y + rect.Height - 1 - y) < borderPx;
            int row = y * pitch;
            for (int x = x0; x < x1; x++)
            {
                bool edgeX = (x - rect.X) < borderPx || (rect.X + rect.Width - 1 - x) < borderPx;
                if (!edgeX && !edgeY)
                    continue;
                int i = row + x * 4;
                dest[i] = b;
                dest[i + 1] = g;
                dest[i + 2] = r;
                dest[i + 3] = 255;
            }
        }
    }

    /// <summary>从全帧裁剪区域到紧密 BGRA 缓冲。</summary>
    public static void CopyCrop(
        byte[] src, int fullW, int fullH,
        Rectangle crop,
        byte[] dest)
    {
        int rowBytes = crop.Width * 4;
        int srcPitch = fullW * 4;
        for (int y = 0; y < crop.Height; y++)
        {
            int srcOff = (crop.Y + y) * srcPitch + crop.X * 4;
            Buffer.BlockCopy(src, srcOff, dest, y * rowBytes, rowBytes);
        }
    }
}
