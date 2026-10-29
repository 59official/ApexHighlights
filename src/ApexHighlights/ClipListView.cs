using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 回顾库里的那条清单。
///
/// 单独做成一个控件，是因为它自己就是一整套关注点：行怎么画（圆角条、缩略图、
/// 种类配色）、缩略图什么时候在哪个线程取、列宽怎么跟着窗口走、Delete/Enter 归谁管。
/// 这些都跟「窗口里还有几个按钮、状态栏写什么」无关。
///
/// 窗口那一侧只要做三件事：SetClips 给数据，听 SelectionChanged / ClipActivated /
/// DeleteRequested，读 SelectedClip。
/// </summary>
internal sealed class ClipListView : ListView
{
    private const int ThumbW = 76;
    private const int ThumbH = 43;
    private const int TextInset = ThumbW + 18;   // 第一列文字要给缩略图让位

    // ---- 批量选择 ----
    // 勾选列不真加一列：真加一列会把 Cols[] 的下标全推一位，
    // DrawSubItem 里的 e.ColumnIndex 就再对不上 Cols（之前踩过整排串色的坑）。
    // 做法是「批量态下把第一列左边让出一段」，勾选框画在这段里。
    private int CheckPadL => UiKit.Px(this, 14);                    // 勾选框左缩进
    private int CheckSize => UiKit.Px(this, 18);                    // 勾选框边长（圆角 5，够圆润）
    private int CheckSpan => CheckPadL + CheckSize + UiKit.Px(this, 12); // 整段让位宽度
    private int CheckRadius => UiKit.Px(this, 5);

    private readonly ImageList _thumbs = new()
    {
        ImageSize = new Size(ThumbW, ThumbH),
        ColorDepth = ColorDepth.Depth32Bit,
    };

    private CancellationTokenSource? _loadCts;
    private int _hoverRow = -1;

    private readonly HoverTip _tip = new();
    private System.Windows.Forms.Timer? _tipTimer;
    private int _tipCol = -1;

    // 勾选态按路径存，不按行下标：重建行之后下标会变，路径不会。
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectMode;

    // 表头是 ListView 的一个子窗口（SysHeader32），点它的消息**不会**冒到 ListView 的
    // OnMouseDown —— 所以「全选」那一格必须单独挂个 NativeWindow 才点得着。
    private HeaderHook? _headerHook;
    private const int LVM_GETHEADER = 0x1000 + 31;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 一列的全部定义：表头文字、宽度、对齐、格子取什么文本、什么颜色、悬停说明什么。
    /// </summary>
    private sealed record Col(
        string Label,
        int Width,
        HorizontalAlignment Align,
        Func<ClipInfo, Mp4Probe.Info, string> Text,
        Func<ClipInfo, Color>? Ink = null,
        string? Tip = null);

    /// <summary>
    /// 列的单一事实来源。
    /// 以前这些散在「构造函数里加列」和「DrawSubItem 里的 switch (ColumnIndex)」两处，
    /// 加一列要改三个地方、index 错一位就整排串色；现在只改这张表。
    /// </summary>
    private static readonly Col[] Cols =
    {
        new("时间", 236, HorizontalAlignment.Left,
            (c, m) => c.CreatedLocal.ToString("MM-dd HH:mm:ss"),
            Tip: "片段保存的时间（录制结束时刻）"),
        new("种类", 88, HorizontalAlignment.Left,
            (c, m) => ClipKinds.Label(c.Kind),
            c => KindInk(c.Kind),
            Tip: "识别到的横幅类型"),
        new("分数", 58, HorizontalAlignment.Right,
            (c, m) => c.Score.ToString("F2", CultureInfo.InvariantCulture),
            _ => UiKit.Accent,
            Tip: "横幅与模板的匹配分：越高越像。低于灵敏度阈值不会入库"),
        new("时长", 64, HorizontalAlignment.Right,
            (c, m) => m.Valid ? Mp4Probe.Fmt(m.Seconds) : "—",
            Tip: "片段时长（前摇 + 中段 + 后摇）"),
        new("分辨率", 72, HorizontalAlignment.Left,
            (c, m) => m.Valid ? $"{m.Height}p" : "—",
            Tip: "文件本身的分辨率：录制时游戏画面多大就是多大，只读，不能改"),
        new("音轨", 58, HorizontalAlignment.Left,
            (c, m) => m.HasAudio ? "有" : "无",
            Tip: "片段里有没有音轨；在托盘菜单里关掉录音就是「无」"),
        new("体积", 66, HorizontalAlignment.Right,
            (c, m) => QuotaReport.Mb(c.Bytes),
            Tip: "文件大小；配额和保留天数在状态栏和托盘菜单里调"),
        new("文件名", 200, HorizontalAlignment.Left,
            (c, m) => c.Name),
    };

    /// <summary>双击或 Enter：用户想打开这一片。</summary>
    public event Action? ClipActivated;
    /// <summary>Delete：用户要删（确认框由窗口弹，控件不做不可撤销的事）。</summary>
    public event Action? DeleteRequested;
    /// <summary>选中行变了，包括变成空。</summary>
    public event Action? SelectionChanged;

    public ClipListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        View = View.Details;
        FullRowSelect = true;
        MultiSelect = true;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        BackColor = UiKit.Sunken;
        ForeColor = UiKit.Text;
        Font = UiKit.F95;
        SmallImageList = _thumbs;
        OwnerDraw = true;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;

        foreach (var col in Cols) Columns.Add(col.Label, UiKit.Px(this, col.Width), col.Align);

        // 文件名列吃掉剩余宽度：别的列固定，窗口拉宽就多显示文件名。
        Resize += (_, _) => FitNameColumn();

        DrawColumnHeader += OnDrawColumnHeader;
        DrawItem += OnDrawItem;
        DrawSubItem += OnDrawSubItem;

        DoubleClick += (_, _) => { if (SelectedClip is not null) ClipActivated?.Invoke(); };
        SelectedIndexChanged += (_, _) => SelectionChanged?.Invoke();
        MouseMove += (_, e) =>
        {
            HoverTo(GetItemAt(e.X, e.Y)?.Index ?? -1);
            TipAt(e.Location);
            Cursor = _selectMode && IsCheckHit(e.Location) ? Cursors.Hand : Cursors.Default;
        };
        MouseLeave += (_, _) => { HoverTo(-1); HideTip(); Cursor = Cursors.Default; };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteRequested?.Invoke(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { if (SelectedClip is not null) ClipActivated?.Invoke(); e.Handled = true; }
        };
    }

    /// <summary>当前选中的第一条（库窗口只关心这一条）。</summary>
    public ClipInfo? SelectedClip => SelectedItems.Count > 0 ? SelectedItems[0].Tag as ClipInfo : null;

    /// <summary>列表里现在有几行（等于最近一次 SetClips 给的条数）。</summary>
    public int RowCount => Items.Count;

    // ------------------------------------------------------------------ 批量选择

    /// <summary>批量态：第一列左边冒出勾选框，表头左边冒出「全选」。</summary>
    public bool SelectMode => _selectMode;

    /// <summary>勾了几片。</summary>
    public int CheckedCount => _checked.Count;

    /// <summary>勾选集合变了（窗口据此改按钮文字）。</summary>
    public event Action? CheckedChanged;

    /// <summary>勾选的那些片段，按列表顺序返回（删除时直接用）。</summary>
    public IReadOnlyList<ClipInfo> CheckedClips
    {
        get
        {
            var list = new List<ClipInfo>();
            foreach (ListViewItem it in Items)
                if (it.Tag is ClipInfo c && _checked.Contains(c.Path)) list.Add(c);
            return list;
        }
    }

    /// <summary>进/出批量态。退出时清空勾选（再进来是干净的）。</summary>
    public void SetSelectMode(bool on)
    {
        if (_selectMode == on) return;
        _selectMode = on;
        if (!on) _checked.Clear();
        // 第一列得变宽：勾选那段是挤在它里面的，不腾地方时间会被截成「10-28 2…」。
        // 只让 40 而不是整个 CheckSpan：多让出来的都是文件名列的钱，批量态本来也不用看清名字。
        Columns[0].Width = UiKit.Px(this, Cols[0].Width) + (on ? UiKit.Px(this, 40) : 0);
        FitNameColumn();
        Invalidate(true);   // 连表头一起刷（「全选」那格是自己画的）
        CheckedChanged?.Invoke();
    }

    /// <summary>勾/取消某一行（按行下标，越界或已删就忽略）。</summary>
    public void ToggleChecked(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        if (Items[index].Tag is not ClipInfo c) return;
        if (!_checked.Remove(c.Path)) _checked.Add(c.Path);
        Invalidate();
        CheckedChanged?.Invoke();
    }

    /// <summary>全选：全勾上了就全取消，否则全勾上（跟表头那一格同一个行为）。</summary>
    public void ToggleAll()
    {
        if (Items.Count == 0) return;
        bool all = _checked.Count >= Items.Count;
        _checked.Clear();
        if (!all)
            foreach (ListViewItem it in Items)
                if (it.Tag is ClipInfo c) _checked.Add(c.Path);
        Invalidate(true);   // 表头的「全选」框也要跟着变
        CheckedChanged?.Invoke();
    }

    /// <summary>
    /// 换一批数据。会重建行、重取缩略图、重算列宽，并默认选中第一行
    /// （选中会触发 SelectionChanged，窗口据此刷新右侧详情）。
    /// </summary>
    public void SetClips(IReadOnlyList<ClipInfo> clips)
    {
        // 行要重建，旧的取图线程必须先停：它的 ListViewItem 马上就不在列表里了。
        _loadCts?.Cancel();

        BeginUpdate();
        try
        {
            Items.Clear();
            _checked.Clear();    // 行都换了，旧的勾选没有意义
            _thumbs.Images.Clear();
            foreach (var c in clips)
            {
                var meta = Mp4Probe.Read(c.Path);   // 只读 box 头，微秒级
                var it = new ListViewItem(Cols[0].Text(c, meta)) { Tag = c };
                for (int i = 1; i < Cols.Length; i++) it.SubItems.Add(Cols[i].Text(c, meta));
                it.ImageIndex = -1;
                Items.Add(it);
            }
        }
        finally { EndUpdate(); }

        FitNameColumn();   // 竖向滚动条是加完行才出现的，它一出现客户宽度就变窄
        LoadThumbs();
        if (Items.Count > 0) Items[0].Selected = true;
    }

    /// <summary>表头悬停：只对「有话说」的列弹提示，延迟 400ms，避免扫过去就一片弹窗。</summary>
    private void TipAt(Point p)
    {
        int headerH = Items.Count > 0 ? GetItemRect(0).Top : 0;
        if (headerH <= 0 || p.Y >= headerH)
        {
            HideTip();
            return;
        }

        int x = Items.Count > 0 ? GetItemRect(0).Left : 0;
        for (int i = 0; i < Columns.Count; i++)
        {
            int w = Columns[i].Width;
            if (p.X >= x && p.X < x + w)
            {
                var tip = i < Cols.Length ? Cols[i].Tip : null;
                if (tip is null) { HideTip(); return; }
                if (_tipCol == i) return;

                HideTip();
                _tipCol = i;
                var anchor = RectangleToScreen(new Rectangle(x, 0, w, headerH));
                _tipTimer = new System.Windows.Forms.Timer { Interval = 400 };
                _tipTimer.Tick += (_, _) =>
                {
                    _tipTimer?.Stop();
                    _tipTimer?.Dispose();
                    _tipTimer = null;
                    _tip.ShowAt(anchor, tip);
                };
                _tipTimer.Start();
                return;
            }
            x += w;
        }

        HideTip();
    }

    private void HideTip()
    {
        _tipCol = -1;
        if (_tipTimer is not null) { _tipTimer.Stop(); _tipTimer.Dispose(); _tipTimer = null; }
        _tip.HideTip();
    }

    private void HoverTo(int idx)
    {
        if (idx == _hoverRow) return;
        _hoverRow = idx;
        Invalidate();
    }

    // ------------------------------------------------------------------ 勾选命中

    /// <summary>表头高度（没有行时算 0，此时不响应点击）。</summary>
    private int HeaderHeight => Items.Count > 0 ? GetItemRect(0).Top : 0;

    /// <summary>这一下点的是不是勾选框（行里的；表头那格走 HeaderHook）。</summary>
    private bool IsCheckHit(Point p)
    {
        if (!_selectMode) return false;
        int headerH = HeaderHeight;
        if (headerH <= 0) return false;
        if (p.Y < headerH) return p.X >= 2 && p.X < CheckSpan + 44;   // 兜底：真收到表头点击时也算
        return p.X >= 2 && p.X < CheckSpan + 2 && GetItemAt(p.X, p.Y) is not null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 句柄建好才有表头；换句柄（改 Style/重建）时要重挂
        _headerHook?.ReleaseHandle();
        _headerHook = null;
        var hdr = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (hdr != IntPtr.Zero)
        {
            _headerHook = new HeaderHook(this);
            _headerHook.AssignHandle(hdr);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _headerHook?.ReleaseHandle();
        _headerHook = null;
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// 表头被点了一下（坐标是表头客户区）。只有「全选」那一格算数，
    /// 其它位置返回 false 让表头自己处理（拖动列宽之类）。
    /// </summary>
    internal bool HandleHeaderClick(int x)
    {
        if (!_selectMode) return false;
        if (x < 2 || x >= CheckSpan + 44) return false;
        ToggleAll();
        return true;
    }

    /// <summary>表头子窗口的壳：拦下「全选」那一下，其余原样放行。</summary>
    private sealed class HeaderHook : NativeWindow
    {
        private readonly ClipListView _owner;
        public HeaderHook(ClipListView owner) => _owner = owner;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
            {
                long lp = m.LParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                if (_owner.HandleHeaderClick(x)) return;   // 吞掉：别让表头顺手开始拖列宽
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>表头句柄拿到了没有（自检用：拿不到说明钩子没挂上）。</summary>
    internal bool HeaderHooked => _headerHook?.Handle is { } h && h != IntPtr.Zero;

    /// <summary>
    /// 自检用：真往表头窗口发一条 WM_LBUTTONDOWN，把 HeaderHook.WndProc 那条路走通。
    /// 直接调 HandleHeaderClick 只能验判断，验不到「钩子到底挂没挂上」——
    /// 用户报的正是「点不动」，那是挂不上，不是判断错。
    /// </summary>
    internal void TestHeaderClick(int x)
    {
        var h = _headerHook?.Handle ?? IntPtr.Zero;
        if (h == IntPtr.Zero) return;
        SendMessage(h, WM_LBUTTONDOWN, IntPtr.Zero, (IntPtr)((10 << 16) | (x & 0xFFFF)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_selectMode && e.Button == MouseButtons.Left && IsCheckHit(e.Location))
        {
            if (e.Y < HeaderHeight) ToggleAll();
            else if (GetItemAt(e.X, e.Y) is { } it) ToggleChecked(it.Index);
            // 不调 base：点勾选框只是勾选，不该顺手把「选中行」也挪过去
            // （右侧详情跟着选中行走，勾一片就得跳一次预览很烦）
            return;
        }
        base.OnMouseDown(e);
    }

    /// <summary>
    /// 画一个圆角勾选框。勾上时填金色 + 深色对勾，没勾时是一圈细描边的深底。
    /// 圆角 5 是刻意调的：再小像直角、再大像个圆点药丸。
    /// </summary>
    private void DrawCheck(Graphics g, Rectangle box, bool on)
    {
        if (on)
        {
            UiKit.Fill(g, box, CheckRadius, UiKit.Accent);
            using var pen = new Pen(Color.FromArgb(23, 24, 29), 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            float x = box.X, y = box.Y;
            g.DrawLines(pen, new[]
            {
                new PointF(x + 4.4f, y + 9.2f),
                new PointF(x + 7.5f, y + 12.4f),
                new PointF(x + 13.6f, y + 5.6f),
            });
        }
        else
        {
            UiKit.Fill(g, box, CheckRadius, Color.FromArgb(31, 33, 40));
            using var path = UiKit.RoundRect(box, CheckRadius);
            using var pen = new Pen(Color.FromArgb(96, 100, 114));
            g.DrawPath(pen, path);
        }
    }

    private void FitNameColumn()
    {
        if (Columns.Count < 8) return;
        int fixedWidth = 0;
        for (int i = 0; i < Columns.Count - 1; i++) fixedWidth += Columns[i].Width;
        // 要留出竖滚动条的宽度，否则最后一行刚好差十几像素，底下会多出一条横向滚动条
        int avail = ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth - UiKit.Px(this, 2);
        Columns[^1].Width = Math.Max(UiKit.Px(this, 120), avail);
    }

    // ------------------------------------------------------------------ 自绘

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(UiKit.Surface))
            g.FillRectangle(b, e.Bounds);
        using (var pen = new Pen(UiKit.Line))
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        // 表头用跟子项完全一样的内缩，左右对齐才咬得死。
        // 第一列还要多让出缩略图那段（表头的原点比行早 13px，用行的原点对齐）。
        // 批量态再多让出勾选那段，勾选框就落在行里勾选框的正上方。
        int shift = _selectMode ? CheckSpan : 0;
        int padL = e.ColumnIndex == 0 ? TextInset + 6 + shift : 6;
        int left = e.ColumnIndex == 0 && Items.Count > 0 ? GetItemRect(0).Left : e.Bounds.X;

        if (_selectMode && e.ColumnIndex == 0)
        {
            var box = new Rectangle(left + CheckPadL, e.Bounds.Y + (e.Bounds.Height - CheckSize) / 2,
                                    CheckSize, CheckSize);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawCheck(g, box, Items.Count > 0 && _checked.Count >= Items.Count);
            var all = new Rectangle(box.Right + 8, e.Bounds.Y, 60, e.Bounds.Height);
            TextRenderer.DrawText(g, "全选", UiKit.F9, all, UiKit.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
        }

        var r = new Rectangle(left + padL, e.Bounds.Y,
                              Math.Max(0, e.Bounds.Width - padL - 6), e.Bounds.Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis | AlignOf(e.ColumnIndex);
        TextRenderer.DrawText(g, e.Header?.Text ?? "", UiKit.F9, r, UiKit.Dim, flags);
    }

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var row = e.Bounds;
        var pill = new Rectangle(4, row.Y + 3, Math.Max(0, ClientSize.Width - 8), row.Height - 6);

        if (e.Item!.Selected)
        {
            UiKit.Fill(g, pill, 10, UiKit.SelectTint);
            UiKit.Fill(g, new Rectangle(pill.X, pill.Y + 9, 3, Math.Max(0, pill.Height - 18)), 2, UiKit.Accent);
        }
        else if (e.Item.Index == _hoverRow)
        {
            UiKit.Fill(g, pill, 10, Color.FromArgb(24, 25, 31));
        }

        if (_selectMode)
        {
            var box = new Rectangle(row.X + CheckPadL, row.Y + Math.Max(0, (row.Height - CheckSize) / 2),
                                    CheckSize, CheckSize);
            DrawCheck(g, box, e.Item.Tag is ClipInfo c0 && _checked.Contains(c0.Path));
        }

        int imgIdx = e.Item.ImageIndex;
        if (imgIdx >= 0 && imgIdx < _thumbs.Images.Count)
        {
            var img = _thumbs.Images[imgIdx];
            int shift = _selectMode ? CheckSpan : 0;
            var dst = new Rectangle(row.X + 9 + shift, row.Y + Math.Max(0, (row.Height - ThumbH) / 2), ThumbW, ThumbH);
            var st = g.Save();
            using (var path = UiKit.RoundRect(dst, 8)) g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, dst);
            g.Restore(st);
            using var outline = UiKit.RoundRect(dst, 8);
            using var pen = new Pen(Color.FromArgb(60, 0, 0, 0));
            g.DrawPath(pen, outline);
        }
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.Item!.Tag as ClipInfo;
        var cell = e.Bounds;
        if (e.ColumnIndex == 0)
        {
            int shift = _selectMode ? CheckSpan : 0;
            cell = new Rectangle(cell.X + TextInset + shift, cell.Y,
                                 Math.Max(0, cell.Width - TextInset - shift), cell.Height);
        }
        cell = new Rectangle(cell.X + 6, cell.Y, Math.Max(0, cell.Width - 12), cell.Height);

        var ink = clip is not null && e.ColumnIndex >= 0 && e.ColumnIndex < Cols.Length
                  && Cols[e.ColumnIndex].Ink is { } inkOf
            ? inkOf(clip)
            : UiKit.Text;
        if (!e.Item.Selected && ink == UiKit.Text) ink = Color.FromArgb(224, 226, 232);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis | AlignOf(e.ColumnIndex);
        TextRenderer.DrawText(g, e.SubItem?.Text ?? "", UiKit.F95, cell, ink, flags);
    }

    private TextFormatFlags AlignOf(int column)
    {
        if (column < 0 || column >= Columns.Count) return TextFormatFlags.Left;
        return Columns[column].TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
    }

    private static Color KindInk(ClipKind k) => k switch
    {
        ClipKind.Kill => UiKit.Accent,
        ClipKind.Knocked => Color.FromArgb(226, 122, 122),
        ClipKind.Elim => Color.FromArgb(150, 214, 160),
        ClipKind.AssistElim => Color.FromArgb(150, 214, 160),
        _ => Color.FromArgb(214, 218, 228),
    };

    // ------------------------------------------------------------------ 缩略图

    /// <summary>
    /// 后台把缩略图一张张塞进 ImageList，不阻塞 UI 也不阻塞录制。
    ///
    /// 两个坑都踩过，写在这里免得再踩：
    /// 1) 必须 STA 线程 —— Shell 的缩略图工厂走 COM 单元模型，线程池（MTA）上取不到；
    /// 2) 路径必须绝对 —— SHCreateItemFromParsingName 不认相对路径，会安静地返回失败，
    ///    表现就是整屏占位方块（所以 ShellThumb.Get 里也兜底转了绝对路径）。
    /// </summary>
    private void LoadThumbs()
    {
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        // 占位图放 0 号：所有行先指向它，取到真图再追加并改 ImageIndex。
        // ImageList 的索引就是行绑定，不能重排，只能追加。
        var placeholder = new Bitmap(ThumbW, ThumbH);
        using (var g = Graphics.FromImage(placeholder))
        {
            g.Clear(Color.FromArgb(34, 36, 44));
            using var pen = new Pen(UiKit.Line);
            g.DrawLine(pen, 0, ThumbH - 1, ThumbW, ThumbH - 1);
        }
        _thumbs.Images.Add(placeholder);

        var items = Items.Cast<ListViewItem>().ToList();
        foreach (var it in items)
        {
            it.ImageKey = "";
            it.ImageIndex = 0;
        }
        Invalidate();

        var thread = new Thread(() =>
        {
            foreach (var it in items)
            {
                if (token.IsCancellationRequested || IsDisposed) return;
                var clip = (ClipInfo)it.Tag!;
                Bitmap? bmp = null;
                try { bmp = ShellThumb.Get(clip.Path, ThumbW * 2, ThumbH * 2); }
                catch { }
                if (bmp is null) continue;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || token.IsCancellationRequested) { bmp.Dispose(); return; }
                        _thumbs.Images.Add(bmp);
                        it.ImageIndex = _thumbs.Images.Count - 1;
                        Invalidate();
                    }));
                }
                catch (InvalidOperationException) { bmp.Dispose(); return; }   // 窗口没了
            }
        })
        {
            IsBackground = true,
            Name = "clip-thumbs",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _thumbs.Dispose();
            HideTip();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
