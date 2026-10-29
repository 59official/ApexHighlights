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

    /// <summary>菜单正弹着（含收起动画中）。自检要断言「第二下收得掉」。</summary>
    internal bool IsOpen => _open;

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

    /// <summary>把菜单弹出来；返回菜单窗口本身（自检截图要用，正常用不到返回值）。</summary>
    internal ContextMenuStrip? ShowMenu()
    {
        if (Items.Count == 0) return null;
        // 已经弹着 = 又点了一下胶囊，那就收起来。用户要的就是「点一下开、再点一下关」。
        if (_open)
        {
            CloseAnimated();
            return null;
        }
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
            // 关掉系统自带的「点外面就立刻消失」：那样点胶囊第二下会被它先吞掉，
            // 收起动画根本没机会放。现在关的时机全由 Dismiss 过滤器 + CloseAnimated 决定。
            AutoClose = false,
        };

        for (int i = 0; i < Items.Count; i++)
        {
            int idx = i;
            var it = new ToolStripMenuItem(Items[i]?.ToString() ?? "")
            {
                ForeColor = i == _selected ? UiKit.Accent : UiKit.Text,
                Padding = new Padding(8, 5, 8, 5),
            };
            // AutoClose=false 之后「选完自己收」也得自己来，否则菜单会赖在那儿
            it.Click += (_, _) => { SelectedIndex = idx; CloseAnimated(); };
            menu.Items.Add(it);
        }

        int minW = Width;
        menu.MinimumSize = new Size(minW, 0);
        menu.Closed += (_, _) =>
        {
            _open = false;
            _closing = false;
            _menu = null;
            Dismiss(null);
            StopWatch();
            StopAnim();
            Invalidate();
            BeginInvoke(new Action(() => menu.Dispose()));
        };
        // Esc 自己处理：AutoClose=false 时窗口不一定会替我们关
        menu.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            e.Handled = e.SuppressKeyPress = true;
            CloseAnimated();
        };
        menu.Show(this, new Point(0, Height + 4));
        using (var rp = UiKit.RoundRect(new Rectangle(0, 0, menu.Width, menu.Height), DarkMenu.Radius))
            menu.Region = new Region(rp);

        var settled = menu.Location;
        _menu = menu;
        _menuSettled = settled;
        Dismiss(new DismissFilter(this));
        Animate(menu, settled, -SlideDist, 0, OpenMs, null, Ease.OutBack);
        WatchLeave(menu, settled);
        return menu;
    }

    private System.Windows.Forms.Timer? _anim;
    private System.Windows.Forms.Timer? _watch;
    private bool _closing;

    private ContextMenuStrip? _menu;
    private Point _menuSettled;
    private DismissFilter? _dismiss;

    /// <summary>收起：走 CloseMs 的滑回动画，落定后才真的 Close。</summary>
    private void CloseAnimated()
    {
        var menu = _menu;
        if (menu is null || menu.IsDisposed || _closing) return;
        _closing = true;
        StopWatch();
        Animate(menu, _menuSettled, menu.Location.Y - _menuSettled.Y, -SlideDist, CloseMs, () =>
        {
            _closing = false;
            if (!menu.IsDisposed) menu.Close();
        }, Ease.InCubic);
    }

    /// <summary>装/卸全局鼠标过滤器（只在菜单弹着的时候装）。</summary>
    private void Dismiss(DismissFilter? f)
    {
        if (_dismiss is not null) Application.RemoveMessageFilter(_dismiss);
        _dismiss = f;
        if (f is not null) Application.AddMessageFilter(f);
    }

    /// <summary>
    /// 菜单弹着时的鼠标接管：
    /// - 点在胶囊上 → 先吞掉这次点击再收起（不吞的话松手后 OnMouseDown 又把它打开，就成了点不掉）；
    /// - 点在别处 → 立刻关掉，但不吞消息，别的控件照常响应（不能像系统菜单那样要点两下）。
    /// 只认按下，不认松开：认松开会让「拖选文字」也误关。
    /// </summary>
    private sealed class DismissFilter : IMessageFilter
    {
        private readonly PillSelect _owner;
        public DismissFilter(PillSelect owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is not (0x0201 or 0x0204 or 0x0207 or 0x020B)) return false;   // L/R 按下、双击、X 键
            var menu = _owner._menu;
            if (menu is null || menu.IsDisposed || !menu.Visible) return false;

            var p = Cursor.Position;
            if (menu.Bounds.Contains(p)) return false;                      // 点菜单项：交给菜单
            if (_owner.RectangleToScreen(_owner.ClientRectangle).Contains(p))
            {
                _owner.CloseAnimated();
                return true;                                                 // 这一次点击就用来收起
            }

            _owner.StopWatch();
            _owner.StopAnim();
            _owner._closing = false;
            menu.Close();                                                    // 点别处：马上收，别挡着人家
            return false;
        }
    }

    private const int OpenMs = 450;      // 滑出总时长：原来 84ms“太生硬”，后来 330ms，用户又要“再柔一点”
    private const int CloseMs = 220;     // 收起比打开快一点，像被吸回去
    private const int SlideDist = 10;    // 行程：用户嫌原来 16px 幅度太大，收小一档
    private const int LeaveDelayMs = 1000;

    /// <summary>
    /// 缓动曲线。原来只有 ease-out cubic 一条，走到终点是「刹住」而不是「落定」，
    /// 用户说不够柔、不够弹。这里换成：
    /// - 打开用 back（冲过终点再回来），靠那一下回弹给出弹性，行程只有 10px 所以回弹约 2px，不会晃眼；
    /// - 收起用 in-cubic（越走越快），像被吸回去，而不是匀速平移。
    /// </summary>
    private enum Ease { OutCubic, OutQuint, OutBack, InCubic }

    /// <summary>
    /// 回弹强度：c 越大冲得越远。2.0 在 10px 行程上过冲约 2px（一帧两像素，看得见但不吵）。
    /// 再大就会在末尾「悬」在 1px 上不动 —— 那是整数取整的锅，不是曲线不够弹。
    /// </summary>
    private const double Backness = 1.5;

    private static double EaseAt(Ease k, double p) => k switch
    {
        Ease.OutQuint => 1 - Math.Pow(1 - p, 5),
        Ease.OutBack => 1 + (Backness + 1) * Math.Pow(p - 1, 3) + Backness * Math.Pow(p - 1, 2),
        Ease.InCubic => p * p * p,
        _ => 1 - Math.Pow(1 - p, 3),
    };

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
                    Animate(menu, settled, menu.Location.Y - settled.Y, 0, 180, null, Ease.OutCubic);
                }
                return;
            }

            away += t.Interval;
            if (_closing || away < LeaveDelayMs) return;

            // 收起只有一条路（CloseAnimated），这里不再自己拼一遍动画
            CloseAnimated();
        };
        _watch = t;
        t.Start();
    }

    /// <summary>
    /// 把菜单从 settled + fromDy 动画到 settled + toDy（曲线见 Ease），逐帧改窗口位置。
    /// 只动位置不做透明度：不引入分层窗口，省掉一类渲染风险。
    /// </summary>
    private void Animate(ContextMenuStrip menu, Point settled, int fromDy, int toDy, int ms, Action? done, Ease ease = Ease.OutCubic)
    {
        StopAnim();
        if (ms <= 0 || fromDy == toDy)
        {
            menu.Location = new Point(settled.X, settled.Y + toDy);
            done?.Invoke();
            return;
        }

        // 15ms 一步 ≈ 60fps；步数按总时长算，所以改时长不会改手感
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
            double e = EaseAt(ease, p);
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
