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

    /// <summary>
    /// 把图标渲染成多尺寸 .ico 写盘（打包用：exe 图标、快捷方式、安装包）。
    ///
    /// 为什么自己拼 ICO 而不用 <c>bmp.GetHicon()</c> + <c>Icon.Save</c>：那样只会存下
    /// 一个尺寸（16 或 32），在资源管理器大图标视图和安装程序里都会糊成一团。ICO 的
    /// 容器格式本身很简单——ICONDIR + 每帧一张「BITMAPINFOHEADER + BGRA 倒序像素 +
    /// AND 掩码」的位图，直接写就行，不必去编码 PNG。
    /// </summary>
    public static void SaveIco(string path, params int[] sizes)
    {
        if (sizes is null || sizes.Length == 0) sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

        var frames = new List<(int Size, byte[] Bmp)>();
        foreach (var size in sizes)
        {
            using var bmp = Render(size);
            frames.Add((size, ToDib(bmp)));
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);                 // reserved
        w.Write((ushort)1);                 // type = icon
        w.Write((ushort)frames.Count);

        int offset = 6 + frames.Count * 16;
        foreach (var (size, dib) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 表示 256，别写 256 会被截成 0 也一样
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);               // 调色板色数
            w.Write((byte)0);               // reserved
            w.Write((ushort)1);             // planes
            w.Write((ushort)32);            // bit count
            w.Write(dib.Length);
            w.Write(offset);
            offset += dib.Length;
        }

        foreach (var (_, dib) in frames) w.Write(dib);
    }

    /// <summary>把一张 32bpp 位图摊成 ICO 里那种「头 + 倒序 BGRA + AND 掩码」的字节。</summary>
    private static byte[] ToDib(Bitmap bmp)
    {
        int size = bmp.Width;
        int maskStride = ((size + 31) / 32) * 4;      // 1bpp，每行按 4 字节对齐
        int pixelBytes = size * size * 4;
        var buf = new byte[40 + pixelBytes + maskStride * size];

        using (var ms = new MemoryStream(buf))
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40);                 // biSize
            w.Write(size);               // biWidth
            w.Write(size * 2);           // biHeight：ICO 里是 XOR + AND 两张图的高度之和
            w.Write((ushort)1);          // biPlanes
            w.Write((ushort)32);         // biBitCount
            w.Write(0);                  // biCompression = BI_RGB
            w.Write(pixelBytes);         // biSizeImage
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        }

        // 像素：DIB 是自下而上、BGRA 顺序
        int at = 40;
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
            {
                var c = bmp.GetPixel(x, y);
                buf[at++] = c.B;
                buf[at++] = c.G;
                buf[at++] = c.R;
                buf[at++] = c.A;
            }

        // AND 掩码：32bpp 下可以全 0（透明靠 alpha），但结构上必须留着
        // 已经在 buf 里预置成 0，这里只把偏移推到位即可（at 到末尾就是掩码区）。
        return buf;
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
