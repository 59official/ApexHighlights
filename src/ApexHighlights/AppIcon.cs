using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 现场画出来的应用图标（不塞资源文件，挪目录/单 exe 都不会丢）。
/// 深色圆角方块 + 青色准星，跟「击倒录制」这个语义对得上。
/// </summary>
internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon? _shared;

    public static Icon Shared => _shared ??= Build(32);

    private static Icon Build(int size)
    {
        using var bmp = Render(size);
        var h = bmp.GetHicon();
        try
        {
            // 复制一份带自己句柄的 Icon，这样原句柄可以立刻销毁
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size;
        var pad = s * 0.06f;
        var rect = new RectangleF(pad, pad, s - pad * 2, s - pad * 2);
        float r = s * 0.24f;

        using (var path = Round(rect, r))
        using (var bg = new LinearGradientBrush(rect, Color.FromArgb(34, 38, 46), Color.FromArgb(18, 20, 26), 60f))
            g.FillPath(bg, path);

        // 准星
        using var pen = new Pen(Color.FromArgb(80, 220, 240), Math.Max(1.6f, s * 0.075f));
        float cx = s * 0.5f, cy = s * 0.5f, rad = s * 0.24f;
        g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
        using var pen2 = new Pen(pen.Color, pen.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen2, cx, cy - rad - s * 0.07f, cx, cy - rad + s * 0.05f);
        g.DrawLine(pen2, cx, cy + rad - s * 0.05f, cx, cy + rad + s * 0.07f);
        g.DrawLine(pen2, cx - rad - s * 0.07f, cy, cx - rad + s * 0.05f, cy);
        g.DrawLine(pen2, cx + rad - s * 0.05f, cy, cx + rad + s * 0.07f, cy);
        using var dot = new SolidBrush(pen.Color);
        g.FillEllipse(dot, cx - s * 0.045f, cy - s * 0.045f, s * 0.09f, s * 0.09f);

        return bmp;
    }

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
