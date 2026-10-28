using System.Drawing.Drawing2D;

namespace ApexHighlights;

/// <summary>
/// 悬停提示。
///
/// 不用系统 ToolTip：它是浅灰直角气泡，跟着系统主题走，压在这套深色圆角界面上很突兀。
/// 这里用一个无边框、不抢焦点、不进任务栏的小窗自己画，颜色/圆角/字体都跟菜单一套。
///
/// 两种定位入口：ShowFor(控件, 文本) 挂在某个控件下方；ShowAt(屏幕矩形, 文本)
/// 用在只知道位置的地方（比如清单表头的某一列）。
/// </summary>
internal sealed class HoverTip : Form
{
    private const int PadX = 15;
    private const int PadY = 12;
    private const int MaxTextW = 430;   // 一行能放下“调高更严格……”这种整句
    private const int Land = 10;     // 圆角
    private const int Gap = 10;      // 跟锚点的距离

    private string _text = "";

    public HoverTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = UiKit.Surface;
        DoubleBuffered = true;
        Font = UiKit.F9;
    }

    /// <summary>弹出时不抢焦点：否则用户点胶囊会先被提示吃掉一次点击。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;    // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW，不占 Alt+Tab
            return cp;
        }
    }

    /// <summary>挂在控件下方（放不下就翻到上方）。</summary>
    public void ShowFor(Control anchor, string text)
    {
        if (!anchor.IsHandleCreated) return;
        ShowAt(anchor.RectangleToScreen(anchor.ClientRectangle), text);
    }

    /// <summary>锚点是屏幕坐标里的一块矩形。</summary>
    public void ShowAt(Rectangle anchor, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_text != text || !Visible)
        {
            _text = text;
            Resize();
        }

        var area = Screen.FromPoint(new Point(anchor.Left, anchor.Bottom)).WorkingArea;

        int x = anchor.Left;
        int y = anchor.Bottom + Gap;
        if (y + Height > area.Bottom) y = anchor.Top - Height - Gap;     // 下面放不下就往上翻
        x = Math.Min(Math.Max(area.Left + 4, x), area.Right - Width - 4);

        Bounds = new Rectangle(x, y, Width, Height);
        if (!Visible) Show();
        else Invalidate();
    }

    public void HideTip()
    {
        if (Visible) Hide();
    }

    private void Resize()
    {
        var sz = TextRenderer.MeasureText(_text, Font, new Size(MaxTextW, int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        ClientSize = new Size(sz.Width + PadX * 2 + 2, sz.Height + PadY * 2 + 2);
        using var path = UiKit.RoundRect(new Rectangle(0, 0, Width, Height), Land);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiKit.Surface);

        using (var path = UiKit.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Land))
        using (var pen = new Pen(UiKit.Line))
            g.DrawPath(pen, path);

        TextRenderer.DrawText(g, _text, Font,
                              new Rectangle(PadX, PadY, Width - PadX * 2, Height - PadY * 2),
                              UiKit.Text,
                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
