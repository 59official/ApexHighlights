using System.Drawing.Imaging;

namespace ApexHighlights;

/// <summary>
/// 把窗口真容截成 PNG（<c>--lib-ui --shot=路径</c>）。
///
/// 用屏幕抓取而不是 Control.DrawToBitmap：DrawToBitmap 走 WM_PRINT，ListView、
/// 原生滚动条这些控件会画出空白或错位的边框，看到的不是用户看到的东西。
/// 这个模式只为「改完 UI 自己看一眼」存在，不算产品功能。
/// </summary>
internal static class UiShot
{
    public static void Capture(Form f, string path)
    {
        try
        {
            bool topMost = f.TopMost;
            f.TopMost = true;
            f.Activate();
            f.Refresh();
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();

            var b = f.Bounds;
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height)))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                bmp.Save(full, ImageFormat.Png);
            }
            Console.WriteLine($"[shot] {full} {b.Width}x{b.Height}");
            f.TopMost = topMost;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[shot] 失败 " + ex.Message);
        }
    }
}
