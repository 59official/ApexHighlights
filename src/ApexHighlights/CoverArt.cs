using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ApexHighlights;

/// <summary>
/// 出视频封面：左边程序图标 ≈ 右边击倒骷髅。
///
/// 为什么用代码画而不是找图：左边那枚图标本来就是 <see cref="AppIcon.Render"/> 画的，
/// 直接复用同一份绘制逻辑，封面和托盘、exe 上的图标一定是同一个样子。
///
/// 用法：<c>ApexHighlights.exe --make-cover=cover.png [--cover-size=1920x1080]</c>
/// </summary>
internal static class CoverArt
{
    private static readonly Color Cyan = Color.FromArgb(57, 215, 232);
    private static readonly Color BgTop = Color.FromArgb(9, 11, 15);
    private static readonly Color BgBottom = Color.FromArgb(22, 26, 34);
    private static readonly Color Ink = Color.FromArgb(246, 248, 252);

    public static int Run(string path, int width = 1920, int height = 1080)
    {
        // 先按 2 倍画，再缩回去：GDI+ 的圆角和细线直接在小尺寸上画会有锯齿
        int w = width * 2, h = height * 2;
        using var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            // 所有坐标都按 1920×1080 写死，整体缩放一次：换个尺寸出图不用改任何常量
            g.ScaleTransform(w / 1920f, h / 1080f);
            Draw(g);
        }

        using var final = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(final))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, width, height));
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        final.Save(path, ImageFormat.Png);
        Console.WriteLine($"[cover] {path}  {width}x{height}");
        return 0;
    }

    private static void Draw(Graphics g)
    {
        const int W = 1920, H = 1080;
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H), BgTop, BgBottom, 90f))
            g.FillRectangle(bg, 0, 0, W, H);

        // 中间一团青色辉光：用径向渐变一次画完。早先是用 24 个低透明度同心圆叠出来的，
        // 放大看会留下一圈圈台阶（每一层的边界都是一条可见的环）。
        var haloPath = new GraphicsPath();
        float haloR = 780f;
        haloPath.AddEllipse(W / 2f - haloR, H / 2f - haloR, haloR * 2, haloR * 2);
        using (var halo = new PathGradientBrush(haloPath))
        {
            halo.CenterColor = Color.FromArgb(46, Cyan);
            halo.SurroundColors = new[] { Color.FromArgb(0, Cyan) };
            halo.FocusScales = new PointF(0.12f, 0.12f);
            g.FillPath(halo, haloPath);
        }
        haloPath.Dispose();

        float icon = 640, iconY = (H - icon) / 2f, gap = 360f;
        float leftX = W / 2f - gap / 2 - icon;
        float rightX = W / 2f + gap / 2;

        DrawAppIcon(g, leftX, iconY, icon);
        DrawSkullIcon(g, rightX, iconY, icon);
        DrawApprox(g, W / 2f, H / 2f);
    }

    private static void DrawAppIcon(Graphics g, float x, float y, float size)
    {
        using (var p = RoundRect(x, y, size, size, 148))
        using (var fill = new LinearGradientBrush(new Rectangle((int)x, (int)y, (int)size, (int)size),
                   Color.FromArgb(28, 32, 41), Color.FromArgb(13, 15, 20), 90f))
            g.FillPath(fill, p);

        float cx = x + size / 2, cy = y + size / 2;
        using (var ring = new Pen(Cyan, 25f))
            g.DrawEllipse(ring, cx - 185, cy - 185, 370, 370);

        using (var tick = new Pen(Cyan, 25f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(tick, cx, cy - 145, cx, cy - 245);      // 上
            g.DrawLine(tick, cx, cy + 145, cx, cy + 245);      // 下
            g.DrawLine(tick, cx - 145, cy, cx - 245, cy);      // 左
            g.DrawLine(tick, cx + 145, cy, cx + 245, cy);      // 右
        }

        using (var dot = new SolidBrush(Cyan))
            g.FillEllipse(dot, cx - 33, cy - 33, 66, 66);
    }

    private static void DrawSkullIcon(Graphics g, float x, float y, float size)
    {
        using (var p = RoundRect(x, y, size, size, 148))
        using (var fill = new LinearGradientBrush(new Rectangle((int)x, (int)y, (int)size, (int)size),
                   Color.FromArgb(8, 10, 14), Color.FromArgb(18, 22, 30), 90f))
        {
            g.FillPath(fill, p);
            using var border = new Pen(Color.FromArgb(31, 111, 235), 9f);
            g.DrawPath(border, p);
        }

        float bx = x + size / 2, by = y + size / 2 + 8;
        using var white = new SolidBrush(Ink);
        using var black = new SolidBrush(Color.FromArgb(10, 12, 16));

        g.FillEllipse(white, bx - 155, by - 172, 310, 292);          // 颅顶
        using (var jaw = RoundRect(bx - 92, by + 62, 184, 104, 30))  // 下颌
            g.FillPath(white, jaw);

        g.FillEllipse(black, bx - 116, by - 92, 92, 100);            // 左眼窝
        g.FillEllipse(black, bx + 24, by - 92, 92, 100);             // 右眼窝

        using (var nose = new GraphicsPath())
        {
            nose.AddPolygon(new[]
            {
                new PointF(bx - 25, by + 4), new PointF(bx + 25, by + 4), new PointF(bx, by + 58),
            });
            g.FillPath(black, nose);
        }

        for (int i = 0; i < 3; i++)                                   // 牙缝
            g.FillRectangle(black, bx - 62 + i * 48, by + 92, 16, 74);
    }

    private static void DrawApprox(Graphics g, float cx, float cy)
    {
        const float halfW = 130f;
        using var wave = new GraphicsPath();
        foreach (float oy in new[] { -84f, 84f })
        {
            var pts = new PointF[101];
            for (int i = 0; i <= 100; i++)
            {
                float t = -1f + i * 0.02f;
                pts[i] = new PointF(cx + t * halfW, cy + oy + MathF.Sin(t * MathF.PI) * 28f);
            }
            // 两条浪各自开一段：不加这句 AddCurve 会把第一段的终点和第二段的起点连起来，
            // 结果画出来是一道斜杠，不是约等号。
            wave.StartFigure();
            wave.AddCurve(pts, 0.4f);
        }

        for (int i = 5; i >= 1; i--)                                   // 辉光
        {
            int a = 5 + i;
            using var glow = new Pen(Color.FromArgb(a, Cyan), 34 + i * 6)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(glow, wave);
        }

        using var grad = new LinearGradientBrush(
            new Rectangle((int)(cx - halfW), 0, (int)(halfW * 2), 1080), Cyan, Color.FromArgb(245, 250, 255), 90f);
        using var pen = new Pen(grad, 52f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, wave);
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
