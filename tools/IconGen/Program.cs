using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// QSrcRecorder 应用图标生成器（ui-ux-pro-max 设计指导版）
// 风格：Vibrant & Block-based（高对比双色、几何构成、duotone）+ 品牌红 #DC2626
// 三个候选方案：
//   A「屏幕·录制中」：白色屏幕 + 右上角录制红点（录屏语义最直接）
//   B「摄像机」    ：白色机身 + 白环红心镜头（摄像机隐喻）
//   C「镜头」      ：白色光圈 + 中心红点（现代几何符号）
// 输出：各方案 ICO（16/24/32/48/64/128/256）+ 三行对比拼图；主方案 A 写入项目 Assets

const string OutDir = @"C:\Users\liuqi\Desktop\屏幕录制软件\tools\IconGen\out";
const string AssetIco = @"C:\Users\liuqi\Desktop\屏幕录制软件\src\ScreenRecorder\Assets\QSrcRecorder.ico";
Directory.CreateDirectory(OutDir);

int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
(string name, Func<int, Bitmap> render)[] designs =
{
    ("A_屏幕录制中", RenderScreen),
    ("B_摄像机", RenderCamera),
    ("C_镜头", RenderLens),
};

// ── 三行对比拼图 ────────────────────────────────
using (var sheet = new Bitmap(40 + sizes.Length * 120, 30 + designs.Length * 118, PixelFormat.Format32bppArgb))
using (var sg = Graphics.FromImage(sheet))
{
    sg.Clear(Color.FromArgb(248, 250, 252));
    using var font = new Font("Segoe UI", 11f);
    for (int r = 0; r < designs.Length; r++)
    {
        sg.DrawString(designs[r].name, font, Brushes.DimGray, 14, 12 + r * 118);
        for (int c = 0; c < sizes.Length; c++)
        {
            using var icon = designs[r].render(sizes[c]);
            int x = 40 + c * 120 + (120 - sizes[c]) / 2;
            int y = 12 + r * 118 + (84 - sizes[c]) / 2;
            sg.DrawImage(icon, x, y, sizes[c], sizes[c]);
        }
    }
    sheet.Save(Path.Combine(OutDir, "preview_combined.png"), ImageFormat.Png);
}

// ── 输出 ICO ────────────────────────────────────
WriteIco(Path.Combine(OutDir, "A_screen.ico"), RenderScreen);
WriteIco(Path.Combine(OutDir, "B_camera.ico"), RenderCamera);
WriteIco(Path.Combine(OutDir, "C_lens.ico"), RenderLens);
WriteIco(AssetIco, RenderScreen); // 主方案：A，先安装到项目

// ── 像素校验（替代目视：验证构图/颜色符合设计） ──
PixelCheck("A", RenderScreen(256), ('T', 2, 2), ('R', 60, 60), ('W', 128, 150), ('R', 168, 96));
PixelCheck("B", RenderCamera(256), ('T', 2, 2), ('R', 60, 60), ('W', 80, 150), ('R', 128, 152));
PixelCheck("C", RenderLens(256), ('T', 2, 2), ('R', 60, 60), ('W', 128, 60), ('R', 128, 128));
Console.WriteLine($"完成：{OutDir}（拼图 + 3 个 ICO），主方案已写入 {AssetIco}");

// ══════════════ 绘制 ══════════════

static Bitmap NewCanvas(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    return bmp;
}

/// <summary>红色渐变圆角底 + 顶部高光（品牌红 #DC2626 系）。</summary>
static void DrawBackground(Graphics g, float s, float u)
{
    var bgRect = new RectangleF(s * 0.05f, s * 0.05f, s * 0.90f, s * 0.90f);
    using (var bgPath = RoundedRect(bgRect, s * 0.22f))
    using (var grad = new LinearGradientBrush(bgRect,
               Color.FromArgb(255, 238, 82, 74),   // #EE524A
               Color.FromArgb(255, 172, 24, 28),   // #AC181C
               90f))
    {
        g.FillPath(grad, bgPath);
    }
    var hiRect = new RectangleF(s * 0.05f, s * 0.05f, s * 0.90f, s * 0.55f);
    using (var hiPath = RoundedRect(hiRect, s * 0.22f))
    using (var hiGrad = new LinearGradientBrush(hiRect,
               Color.FromArgb(64, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
    {
        g.FillPath(hiGrad, hiPath);
    }
}

/// <summary>镜头质感：中心亮红 → 外圈深红。</summary>
static Brush LensBrush(RectangleF rect)
{
    var path = new GraphicsPath();
    path.AddEllipse(rect);
    var pg = new PathGradientBrush(path);
    pg.CenterColor = Color.FromArgb(255, 255, 96, 90);
    pg.SurroundColors = new[] { Color.FromArgb(255, 170, 20, 28) };
    path.Dispose();
    return pg;
}

static void DrawLensHighlight(Graphics g, float cx, float cy, float dotR)
{
    float hr = dotR * 0.32f;
    using var hp = new GraphicsPath();
    hp.AddEllipse(cx - dotR * 0.38f - hr, cy - dotR * 0.42f - hr, 2 * hr, 2 * hr);
    g.FillPath(new SolidBrush(Color.FromArgb(170, 255, 255, 255)), hp);
}

/// <summary>方案 A：白色屏幕 + 右上角录制红点。</summary>
static Bitmap RenderScreen(int size)
{
    float s = size, u = s / 256f;
    var bmp = NewCanvas(size);
    using var g = Graphics.FromImage(bmp);
    DrawBackground(g, s, u);

    var screen = new RectangleF(56f * u, 64f * u, 144f * u, 128f * u);
    using (var path = RoundedRect(screen, 18f * u))
        g.FillPath(Brushes.White, path);

    float dotR = (size >= 48 ? 15f : size >= 24 ? 16f : 17f) * u;
    float cx = 168f * u, cy = 96f * u;
    using var dot = new GraphicsPath();
    dot.AddEllipse(cx - dotR, cy - dotR, 2 * dotR, 2 * dotR);
    using (var lens = LensBrush(new RectangleF(cx - dotR, cy - dotR, 2 * dotR, 2 * dotR)))
        g.FillPath(lens, dot);
    if (size >= 48)
        DrawLensHighlight(g, cx, cy, dotR);
    return bmp;
}

/// <summary>方案 B：白色摄像机机身（+顶部取景器）+ 白环红心镜头。</summary>
static Bitmap RenderCamera(int size)
{
    float s = size, u = s / 256f;
    var bmp = NewCanvas(size);
    using var g = Graphics.FromImage(bmp);
    DrawBackground(g, s, u);

    if (size >= 48)
    {
        var grip = new RectangleF(96f * u, 82f * u, 64f * u, 34f * u);
        using var gripPath = RoundedRect(grip, 12f * u);
        g.FillPath(Brushes.White, gripPath);
    }

    var body = new RectangleF(64f * u, 112f * u, 128f * u, 80f * u);
    using (var bodyPath = RoundedRect(body, 16f * u))
        g.FillPath(Brushes.White, bodyPath);

    float outerR = (size >= 48 ? 30f : 26f) * u;
    float innerR = outerR * 0.70f;
    float cx = 128f * u, cy = 152f * u;
    using (var ring = new GraphicsPath())
    {
        ring.AddEllipse(cx - outerR, cy - outerR, 2 * outerR, 2 * outerR);
        ring.AddEllipse(cx - innerR, cy - innerR, 2 * innerR, 2 * innerR);
        g.FillPath(Brushes.White, ring);
    }
    using var heart = new GraphicsPath();
    heart.AddEllipse(cx - innerR, cy - innerR, 2 * innerR, 2 * innerR);
    using (var lens = LensBrush(new RectangleF(cx - innerR, cy - innerR, 2 * innerR, 2 * innerR)))
        g.FillPath(lens, heart);
    if (size >= 48)
        DrawLensHighlight(g, cx, cy, innerR);
    return bmp;
}

/// <summary>方案 C：白色光圈 + 中心红点（镜头符号）。</summary>
static Bitmap RenderLens(int size)
{
    float s = size, u = s / 256f;
    var bmp = NewCanvas(size);
    using var g = Graphics.FromImage(bmp);
    DrawBackground(g, s, u);

    float outerR = (size >= 48 ? 74f : 68f) * u;
    float thickness = (size >= 48 ? 15f : 12f) * u;
    float innerR = outerR - thickness;
    float cx = 128f * u, cy = 128f * u;
    using (var ring = new GraphicsPath())
    {
        ring.AddEllipse(cx - outerR, cy - outerR, 2 * outerR, 2 * outerR);
        ring.AddEllipse(cx - innerR, cy - innerR, 2 * innerR, 2 * innerR);
        g.FillPath(Brushes.White, ring);
    }
    float dotR = innerR * 0.60f;
    using var heart = new GraphicsPath();
    heart.AddEllipse(cx - dotR, cy - dotR, 2 * dotR, 2 * dotR);
    using (var lens = LensBrush(new RectangleF(cx - dotR, cy - dotR, 2 * dotR, 2 * dotR)))
        g.FillPath(lens, heart);
    if (size >= 48)
        DrawLensHighlight(g, cx, cy, dotR);
    return bmp;
}

// ══════════════ 工具 ══════════════

static void WriteIco(string path, Func<int, Bitmap> render)
{
    int[] sizeList = { 256, 128, 64, 48, 32, 24, 16 };
    var images = sizeList.Select(render).ToList();
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write((short)0);
    bw.Write((short)1);
    bw.Write((short)images.Count);

    var pngs = images.Select(img =>
    {
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }).ToList();

    int offset = 6 + 16 * images.Count;
    for (int i = 0; i < images.Count; i++)
    {
        int sz = sizeList[i];
        bw.Write((byte)(sz >= 256 ? 0 : sz));
        bw.Write((byte)(sz >= 256 ? 0 : sz));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(pngs[i].Length);
        bw.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) bw.Write(p);
}

static void PixelCheck(string name, Bitmap bmp, params (char kind, int x, int y)[] points)
{
    foreach (var (kind, x, y) in points)
    {
        Color c = bmp.GetPixel(x, y);
        bool ok = kind switch
        {
            'T' => c.A == 0,
            'R' => c.R > 170 && c.G < 130 && c.B < 130,
            'W' => c.R > 200 && c.G > 200 && c.B > 200,
            _ => false,
        };
        if (!ok)
            throw new InvalidOperationException(
                $"方案 {name} 像素校验失败 ({x},{y}): 实际 {c}，期望 {kind}");
    }
    Console.WriteLine($"方案 {name} 像素校验通过：透明角 / 红底 / 白主体 / 红点 均符合设计。");
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    float d = radius * 2f;
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}
