using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ApexHighlights;

/// <summary>
/// 浮层提示的视觉自检：真的把那个小窗弹出来，在「滑入中 / 停留 / 滑出中」三个时刻各截一张图。
///
/// 为什么不直接看代码：这种三分之一秒的动效，光读代码看不出手感——
/// 是不是从屏幕外滑进来的、圆角有没有黑边、两行字有没有被裁，只有截图能说清。
/// </summary>
internal static class ToastTest
{
    public static int Run(string dir)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Directory.CreateDirectory(dir);

        var host = new Form
        {
            Text = "toast-test",
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            Size = new Size(120, 60),
            Opacity = 0,
        };

        var shotRect = new Rectangle(0, 0, 0, 0);
        var plan = new (int At, string Name)[]
        {
            (320, "toast_in.png"),      // 滑入刚开始：应该还在屏幕右侧外面一点
            (1500, "toast_hold.png"),   // 停留中：应该在最终位置上
            (2800, "toast_out.png"),    // 滑出中：又往右退
        };
        int step = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 40 };

        host.Shown += (_, _) =>
        {
            // 屏幕右上角那一块：浮层最终停在这里，留点余量好看出「从右边滑进来」
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            shotRect = new Rectangle(wa.Right - 420, wa.Top + 56, 400, 140);
            Toast.Show("35 秒 · 击倒", "ApexHighlights已录制");
            timer.Start();
        };

        timer.Tick += (_, _) =>
        {
            if (step >= plan.Length)
            {
                timer.Stop();
                host.Close();
                return;
            }
            var (at, name) = plan[step];
            if (clock.ElapsedMilliseconds < at) return;
            step++;
            try
            {
                using var bmp = new Bitmap(shotRect.Width, shotRect.Height);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(shotRect.Left, shotRect.Top, 0, 0, bmp.Size);
                bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
                Console.WriteLine($"[toast] {name} @{clock.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { Console.WriteLine($"[toast] 截图失败: {ex.Message}"); }
        };

        Application.Run(host);

        Console.WriteLine($"[toast] 三张图在 {Path.GetFullPath(dir)}");
        return 0;
    }
}
