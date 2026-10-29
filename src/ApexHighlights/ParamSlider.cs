using System.Drawing.Drawing2D;

namespace ApexHighlights;

/// <summary>
/// 一条自绘滑杆：左上角标签、右侧数值、下面一条细轨 + 圆角滑块。
///
/// 为什么不用 WinForms 的 TrackBar：它是系统灰的直角控件，塞进这套深色界面里像贴了块补丁。
/// 这个控件只有一件事要做——把「4–60 的码率」「0–30 秒的前后摇」画成一条能拖的线。
/// </summary>
internal sealed class ParamSlider : Control
{
    private const int TrackH = 4;
    private const int ThumbD = 14;
    private const int PadX = 2;

    private bool _drag;

    public string Label = "";
    public int Min = 0;
    public int Max = 100;
    public int Step = 1;

    /// <summary>取值范围里的当前位置。设值会被夹到 [Min, Max] 并对齐到 Step。</summary>
    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Min, Max);
            if (Step > 1) v = (int)(Math.Round((v - Min) / (double)Step) * Step) + Min;
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }
    private int _value;

    /// <summary>右侧数值怎么写。默认就是数字。</summary>
    public Func<int, string>? Format;

    /// <summary>
    /// 拖动中就已经变了（还没松手）。调用方只该拿它去改采集器——
    /// 「拖一下就看到效果」，不能挂写盘，否则一次拖动能刷几百次配置文件。
    /// </summary>
    public event Action? Dragged;

    /// <summary>拖完/键盘调完才触发一次 —— 写盘挂在这里。</summary>
    public event Action? Committed;

    public ParamSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 58;
        BackColor = UiKit.Bg;
        ForeColor = UiKit.Text;
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    private string ValueText => Format?.Invoke(_value) ?? _value.ToString();

    /// <summary>数值文本基线：固定贴右边，长文本也不会把轨道挤歪。</summary>
    private int TrackTop => Height - 20;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var labelRect = new Rectangle(PadX, 2, Width - PadX * 2, 20);
        TextRenderer.DrawText(g, Label, UiKit.F95, labelRect, UiKit.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        var valRect = new Rectangle(PadX, 2, Width - PadX * 2, 20);
        TextRenderer.DrawText(g, ValueText, UiKit.F10, valRect, Focused ? UiKit.Accent : UiKit.Dim,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix);

        // 轨道：左半截填强调色，右半截留灰
        int x0 = PadX + ThumbD / 2;
        int x1 = Width - PadX - ThumbD / 2;
        int y = TrackTop + TrackH / 2;
        var full = new Rectangle(x0, TrackTop, Math.Max(1, x1 - x0), TrackH);
        UiKit.Fill(g, full, TrackH / 2, Color.FromArgb(58, 61, 70));

        int tx = ValueToX(_value);
        if (tx > x0)
            UiKit.Fill(g, new Rectangle(x0, TrackTop, tx - x0, TrackH), TrackH / 2, UiKit.Accent);

        var thumb = new Rectangle(tx - ThumbD / 2, y - ThumbD / 2, ThumbD, ThumbD);
        using (var b = new SolidBrush(UiKit.Accent)) g.FillEllipse(b, thumb);
        using (var p = new Pen(Color.FromArgb(20, 21, 25), 2)) g.DrawEllipse(p, thumb);
    }

    private int ValueToX(int v)
    {
        int x0 = PadX + ThumbD / 2;
        int x1 = Width - PadX - ThumbD / 2;
        if (Max <= Min) return x0;
        double t = (v - Min) / (double)(Max - Min);
        return x0 + (int)Math.Round(t * (x1 - x0));
    }

    private int XToValue(int x)
    {
        int x0 = PadX + ThumbD / 2;
        int x1 = Width - PadX - ThumbD / 2;
        if (x1 <= x0) return Min;
        double t = Math.Clamp((x - x0) / (double)(x1 - x0), 0, 1);
        return Min + (int)Math.Round(t * (Max - Min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _drag = true;
        Capture = true;
        Value = XToValue(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        int before = _value;
        Value = XToValue(e.X);
        if (_value != before) Dragged?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drag) return;
        _drag = false;
        Capture = false;
        Committed?.Invoke();
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int before = _value;
        switch (e.KeyCode)
        {
            case Keys.Left: Value = _value - Math.Max(1, Step); break;
            case Keys.Right: Value = _value + Math.Max(1, Step); break;
            case Keys.Home: Value = Min; break;
            case Keys.End: Value = Max; break;
            default: return;
        }
        e.Handled = true;
        if (_value != before) Committed?.Invoke();
    }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
}
