using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 右上角滑出提示（仿 NVIDIA 的「录制已开始」，但按用户要求小一圈：320×72、圆角 14）。
///
/// 三条硬约束，决定了它必须是一个「分层窗口」而不是普通 Form：
/// 1. 圆角要真圆角——Region 裁出来的边缘是锯齿，只有逐像素 alpha 才干净；
/// 2. 不能抢焦点——用户正在打游戏，弹个窗把键鼠叼走是灾难（WS_EX_NOACTIVATE + ShowWithoutActivation）；
/// 3. 不进任务栏、不进 Alt+Tab（WS_EX_TOOLWINDOW + ShowInTaskbar=false）。
///
/// 它不会被录进片段：采集是按游戏窗口 HWND 抓的，这个窗是另一个顶层窗，天然不在取景框里。
/// </summary>
internal sealed class ToastForm : Form
{
    private const int W = 320;
    private const int H = 72;
    private const int Radius = 14;
    private const int SlideInMs = 1000;    // 用户定的节奏：滑入 1 秒
    private const int HoldMs = 1500;       // 停留 1.5 秒
    private const int SlideOutMs = 500;    // 收起 0.5 秒
    private const int RightMargin = 20;
    private const int TopMargin = 84;

    private readonly string _main;
    private readonly string _sub;
    private readonly Action? _clicked;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    private readonly float _scale;
    private readonly int _settleX;
    private readonly int _settleY;
    private readonly Bitmap _bmp;
    private bool _drawn;

    /// <summary>滑出去的时候朝右走，和进来时同一条道，看起来像缩回屏幕边缘。</summary>
    private const int ExitDx = 90;

    public ToastForm(string main, string sub, Action? clicked)
    {
        _main = main;
        _sub = sub;
        _clicked = clicked;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        DoubleBuffered = true;

        _scale = DeviceDpi / 96f;
        int w = (int)(W * _scale);
        int h = (int)(H * _scale);
        Size = new Size(w, h);

        // 位置：与 NVIDIA 那个浮层同一个角（右上），但往里收一点、尺寸小一圈
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        _settleX = area.Right - w - (int)(RightMargin * _scale);
        _settleY = area.Top + (int)(TopMargin * _scale);
        Location = new Point(area.Right + 8, _settleY);   // 从屏幕外滑进来

        _bmp = Render(w, h);
        _tick.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PushLayered();          // 第一帧就得是分层内容，否则会先闪一个黑方块
        _tick.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _clicked?.Invoke();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Stop();
            _tick.Dispose();
            _bmp.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>三段式：滑入 → 停留 → 滑出。全都是 x 位移，淡入淡出会让字发虚，不做。</summary>
    private void Step()
    {
        long ms = _clock.ElapsedMilliseconds;
        if (ms <= SlideInMs)
        {
            double p = EaseOutCubic(ms / (double)SlideInMs);
            int from = _settleX + (int)(W * _scale);
            SetX((int)(from + (_settleX - from) * p));
        }
        else if (ms <= SlideInMs + HoldMs)
        {
            SetX(_settleX);
        }
        else if (ms <= SlideInMs + HoldMs + SlideOutMs)
        {
            double p = (ms - SlideInMs - HoldMs) / (double)SlideOutMs;
            SetX(_settleX + (int)(ExitDx * _scale * (p * p)));
        }
        else Close();
    }

    private void SetX(int x)
    {
        if (Left == x) return;
        Left = x;
        PushLayered();
    }

    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - p, 3);

    /// <summary>
    /// 把 <see cref="_bmp"/> 推给 DWM。窗口本身不画任何东西（OnPaint 永远用不到），
    /// 位置变化只要重新 UpdateLayeredWindow 一次。
    /// </summary>
    private void PushLayered()
    {
        if (!_drawn && Handle == IntPtr.Zero) return;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            hBitmap = _bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = Width, cy = Height };
            var src = new POINT { X = 0, Y = 0 };
            var dst = new POINT { X = Left, Y = Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,          // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,      // AC_SRC_ALPHA：用位图自带的 alpha
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            _drawn = true;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 一次画好整张图：圆角底 + 左侧 3px 强调竖条 + 准星图标 + 两行字。
    /// 主行是大字（35 秒 · 击倒），副行是小灰字（ApexHighlights已录制）—— 用户 2026-10-29 定的。
    /// </summary>
    private Bitmap Render(int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 分层窗口（UpdateLayeredWindow）的底是透明的，ClearType 的子像素抗锯齿在这种底上
        // 会留下彩边（字的左缘发蓝、右缘发红）。这里退回灰度抗锯齿，柔和一点但干净。
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float s = _scale;
        var body = new RectangleF(0, 0, w - 1, h - 1);
        using (var path = RoundRect(body, Radius * s))
        {
            using var fill = new LinearGradientBrush(body,
                Color.FromArgb(238, 22, 25, 32), Color.FromArgb(238, 14, 16, 21), LinearGradientMode.Vertical);
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1f, s));
            g.DrawPath(border, path);
        }

        // 左边那条竖条：学 NVIDIA 的绿条，换成我们自己的黄
        var barW = 3f * s;
        var barInset = 10f * s;
        using (var barPath = RoundRect(new RectangleF(barInset, barInset, barW, h - barInset * 2), barW / 2f))
        using (var bar = new SolidBrush(UiKit.Accent))
            g.FillPath(bar, barPath);

        // 图标：直接把运行时的准星图标缩小画上去，不额外带资源
        int icon = (int)(26 * s);
        int iconX = (int)(barInset + barW + 12 * s);
        int iconY = (h - icon) / 2;
        try
        {
            using var ico = AppIcon.Render(icon);
            g.DrawImage(ico, new Rectangle(iconX, iconY, icon, icon));
        }
        catch { /* 图标画不出来不影响提示本身 */ }

        int textX = iconX + icon + (int)(12 * s);
        int textW = w - textX - (int)(16 * s);
        using var mainFont = new Font("Microsoft YaHei UI", 10.5f * s, FontStyle.Bold, GraphicsUnit.Point);
        using var subFont = new Font("Microsoft YaHei UI", 8.5f * s, FontStyle.Regular, GraphicsUnit.Point);
        using var mainBrush = new SolidBrush(Color.FromArgb(244, 246, 250));
        using var subBrush = new SolidBrush(Color.FromArgb(150, 155, 168));
        var mainRect = new Rectangle(textX, (int)(h * 0.26), textW, (int)(h * 0.36));
        var subRect = new Rectangle(textX, (int)(h * 0.56), textW, (int)(h * 0.30));
        g.DrawString(_main, mainFont, mainBrush, mainRect, NoWrap);
        g.DrawString(_sub, subFont, subBrush, subRect, NoWrap);
        return bmp;
    }

    private static readonly StringFormat NoWrap = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Near,
    };

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}

/// <summary>
/// 提示浮层的入口。同一时刻只留一个：新的来了就把旧的直接关掉（排队反而会让提示滞后几秒，
/// 那时候片段早就存好了，用户也对不上号）。
/// </summary>
internal static class Toast
{
    private static ToastForm? _open;

    /// <summary>弹一条。必须在 UI 线程调用（托盘的所有 UI 动作都走 _marshal.BeginInvoke）。</summary>
    public static void Show(string main, string sub, Action? clicked = null)
    {
        try
        {
            if (_open is { IsDisposed: false })
            {
                _open.Close();
                _open = null;
            }
            var f = new ToastForm(main, sub, clicked);
            _open = f;
            f.FormClosed += (_, _) => { if (ReferenceEquals(_open, f)) _open = null; };
            f.Show();
        }
        catch (Exception ex)
        {
            Log.Write("[toast] 弹窗失败: " + ex.Message);
        }
    }
}
