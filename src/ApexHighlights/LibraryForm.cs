using System.Diagnostics;
using System.Globalization;

namespace ApexHighlights;

/// <summary>
/// 回顾库窗口。
///
/// 职责上刻意做得很薄：看清单、看一帧、删除、去目录，**不内嵌播放器**
/// （双击交给系统默认播放器，用户明确要的）。
///
/// 结构上分三块，各自可以单独改：
///   · ClipListView   —— 清单控件：行怎么画、缩略图怎么取（见该文件）；
///   · LibraryForm    —— 这里的窗口骨架：四条横带的布局 + 数据流 + 按钮行为；
///   · UiKit          —— 一套自绘的小零件（圆角按钮/卡片/预览/键值表/滚动条）。
///
/// 外观上「全自绘」：WinForms 原生控件只有直角和系统灰，换主题换不掉形状，
/// 所以列表、按钮、预览、键值区都是自己画的。
/// </summary>
public sealed class LibraryForm : Form
{
    private readonly ClipLibrary _lib;
    private QuotaPolicy _policy;

    private readonly ClipListView _list = new();
    private readonly PreviewBox _preview = new();
    private readonly MetaList _meta = new();
    private readonly Label _note = new();
    private readonly Label _empty = new();
    private readonly Label _title = new();
    private readonly Label _count = new();
    private readonly PillSelect _kindFilter = new();
    private readonly PillSelect _timeFilter = new();
    private readonly PillSelect _thCombo = new();
    private readonly Label _thLabel = new();
    private readonly CueTextBox _search = new();
    private readonly Label _status = new();
    private readonly Label _statusRight = new();

    // 四条横带。位置全部由 LayoutBands 算，不参与任何自动布局。
    private readonly Panel _head = new();
    private readonly Panel _bar = new();
    private readonly Panel _body = new();
    private readonly Panel _foot = new();
    private readonly RoundButton _btnPlay = new("播放", 104);
    private readonly RoundButton _btnReveal = new("打开目录", 118);
    private readonly RoundButton _btnDel = new("删除", 88);
    private Panel? _searchPill, _thPill, _kindPill, _timePill;
    private SurfaceCard? _listCard;
    private Panel? _sideWrap;
    private bool _bandsReady;

    private float _threshold;

    /// <summary>
    /// 当前命中阈值，由托盘传进来；本窗口只负责显示和回调，不自己存配置。
    /// setter 会顺手同步下拉（构造期对象初始化器赋值时下拉可能还没建）。
    /// </summary>
    public float Threshold
    {
        get => _threshold;
        set
        {
            _threshold = value;
            if (_thCombo.Items.Count > 0) SyncThresholdCombo();
        }
    }
    /// <summary>用户在库里改了灵敏度后通知托盘（托盘负责落盘 + 套到运行中的采集上）。</summary>
    public Action<float>? OnThresholdChanged { get; set; }

    private List<ClipInfo> _all = new();
    private List<ClipInfo> _shown = new();

    public LibraryForm(ClipLibrary lib, QuotaPolicy policy)
    {
        _lib = lib;
        _policy = policy;

        Text = "ApexHighlights — 回顾库";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 600);
        Size = new Size(1360, 760);
        Font = UiKit.F95;
        Icon = AppIcon.Shared;
        BackColor = UiKit.Bg;
        DoubleBuffered = true;
        KeyPreview = true;

        BuildHead();
        BuildBar();
        BuildBody();
        BuildFoot();
        MountShell();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        };

        // 构造期就把档位建出来：外部用对象初始化器设 Threshold 时下拉已经存在，
        // setter 里的同步才生效；Load 里再建一次是给「先 Show 后设值」的顺序兜底。
        BuildThresholdItems();

        Load += (_, _) =>
        {
            UiKit.ApplyWindowChrome(this);
            UiKit.DarkScrollbars(_list);
            BuildThresholdItems();
            Reload();
        };
    }

    // ------------------------------------------------------------------ 布局

    // 四条横带全部手工算坐标，不用 TableLayoutPanel / FlowLayoutPanel：
    // 自动布局在「AutoSize 列里塞自绘控件」和 DPI 缩放下会给出反直觉的结果
    // （第一版就踩了：标题栏和工具条上下颠倒，搜索框压到列表上）。
    // 这里一共四行，算一遍比调一遍快，也稳。

    private void BuildHead()
    {
        _head.BackColor = UiKit.Bg;

        _title.Text = "回顾库";
        _title.Font = UiKit.F135B;
        _title.ForeColor = UiKit.Text;
        _title.AutoSize = true;
        _title.BackColor = UiKit.Bg;
        _title.Location = new Point(0, 2);

        _count.Font = UiKit.F9;
        _count.ForeColor = UiKit.Dim;
        _count.AutoSize = true;
        _count.BackColor = UiKit.Bg;
        _count.Text = "还没读盘";
        _count.Location = new Point(2, 40);

        _thLabel.Font = UiKit.F9;
        _thLabel.ForeColor = UiKit.Dim;
        _thLabel.AutoSize = true;
        _thLabel.TextAlign = ContentAlignment.MiddleLeft;

        _thPill = PillHostSelect(_thCombo, 232);

        _search.Cue = "搜索文件名";
        _searchPill = PillHost(_search, 250);
        ((Pill)_searchPill).Fill = UiKit.PillFill;

        _head.Controls.Add(_title);
        _head.Controls.Add(_count);
        _head.Controls.Add(_thLabel);
        _head.Controls.Add(_thPill);
        _head.Controls.Add(_searchPill);
    }

    private void BuildBar()
    {
        _bar.BackColor = UiKit.Bg;

        _btnPlay.Style = RoundButton.Look.Primary;
        _btnPlay.Click += (_, _) => OpenSelected();
        _btnReveal.Click += (_, _) => RevealSelected();
        _btnDel.Style = RoundButton.Look.Danger;
        _btnDel.Click += (_, _) => DeleteSelected();

        _kindFilter.Items.AddRange(new object[]
            { "全部种类", "击倒", "再次击倒", "助攻击倒", "小队淘汰", "助攻淘汰", "击杀", "被击倒" });
        _kindFilter.SelectedIndex = 0;   // ComboBox 不像 ListBox 会自动选第一项，不设就是空白
        _kindFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _kindPill = PillHostSelect(_kindFilter, 168);

        _timeFilter.Items.AddRange(new object[] { "全部时间", "今天", "最近 7 天", "最近 30 天" });
        _timeFilter.SelectedIndex = 0;
        _timeFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _timePill = PillHostSelect(_timeFilter, 158);

        _bar.Controls.Add(_btnPlay);
        _bar.Controls.Add(_btnReveal);
        _bar.Controls.Add(_btnDel);
        _bar.Controls.Add(_kindPill);
        _bar.Controls.Add(_timePill);
    }

    private void BuildBody()
    {
        _body.BackColor = UiKit.Bg;

        // ---- 左：清单 ----
        _listCard = new SurfaceCard();
        var listPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 6), BackColor = UiKit.Surface };

        _list.Dock = DockStyle.Fill;
        _list.SelectionChanged += ShowSelection;
        _list.ClipActivated += OpenSelected;
        _list.DeleteRequested += DeleteSelected;
        listPad.Controls.Add(_list);

        _empty.Dock = DockStyle.Fill;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.ForeColor = UiKit.Dim;
        _empty.Font = UiKit.F10;
        _empty.BackColor = UiKit.Sunken;
        _empty.Visible = false;
        listPad.Controls.Add(_empty);
        _empty.BringToFront();
        _listCard.Controls.Add(listPad);

        // ---- 右：一帧大图 + 元数据 ----
        _sideWrap = new Panel { BackColor = UiKit.Bg };
        var sideCard = new SurfaceCard { Dock = DockStyle.Fill, Padding = new Padding(12) };

        _preview.Dock = DockStyle.Top;
        _preview.Height = 212;
        _preview.EmptyText = "选一条看看";
        _preview.DoubleClick += (_, _) => OpenSelected();

        var metaPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 6), BackColor = UiKit.Surface };
        _meta.Dock = DockStyle.Fill;
        metaPad.Controls.Add(_meta);

        _note.Dock = DockStyle.Bottom;
        _note.Height = 44;
        _note.ForeColor = UiKit.Dim;
        _note.Font = UiKit.F9;
        _note.Text = "";
        _note.TextAlign = ContentAlignment.MiddleLeft;

        sideCard.Controls.Add(metaPad);
        sideCard.Controls.Add(_note);
        sideCard.Controls.Add(_preview);
        _sideWrap.Controls.Add(sideCard);

        _body.Controls.Add(_listCard);
        _body.Controls.Add(_sideWrap);
    }

    private void BuildFoot()
    {
        _foot.BackColor = UiKit.Bg;
        _foot.Paint += (_, e) =>
        {
            using var pen = new Pen(UiKit.Line);
            e.Graphics.DrawLine(pen, 22, 0, _foot.Width - 22, 0);
        };

        _status.ForeColor = UiKit.Dim;
        _status.Font = UiKit.F9;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = "";

        _statusRight.ForeColor = UiKit.Dim;
        _statusRight.Font = UiKit.F9;
        _statusRight.TextAlign = ContentAlignment.MiddleRight;
        _statusRight.Text = "";

        _foot.Controls.Add(_status);
        _foot.Controls.Add(_statusRight);
    }

    /// <summary>
    /// 把四条带挂上去。停靠顺序是**逆 z 序**的：最后 Add 的最先停靠、拿最外侧的边。
    /// 所以这里从「最外层」往内倒着加，读起来正好等于屏幕上的顺序。
    /// </summary>
    private void MountShell()
    {
        Controls.Add(_body);
        Controls.Add(_foot);
        Controls.Add(_bar);
        Controls.Add(_head);
        _bandsReady = true;
        LayoutBands();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_bandsReady) LayoutBands();
    }

    private void LayoutBands()
    {
        const int pad = 22;
        const int headH = 78;
        const int barH = 56;
        const int footH = 34;

        int w = ClientSize.Width;
        int h = ClientSize.Height;

        _head.SetBounds(pad, 8, w - pad * 2, headH);
        _bar.SetBounds(pad, 8 + headH, w - pad * 2, barH);
        _foot.SetBounds(0, h - footH, w, footH);

        int bodyTop = 8 + headH + barH;
        _body.SetBounds(pad, bodyTop, w - pad * 2, Math.Max(120, h - bodyTop - footH - 10));

        // ---- 标题栏：左标题、右「灵敏度 + 搜索」----
        int hw = _head.Width;
        var search = _searchPill!;
        var thPill = _thPill!;
        search.SetBounds(hw - search.Width, 8, search.Width, 38);
        thPill.SetBounds(search.Left - 8 - thPill.Width, 8, thPill.Width, 38);
        _thLabel.Text = "灵敏度";
        int thLabelW = TextRenderer.MeasureText(_thLabel.Text, _thLabel.Font).Width;
        _thLabel.SetBounds(thPill.Left - 10 - thLabelW, 8 + (38 - _thLabel.Height) / 2, thLabelW, _thLabel.Height);

        // ---- 工具条：左三个动作、右两个筛选 ----
        const int btnY = (barH - 38) / 2;
        _btnPlay.SetBounds(0, btnY, 104, 38);
        _btnReveal.SetBounds(112, btnY, 118, 38);
        _btnDel.SetBounds(238, btnY, 88, 38);
        var timePill = _timePill!;
        var kindPill = _kindPill!;
        timePill.SetBounds(_bar.Width - timePill.Width, btnY, timePill.Width, 38);
        kindPill.SetBounds(timePill.Left - 8 - kindPill.Width, btnY, kindPill.Width, 38);

        // ---- 正文：左边清单、右边详情 ----
        int sideW = 420;
        _sideWrap!.SetBounds(_body.Width - sideW, 0, sideW, _body.Height);
        _listCard!.SetBounds(0, 0, Math.Max(200, _body.Width - sideW - 16), _body.Height);

        // ---- 状态栏 ----
        _status.SetBounds(22, 0, Math.Max(120, _foot.Width - 22 - 460 - 24), footH);
        _statusRight.SetBounds(_foot.Width - 22 - 460, 0, 460, footH);
    }

    /// <summary>把控件塞进胶囊容器里并居中，圆角是靠容器画的。</summary>
    private static Pill PillHost(Control inner, int width, int height = 38, int padX = 15)
    {
        var pill = new Pill { Size = new Size(width, height), Margin = new Padding(8, 2, 0, 2) };
        inner.Font = UiKit.F95;
        pill.Controls.Add(inner);
        void Place()
        {
            int h = inner.Height > 0 ? inner.Height : 22;
            inner.SetBounds(padX, Math.Max(2, (pill.Height - h) / 2), Math.Max(20, pill.Width - padX * 2), h);
        }
        pill.Resize += (_, _) => Place();
        pill.HandleCreated += (_, _) => Place();
        Place();
        return pill;
    }

    /// <summary>下拉的容器：不画底板，胶囊由 PillSelect 自己画满整块。</summary>
    private static Pill PillHostSelect(PillSelect sel, int width, int height = 38)
    {
        var pill = new Pill { Size = new Size(width, height), Chrome = false, Margin = new Padding(8, 2, 0, 2) };
        pill.BackColor = UiKit.Bg;
        pill.Controls.Add(sel);
        void Place() => sel.SetBounds(0, 0, pill.Width, pill.Height);
        pill.Resize += (_, _) => Place();
        pill.HandleCreated += (_, _) => Place();
        Place();
        return pill;
    }

    // ------------------------------------------------------------------ 数据

    /// <summary>
    /// 灵敏度下拉：第一项是「按模板自带」，后面是固定档位。
    /// 用固定档位而不是滑块，是为了让用户一眼看到「0.70 推荐」这种标签，
    /// 而不是面对一个 0.30–0.95 的无刻度滑块瞎拖。
    /// </summary>
    private void BuildThresholdItems()
    {
        _thCombo.Items.Clear();
        _thCombo.Items.Add("按模板自带");
        foreach (var v in DetectPolicy.Presets)
            _thCombo.Items.Add($"{v:F2} · {DetectPolicy.ShortHintFor(v)}");
        SyncThresholdCombo();
    }

    /// <summary>把下拉同步到当前阈值（不触发回调，避免初始化时把配置反写一遍）。</summary>
    private void SyncThresholdCombo()
    {
        var cur = Threshold;
        int idx = 0;
        if (cur > 0)
        {
            var i = Array.FindIndex(DetectPolicy.Presets, v => Math.Abs(v - cur) < 0.001f);
            idx = i < 0 ? 0 : i + 1;
        }
        _thCombo.SelectedIndexChanged -= OnThresholdUiChanged;
        _thCombo.SelectedIndex = idx;
        _thCombo.SelectedIndexChanged += OnThresholdUiChanged;
        _thLabel.Text = cur > 0 ? $"灵敏度 {cur:F2}" : "灵敏度";
    }

    private void OnThresholdUiChanged(object? sender, EventArgs e)
    {
        var idx = _thCombo.SelectedIndex;
        var v = idx <= 0 ? 0f : DetectPolicy.Presets[idx - 1];
        Threshold = DetectPolicy.Clamp(new DetectPolicy { Threshold = v }).Threshold;
        _thLabel.Text = "灵敏度";
        OnThresholdChanged?.Invoke(Threshold);
    }

    /// <summary>外部改了阈值后刷新下拉显示。</summary>
    public void SetThreshold(float v)
    {
        Threshold = v;
        SyncThresholdCombo();
    }

    /// <summary>外部（托盘）改了配额策略后调用，重算状态栏。</summary>
    public void SetPolicy(QuotaPolicy policy)
    {
        _policy = policy;
        UpdateStatus();
    }

    public void Reload()
    {
        _all = _lib.Scan();
        _all.Reverse();   // 新的在前，这里不是淘汰顺序
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = ClipQuery.FromUi(_kindFilter.SelectedIndex, _timeFilter.SelectedIndex, _search.Text, DateTime.Now);
        _shown = ClipQuery.Apply(_all, filter);

        _list.SetClips(_shown);   // 重建行 + 重取缩略图 + 默认选中第一行（会回调 ShowSelection）

        _empty.Visible = _shown.Count == 0;
        _empty.Text = _all.Count == 0
            ? "库里还没有片段\r\n\r\n下一把录到击倒会自动存进来"
            : "这个筛选下没有片段\r\n\r\n把「种类/时间」调回全部，或清空搜索";

        if (_list.RowCount == 0) ShowSelection();
        UpdateStatus();
    }

    private void ShowSelection()
    {
        var c = _list.SelectedClip;
        if (c is null)
        {
            _preview.Image?.Dispose();
            _preview.Image = null;
            _meta.Set(("提示", _shown.Count == 0 ? "这个筛选下没有片段" : "在左边选一条"));
            _note.Text = _shown.Count == 0
                ? "录一段之后回来按 F5 刷新"
                : "双击或 Enter 用系统播放器打开 · Delete 删除";
            return;
        }

        var meta = Mp4Probe.Read(c.Path);
        _preview.Image?.Dispose();
        _preview.Image = ShellThumb.Get(c.Path, 720, 405);

        _meta.Set(
            ("时间", c.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss")),
            ("种类", ClipKinds.Label(c.Kind)),
            ("分数", c.Score.ToString("F3", CultureInfo.InvariantCulture)),
            ("时长", Mp4Probe.Fmt(meta.Seconds)),
            ("分辨率", meta.Valid ? $"{meta.Width} × {meta.Height}" : "—"),
            ("音轨", meta.HasAudio ? $"有 · {meta.Codec}" : "无"),
            ("体积", QuotaReport.Mb(c.Bytes)));

        _note.Text = "双击或 Enter 用系统播放器打开 · Delete 删除";
    }

    private void UpdateStatus()
    {
        long total = _all.Sum(c => c.Bytes);
        var shownTotal = _shown.Sum(c => c.Bytes);
        var q = _policy.QuotaGb <= 0 ? "不限" : $"{_policy.QuotaGb}GB";
        var r = _policy.RetainDays <= 0 ? "永久" : $"{_policy.RetainDays} 天";
        var free = QuotaReport.Mb(QuotaEnforcer.FreeBytes(_lib.Dir));
        _status.Text = $"{_shown.Count} / {_all.Count} 片　·　{QuotaReport.Mb(shownTotal)}（共 {QuotaReport.Mb(total)}）";
        _statusRight.Text = $"配额 {q}　·　保留 {r}　·　磁盘可用 {free}";
        _count.Text = _all.Count == 0
            ? "还没有片段"
            : $"{_all.Count} 片 · 合计 {QuotaReport.Mb(total)}";
    }

    private void OpenSelected()
    {
        var c = _list.SelectedClip;
        if (c is null) return;
        try { Process.Start(new ProcessStartInfo(c.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打不开：" + ex.Message, "ApexHighlights"); }
    }

    private void RevealSelected()
    {
        var c = _list.SelectedClip;
        try
        {
            if (c is null) Process.Start(new ProcessStartInfo(_lib.Dir) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{c.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "打不开目录：" + ex.Message, "ApexHighlights"); }
    }

    private void DeleteSelected()
    {
        var sel = _list.SelectedItems.Cast<ListViewItem>().Select(i => (ClipInfo)i.Tag!).ToList();
        if (sel.Count == 0) return;
        var ask = sel.Count == 1
            ? $"删掉这一片？\r\n\r\n{sel[0].Name}\r\n\r\n文件会直接从磁盘删除，不进回收站。"
            : $"删掉选中的 {sel.Count} 片？\r\n\r\n文件会直接从磁盘删除，不进回收站。";
        if (MessageBox.Show(this, ask, "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        int ok = 0;
        foreach (var c in sel) if (_lib.Delete(c)) ok++;
        if (ok != sel.Count)
            MessageBox.Show(this, $"{sel.Count} 片里删成功 {ok} 片，其余可能被占用。", "ApexHighlights");
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _preview.Image?.Dispose();
        base.Dispose(disposing);
    }
}
