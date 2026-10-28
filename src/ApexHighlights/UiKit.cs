using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 回顾库的视觉基件：一套深色 + 圆角的自绘控件。
///
/// 为什么自己画：WinForms 原生控件全是直角、浅灰、系统配色，和 2560x1440 的
/// 游戏片段摆在一起很割裂。圆角只能靠 GraphicsPath + 抗锯齿自绘，没有开关。
///
/// 这里的每个控件都只做一件事（圆角容器 / 圆角按钮 / 圆角图片 / 键值行），
/// 组合起来才是窗口的样子；窗口本身不画像素，只摆位置。
/// </summary>
internal static class UiKit
{
    // ---- 色板（深灰蓝底 + Apex 金强调）----
    public static readonly Color Bg = Color.FromArgb(20, 21, 25);         // 窗口底
    public static readonly Color Surface = Color.FromArgb(29, 30, 36);    // 卡片
    public static readonly Color SurfaceHi = Color.FromArgb(41, 43, 51);  // 悬停
    public static readonly Color Sunken = Color.FromArgb(16, 17, 20);     // 输入框/列表底
    public static readonly Color Line = Color.FromArgb(52, 55, 64);       // 1px 描边
    public static readonly Color Text = Color.FromArgb(236, 237, 241);
    public static readonly Color Dim = Color.FromArgb(147, 151, 163);
    public static readonly Color Accent = Color.FromArgb(247, 197, 68);   // Apex 金
    public static readonly Color AccentInk = Color.FromArgb(26, 21, 6);
    public static readonly Color Danger = Color.FromArgb(232, 98, 98);
    public static readonly Color SelectTint = Color.FromArgb(43, 48, 62);

    // 字体在绘制循环里用，必须缓存：每次 New 一个 Font 会在每个 WM_PAINT 里漏一个 GDI 对象。
    public static readonly Font F9 = new("Microsoft YaHei UI", 9f);
    public static readonly Font F95 = new("Microsoft YaHei UI", 9.5f);
    public static readonly Font F10 = new("Microsoft YaHei UI", 10f);
    public static readonly Font F11B = new("Microsoft YaHei UI", 11f, FontStyle.Bold);
    public static readonly Font F135B = new("Microsoft YaHei UI", 13.5f, FontStyle.Bold);

    public static Font Ui(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style, GraphicsUnit.Point);

    /// <summary>圆角矩形路径；半径按短边收敛，尺寸很小时不会画成畸形。</summary>
    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)) * 2;
        if (d <= 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, Rectangle r, int radius, Color fill)
    {
        using var path = RoundRect(r, radius);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
    }

    public static void FillAndStroke(Graphics g, Rectangle r, int radius, Color fill, Color stroke)
    {
        using var path = RoundRect(r, radius);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
        using var pen = new Pen(stroke);
        g.DrawPath(pen, path);
    }

    /// <summary>把下拉框刷成深色。系统下拉列表底色改不动，但每项自绘后看不出白底。</summary>
    public static void ThemeCombo(ComboBox c, int itemHeight = 26)
    {
        c.DrawMode = DrawMode.OwnerDrawFixed;
        c.DropDownStyle = ComboBoxStyle.DropDownList;
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Surface;
        c.ForeColor = Text;
        c.ItemHeight = itemHeight;
        c.Font = F9;
        c.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            var r = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? SelectTint : Surface))
                g.FillRectangle(b, r);
            TextRenderer.DrawText(g, c.Items[e.Index]?.ToString() ?? "", c.Font, r,
                sel ? Accent : Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
        };
    }

    // ---- Win11 窗口外观：圆角 + 深色标题栏。老系统上调用失败就安静忽略。----
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? subIdList);

    public static void ApplyWindowChrome(Form f)
    {
        try
        {
            int on = 1, round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(f.Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int));
            DwmSetWindowAttribute(f.Handle, DwmWindowCornerPreference, ref round, sizeof(int));
        }
        catch { }
    }

    /// <summary>让原生滚动条也走深色主题（不支持的 Windows 版本会返回非 0，忽略即可）。</summary>
    public static void DarkScrollbars(Control c)
    {
        try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>输入框占位提示（EM_SETCUEBANNER），WinForms 没有公开 API。</summary>
    public static void SetCue(TextBox box, string cue)
    {
        try { SendMessage(box.Handle, 0x1501, (IntPtr)1, cue); } catch { }
    }
}

/// <summary>圆角按钮：Ghost 普通 / Primary 金色实心 / Danger 危险。</summary>
internal sealed class RoundButton : Button
{
    public enum Look { Ghost, Primary, Danger }

    public Look Style { get; set; } = Look.Ghost;
    public int Radius { get; set; } = 10;

    private bool _hover, _down;

    public RoundButton(string text, int width = 96)
    {
        Text = text;
        Size = new Size(width, 38);
        Font = UiKit.F95;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = UiKit.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? UiKit.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill, ink;
        switch (Style)
        {
            case Look.Primary:
                fill = _down ? UiKit.Accent : _hover ? ControlPaint.Light(UiKit.Accent, 0.12f) : UiKit.Accent;
                ink = UiKit.AccentInk;
                break;
            case Look.Danger:
                fill = _down ? Color.FromArgb(70, 34, 38) : _hover ? Color.FromArgb(58, 32, 36) : UiKit.Surface;
                ink = _hover || _down ? UiKit.Danger : UiKit.Text;
                break;
            default:
                fill = _down ? UiKit.Sunken : _hover ? UiKit.SurfaceHi : UiKit.Surface;
                ink = UiKit.Text;
                break;
        }

        using (var path = UiKit.RoundRect(r, Radius))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        if (Style != Look.Primary)
        {
            using var path = UiKit.RoundRect(r, Radius);
            using var pen = new Pen(_hover ? UiKit.Line : Color.FromArgb(45, 47, 56));
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>圆角卡片容器。圆角外的像素画父容器的底色，靠抗锯齿拿到平滑边缘。</summary>
internal class Card : Panel
{
    public int Radius { get; set; } = 16;

    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiKit.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
}

/// <summary>列表/预览用的卡片：填底色 + 1px 描边。</summary>
internal sealed class SurfaceCard : Card
{
    public SurfaceCard()
    {
        Padding = new Padding(1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? UiKit.Bg);
        UiKit.FillAndStroke(g, new Rectangle(0, 0, Width - 1, Height - 1), Radius, UiKit.Surface, UiKit.Line);
        base.OnPaint(e);
    }
}

/// <summary>胶囊容器：搜索框、下拉框都套在它里面，拿高度一半当半径。</summary>
internal sealed class Pill : Panel
{
    public Color Fill { get; set; } = UiKit.Sunken;

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Fill;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? UiKit.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        UiKit.FillAndStroke(g, r, Height / 2, Fill, UiKit.Line);
        base.OnPaint(e);
    }
}

/// <summary>占位提示的输入框（EM_SETCUEBANNER 需要句柄，所以句柄建好再设）。</summary>
internal sealed class CueTextBox : TextBox
{
    private string _cue = "";
    public string Cue
    {
        get => _cue;
        set { _cue = value ?? ""; if (IsHandleCreated) UiKit.SetCue(this, _cue); }
    }

    public CueTextBox()
    {
        BorderStyle = BorderStyle.None;
        BackColor = UiKit.Sunken;
        ForeColor = UiKit.Text;
        Font = UiKit.F95;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_cue.Length > 0) UiKit.SetCue(this, _cue);
    }
}

/// <summary>
/// 缩略图预览：圆角裁切 + 等比铺满（cover）。
/// 不用 PictureBox 是因为它只能 Zoom/Stretch，Zoom 会留大黑边、Stretch 会拉歪。
/// </summary>
internal sealed class PreviewBox : Control
{
    private Image? _image;
    public int Radius { get; set; } = 12;
    public string EmptyText { get; set; } = "选一条看看";

    public Image? Image
    {
        get => _image;
        set { _image = value; Invalidate(); }
    }

    public PreviewBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiKit.Bg;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? UiKit.Surface);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width < 8 || r.Height < 8) return;

        using (var path = UiKit.RoundRect(r, Radius))
        using (var b = new SolidBrush(UiKit.Sunken))
            g.FillPath(b, path);

        if (_image is null)
        {
            TextRenderer.DrawText(g, EmptyText, UiKit.F10, r, UiKit.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        else
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var state = g.Save();
            using (var path = UiKit.RoundRect(r, Radius))
                g.SetClip(path);
            var dst = CoverRect(_image.Size, r);
            g.DrawImage(_image, dst);
            g.Restore(state);
        }

        using var border = new Pen(UiKit.Line);
        using var outline = UiKit.RoundRect(r, Radius);
        g.DrawPath(border, outline);
    }

    private static Rectangle CoverRect(Size src, Rectangle box)
    {
        if (src.Width <= 0 || src.Height <= 0) return box;
        double scale = Math.Max((double)box.Width / src.Width, (double)box.Height / src.Height);
        int w = (int)Math.Ceiling(src.Width * scale), h = (int)Math.Ceiling(src.Height * scale);
        return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }
}

/// <summary>键值行：左边暗标签、右边亮值，替代一整块多行 Label。</summary>
internal sealed class MetaList : Control
{
    private (string Key, string Value)[] _rows = Array.Empty<(string, string)>();
    public int RowHeight { get; set; } = 27;
    public int KeyWidth { get; set; } = 52;
    public Color ValueColor { get; set; } = UiKit.Text;

    public MetaList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiKit.Surface;
    }

    public void Set(params (string Key, string Value)[] rows)
    {
        _rows = rows ?? Array.Empty<(string, string)>();
        Invalidate();
    }

    public void Clear() => Set();

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? UiKit.Surface);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int y = 2;
        foreach (var (k, v) in _rows)
        {
            TextRenderer.DrawText(g, k, UiKit.F9, new Rectangle(0, y, KeyWidth, RowHeight), UiKit.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, v, UiKit.F95,
                new Rectangle(KeyWidth, y, Math.Max(0, Width - KeyWidth), RowHeight), ValueColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);
            y += RowHeight;
        }
    }
}
