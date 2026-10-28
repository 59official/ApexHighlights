using System.Drawing.Drawing2D;

namespace ApexHighlights;

/// <summary>
/// 胶囊下拉：自己和选项面板都自绘，不用系统 ComboBox。
///
/// 为什么扔掉 ComboBox：它的右侧按钮是系统画的浅色方块，不吃 BackColor，
/// 深色界面上像贴了张白纸；弹出列表的边框和底色同样改不动。
/// 试过「整块自绘盖住原生按钮」，能盖住九成，但右边总会漏出一条白边，
/// 而且控件高度不受控（≈ ItemHeight+6），跟旁边 38px 的按钮对不齐。
/// 这里干脆自己实现：38px 高、圆角 = 高/2、悬停变亮、点开是深色菜单。
///
/// 对外接口刻意跟 ComboBox 保持一致的子集（Items / SelectedIndex / SelectedIndexChanged），
/// 这样调用方和自检脚本都只改类型名。
/// </summary>
internal sealed class PillSelect : Control
{
    private int _selected = -1;
    private bool _hover;
    private bool _open;

    public PillSelect()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiKit.Bg;
        ForeColor = UiKit.Text;
        Font = UiKit.F95;
        Cursor = Cursors.Hand;
        Height = 38;
        TabStop = true;
    }

    /// <summary>选项文字。先用 Add/AddRange 铺好再设 SelectedIndex。</summary>
    public List<object> Items { get; } = new();

    public Color Host { get; set; } = UiKit.Bg;

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem => _selected >= 0 && _selected < Items.Count ? Items[_selected] : null;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Parent?.BackColor ?? Host)) g.FillRectangle(b, ClientRectangle);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = _hover || _open ? Color.FromArgb(36, 39, 48) : UiKit.PillFill;
        var stroke = _open ? UiKit.Accent : Color.FromArgb(38, 41, 50);
        UiKit.FillAndStroke(g, r, Height / 2, fill, stroke);

        int arrow = 26, padL = 15;
        var tr = new Rectangle(r.X + padL, r.Y, Math.Max(0, r.Width - padL - arrow), r.Height);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, tr, UiKit.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
            | TextFormatFlags.EndEllipsis);

        int cx = r.Right - arrow / 2 - 4, cy = r.Height / 2;
        using var pen = new Pen(UiKit.Dim, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) ShowMenu(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { ShowMenu(); e.Handled = true; }
        else if (e.KeyCode is Keys.Down or Keys.Up && Items.Count > 0)
        {
            int d = e.KeyCode == Keys.Down ? 1 : -1;
            SelectedIndex = Math.Clamp((_selected < 0 ? d > 0 ? -1 : Items.Count : _selected) + d, 0, Items.Count - 1);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>弹出选项面板：深色、圆角行、悬停/选中用金色字。</summary>
    /// <summary>把菜单弹出来；返回菜单窗口本身（自检截图要用，正常用不到返回值）。</summary>
    internal ContextMenuStrip? ShowMenu()
    {
        if (Items.Count == 0 || _open) return null;
        _open = true;
        Invalidate();

        var menu = new ContextMenuStrip
        {
            Font = UiKit.F95,
            ShowImageMargin = false,
            BackColor = UiKit.Surface,
            ForeColor = UiKit.Text,
            Padding = new Padding(4, 4, 4, 4),
            Renderer = new DarkMenu(),
        };

        for (int i = 0; i < Items.Count; i++)
        {
            int idx = i;
            var it = new ToolStripMenuItem(Items[i]?.ToString() ?? "")
            {
                ForeColor = i == _selected ? UiKit.Accent : UiKit.Text,
                Padding = new Padding(8, 5, 8, 5),
            };
            it.Click += (_, _) => SelectedIndex = idx;
            menu.Items.Add(it);
        }

        int minW = Width;
        menu.MinimumSize = new Size(minW, 0);
        menu.Closed += (_, _) =>
        {
            _open = false;
            _closing = false;
            StopWatch();
            StopAnim();
            Invalidate();
            BeginInvoke(new Action(() => menu.Dispose()));
        };
        menu.Show(this, new Point(0, Height + 4));
        using (var rp = UiKit.RoundRect(new Rectangle(0, 0, menu.Width, menu.Height), DarkMenu.Radius))
            menu.Region = new Region(rp);

        var settled = menu.Location;
        Animate(menu, settled, -SlideDist, 0, OpenMs, null);
        WatchLeave(menu, settled);
        return menu;
    }

    private System.Windows.Forms.Timer? _anim;
    private System.Windows.Forms.Timer? _watch;
    private bool _closing;

    private const int OpenMs = 330;      // 滑出总时长：原来 84ms，用户要“慢 0.25 秒”
    private const int CloseMs = 200;     // 收起比打开快一点，像被吸回去
    private const int SlideDist = 16;    // 行程：始终从胶囊底边往上 16px 处滑进来
    private const int LeaveDelayMs = 1000;

    /// <summary>菜单落定后盯鼠标：离开胶囊和菜单满 1 秒就滑回去收掉。</summary>
    private void WatchLeave(ContextMenuStrip menu, Point settled)
    {
        StopWatch();
        _closing = false;

        int away = 0;
        var t = new System.Windows.Forms.Timer { Interval = 80 };
        t.Tick += (_, _) =>
        {
            if (menu.IsDisposed || !menu.Visible)
            {
                StopWatch();
                return;
            }

            var p = Cursor.Position;
            bool overPill = RectangleToScreen(ClientRectangle).Contains(p);

            // 鼠标滑进菜单里挑选项不算“移开”，否则菜单根本用不了
            bool overMenu = menu.Bounds.Contains(p);

            if (overPill || overMenu)
            {
                away = 0;
                if (_closing)      // 收到一半又挪回来：停手，回原位
                {
                    _closing = false;
                    Animate(menu, settled, menu.Location.Y - settled.Y, 0, 90, null);
                }
                return;
            }

            away += t.Interval;
            if (_closing || away < LeaveDelayMs) return;

            _closing = true;
            Animate(menu, settled, menu.Location.Y - settled.Y, -SlideDist, CloseMs, () =>
            {
                _closing = false;
                if (!menu.IsDisposed) menu.Close();
            });
        };
        _watch = t;
        t.Start();
    }

    /// <summary>
    /// 把菜单从 settled + fromDy 动画到 settled + toDy（ease-out cubic，逐帧改窗口位置）。
    /// 只动位置不做透明度：不引入分层窗口，省掉一类渲染风险。
    /// </summary>
    private void Animate(ContextMenuStrip menu, Point settled, int fromDy, int toDy, int ms, Action? done)
    {
        StopAnim();
        if (ms <= 0 || fromDy == toDy)
        {
            menu.Location = new Point(settled.X, settled.Y + toDy);
            done?.Invoke();
            return;
        }

        int steps = Math.Max(2, ms / 15);
        int interval = Math.Max(10, ms / steps);
        int step = 0;

        menu.Location = new Point(settled.X, settled.Y + fromDy);

        var t = new System.Windows.Forms.Timer { Interval = interval };
        t.Tick += (_, _) =>
        {
            if (menu.IsDisposed)
            {
                t.Stop();
                t.Dispose();
                return;
            }

            step++;
            double p = step / (double)steps;
            double e = 1 - Math.Pow(1 - p, 3);
            int dy = (int)Math.Round(fromDy + (toDy - fromDy) * e);
            menu.Location = new Point(settled.X, settled.Y + dy);

            if (step < steps) return;

            menu.Location = new Point(settled.X, settled.Y + toDy);
            t.Stop();
            t.Dispose();
            if (ReferenceEquals(_anim, t)) _anim = null;
            done?.Invoke();
        };
        _anim = t;
        t.Start();
    }

    private void StopAnim()
    {
        _anim?.Stop();
        _anim?.Dispose();
        _anim = null;
    }

    private void StopWatch()
    {
        _watch?.Stop();
        _watch?.Dispose();
        _watch = null;
    }
}

/// <summary>把 ContextMenuStrip 刷成跟界面一致的深色（系统默认是浅灰菜单）。</summary>
internal sealed class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkTable()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(UiKit.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    // 菜单是方角的原生弹窗，跟按钮的圆角对不上：边框画成圆角路径，
    // 再在 ShowMenu 里给窗口套一个同样的圆角 Region 把外面的直角切掉。
    internal const int Radius = 10;

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = UiKit.RoundRect(r, Radius);
        using var pen = new Pen(UiKit.Line);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height - 1);
        using (var b = new SolidBrush(UiKit.Surface)) g.FillRectangle(b, r);
        if (e.Item.Selected) UiKit.Fill(g, r, 7, UiKit.SelectTint);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? UiKit.Text : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }

    private sealed class DarkTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => UiKit.SelectTint;
        public override Color MenuItemBorder => UiKit.SelectTint;
        public override Color MenuBorder => UiKit.Line;
        public override Color ToolStripDropDownBackground => UiKit.Surface;
        public override Color ImageMarginGradientBegin => UiKit.Surface;
        public override Color ImageMarginGradientMiddle => UiKit.Surface;
        public override Color ImageMarginGradientEnd => UiKit.Surface;
        public override Color MenuItemSelectedGradientBegin => UiKit.SelectTint;
        public override Color MenuItemSelectedGradientEnd => UiKit.SelectTint;
    }
}
