using System.Drawing;
using System.Drawing.Drawing2D;

int w = 1920, h = 1080;
using var bmp = new Bitmap(w, h);
using var g = Graphics.FromImage(bmp);
g.SmoothingMode = SmoothingMode.AntiAlias;
g.InterpolationMode = InterpolationMode.HighQualityBicubic;
g.CompositingQuality = CompositingQuality.HighQuality;
g.Clear(Color.FromArgb(255, 4, 7, 12));

void Glow(float cx, float cy, float rx, float ry, Color center)
{
    using var path = new GraphicsPath();
    path.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
    using var b = new PathGradientBrush(path)
    {
        CenterColor = center,
        SurroundColors = new[] { Color.FromArgb(0, center.R, center.G, center.B) }
    };
    g.FillEllipse(b, cx - rx, cy - ry, rx * 2, ry * 2);
}

Glow(260, 860, 780, 460, Color.FromArgb(110, 0, 50, 90));
Glow(480, 940, 560, 300, Color.FromArgb(80, 0, 190, 200));
Glow(1540, 160, 700, 380, Color.FromArgb(70, 20, 50, 140));
Glow(1180, 760, 520, 240, Color.FromArgb(50, 0, 140, 180));
Glow(900, 420, 300, 180, Color.FromArgb(28, 40, 120, 200));

var rng = new Random(11);
for (int i = 0; i < 520; i++)
{
    int x = rng.Next(w), y = rng.Next(h);
    int s = rng.Next(100) > 94 ? 3 : 1;
    int al = rng.Next(30, 210);
    using var brush = new SolidBrush(Color.FromArgb(al, 220, 240, 255));
    g.FillEllipse(brush, x, y, s, s);
}
for (int i = 0; i < 22; i++)
{
    float x = rng.Next(30, w - 30), y = rng.Next(30, h - 30);
    Glow(x, y, 26, 26, Color.FromArgb(60, 90, 220, 255));
    using var core = new SolidBrush(Color.FromArgb(240, 235, 250, 255));
    g.FillEllipse(core, x - 1.4f, y - 1.4f, 2.8f, 2.8f);
}

for (int i = 0; i < 8; i++)
{
    float y1 = rng.Next(60, 980);
    using var pen = new Pen(Color.FromArgb(36, 70, 210, 255), 1.1f + (i % 3));
    pen.StartCap = LineCap.Round;
    pen.EndCap = LineCap.Round;
    g.DrawBezier(pen, -60, y1, 480, y1 - 200, 1280, y1 + 160, 2000, y1 - 40);
}

var pts = new PointF[52];
for (int i = 0; i < pts.Length; i++)
    pts[i] = new PointF(rng.Next(w), rng.Next(h));
using (var mesh = new Pen(Color.FromArgb(32, 100, 210, 235), 1f))
{
    for (int i = 0; i < pts.Length; i++)
        for (int j = i + 1; j < pts.Length; j++)
        {
            float dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
            if (dx * dx + dy * dy < 210 * 210)
                g.DrawLine(mesh, pts[i], pts[j]);
        }
}
using (var node = new SolidBrush(Color.FromArgb(120, 140, 235, 255)))
{
    foreach (var p in pts)
        g.FillEllipse(node, p.X - 1.7f, p.Y - 1.7f, 3.4f, 3.4f);
}

Glow(w / 2f, h / 2f, w * 0.78f, h * 0.78f, Color.FromArgb(0, 0, 0, 0));
using (var edge = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
{
    g.FillRectangle(edge, 0, 0, w, 80);
    g.FillRectangle(edge, 0, h - 90, w, 90);
}

string dir = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ScreenRecorder", "Assets"));
Directory.CreateDirectory(dir);
string outPath = Path.Combine(dir, "hud-bg.png");
bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
Console.WriteLine(outPath);
