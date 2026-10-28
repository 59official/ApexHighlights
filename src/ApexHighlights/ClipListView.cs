using System.Drawing.Drawing2D;
using System.Globalization;

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

    private readonly ImageList _thumbs = new()
    {
        ImageSize = new Size(ThumbW, ThumbH),
        ColorDepth = ColorDepth.Depth32Bit,
    };

    private CancellationTokenSource? _loadCts;
    private int _hoverRow = -1;

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

        Columns.Add("时间", 236);
        Columns.Add("种类", 88);
        Columns.Add("分数", 58, HorizontalAlignment.Right);
        Columns.Add("时长", 64, HorizontalAlignment.Right);
        Columns.Add("画质", 72);
        Columns.Add("音轨", 58);
        Columns.Add("体积", 66, HorizontalAlignment.Right);
        Columns.Add("文件名", 200);

        // 文件名列吃掉剩余宽度：别的列固定，窗口拉宽就多显示文件名。
        Resize += (_, _) => FitNameColumn();

        DrawColumnHeader += OnDrawColumnHeader;
        DrawItem += OnDrawItem;
        DrawSubItem += OnDrawSubItem;

        DoubleClick += (_, _) => { if (SelectedClip is not null) ClipActivated?.Invoke(); };
        SelectedIndexChanged += (_, _) => SelectionChanged?.Invoke();
        MouseMove += (_, e) => HoverTo(GetItemAt(e.X, e.Y)?.Index ?? -1);
        MouseLeave += (_, _) => HoverTo(-1);
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
            _thumbs.Images.Clear();
            foreach (var c in clips)
            {
                var meta = Mp4Probe.Read(c.Path);   // 只读 box 头，微秒级
                var it = new ListViewItem(c.CreatedLocal.ToString("MM-dd HH:mm:ss")) { Tag = c };
                it.SubItems.Add(ClipKinds.Label(c.Kind));
                it.SubItems.Add(c.Score.ToString("F2", CultureInfo.InvariantCulture));
                it.SubItems.Add(Mp4Probe.Fmt(meta.Seconds));
                it.SubItems.Add(meta.Valid ? $"{meta.Height}p" : "—");
                it.SubItems.Add(meta.HasAudio ? "有" : "无");
                it.SubItems.Add(QuotaReport.Mb(c.Bytes));
                it.SubItems.Add(c.Name);
                it.ImageIndex = -1;
                Items.Add(it);
            }
        }
        finally { EndUpdate(); }

        FitNameColumn();   // 竖向滚动条是加完行才出现的，它一出现客户宽度就变窄
        LoadThumbs();
        if (Items.Count > 0) Items[0].Selected = true;
    }

    private void HoverTo(int idx)
    {
        if (idx == _hoverRow) return;
        _hoverRow = idx;
        Invalidate();
    }

    private void FitNameColumn()
    {
        if (Columns.Count < 8) return;
        int fixedWidth = 0;
        for (int i = 0; i < Columns.Count - 1; i++) fixedWidth += Columns[i].Width;
        Columns[^1].Width = Math.Max(120, ClientSize.Width - fixedWidth - 2);
    }

    // ------------------------------------------------------------------ 自绘

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(UiKit.Surface))
            g.FillRectangle(b, e.Bounds);
        using (var pen = new Pen(UiKit.Line))
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
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

        int imgIdx = e.Item.ImageIndex;
        if (imgIdx >= 0 && imgIdx < _thumbs.Images.Count)
        {
            var img = _thumbs.Images[imgIdx];
            var dst = new Rectangle(row.X + 9, row.Y + Math.Max(0, (row.Height - ThumbH) / 2), ThumbW, ThumbH);
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
            cell = new Rectangle(cell.X + TextInset, cell.Y, Math.Max(0, cell.Width - TextInset), cell.Height);
        cell = new Rectangle(cell.X + 6, cell.Y, Math.Max(0, cell.Width - 12), cell.Height);

        var ink = e.ColumnIndex switch
        {
            1 => clip is null ? UiKit.Text : KindInk(clip.Kind),
            2 => UiKit.Accent,
            _ => UiKit.Text,
        };
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
        }
        base.Dispose(disposing);
    }
}
