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
    private ClipLibrary _lib;   // 换过目录就换一个（不是 readonly）
    private QuotaPolicy _policy;

    private readonly ClipListView _list = new();
    private readonly ClipDetailPane _detail = new();
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
    private readonly HoverTip _tip = new();

    // 四条横带。位置全部由 LayoutBands 算，不参与任何自动布局。
    private readonly Panel _head = new();
    private readonly Panel _bar = new();
    private readonly Panel _body = new();
    private readonly Panel _foot = new();
    private readonly RoundButton _btnPlay = new("播放", 104);
    private readonly RoundButton _btnReveal = new("打开目录", 118);
    private readonly RoundButton _btnDir = new("选择目录", 118);
    private readonly RoundButton _btnDel = new("删除", 88);
    private readonly RoundButton _btnCancel = new("取消", 88) { Visible = false };
    private readonly RoundButton _btnParams = new("录制参数", 118);
    /// <summary>头部那行小灰字：说清「录的是窗口尺寸」这件事，免得有人拿它跟游戏内分辨率较真。</summary>
    private readonly Label _sizeNote = new();
    private Panel? _searchPill, _thPill, _kindPill, _timePill;
    private SurfaceCard? _listCard;
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

    /// <summary>
    /// 用户在库里点了「选择目录」。返回 false = 宿主否决（比如用户在「要不要搬走旧片段」上点了取消），
    /// 窗口就什么都不改。没有挂宿主（单开回顾库看片时）就自己切。
    /// </summary>
    public Func<string, bool>? OnClipsDirRequested { get; set; }

    private List<ClipInfo> _all = new();
    private List<ClipInfo> _shown = new();

    public LibraryForm(ClipLibrary lib, QuotaPolicy policy)
    {
        _lib = lib;
        _policy = policy;

        Text = "ApexHighlights — 回顾库";
        StartPosition = FormStartPosition.CenterScreen;
        // 1000 宽时，批量态多出来的「取消」会顶到右侧筛选胶囊上（量过：重叠 100 多个像素）
        MinimumSize = new Size(1120, 600);
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
            else if (e.KeyCode == Keys.Escape && _list.SelectMode) { EndBatch(); e.Handled = true; }
        };

        WireTips();

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

        _sizeNote.Text = "录制尺寸跟随游戏窗口（非游戏内渲染分辨率）";
        _sizeNote.Font = UiKit.F9;
        _sizeNote.ForeColor = Color.FromArgb(120, 124, 136);
        _sizeNote.AutoSize = true;
        _sizeNote.BackColor = UiKit.Bg;

        _head.Controls.Add(_title);
        _head.Controls.Add(_count);
        _head.Controls.Add(_sizeNote);
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
        _btnDir.Click += (_, _) => PickClipsDir();
        _btnParams.Click += (_, _) => OnParamsRequested?.Invoke();
        _btnDel.Style = RoundButton.Look.Danger;
        _btnDel.Click += (_, _) =>
        {
            if (_list.SelectMode) DeleteChecked();
            else BeginBatch();
        };
        _btnCancel.Click += (_, _) => EndBatch();
        _list.CheckedChanged += UpdateBatchButtons;

        // 种类列表跟着 ClipKinds.All 走：以前手写的这份多出「击杀」「被击倒」，
        // BannerKind 判不出这两种，选它们永远是空列表，白让人以为录漏了。
        var kinds = new List<object> { "全部种类" };
        foreach (var k in ClipKinds.All) kinds.Add(ClipKinds.Label(k));
        _kindFilter.Items.AddRange(kinds.ToArray());
        _kindFilter.SelectedIndex = 0;   // ComboBox 不像 ListBox 会自动选第一项，不设就是空白
        _kindFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _kindPill = PillHostSelect(_kindFilter, 168);

        _timeFilter.Items.AddRange(new object[] { "全部时间", "今天", "最近 7 天", "最近 30 天" });
        _timeFilter.SelectedIndex = 0;
        _timeFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _timePill = PillHostSelect(_timeFilter, 158);

        _bar.Controls.Add(_btnPlay);
        _bar.Controls.Add(_btnReveal);
        _bar.Controls.Add(_btnDir);
        _bar.Controls.Add(_btnParams);
        _bar.Controls.Add(_btnDel);
        _bar.Controls.Add(_btnCancel);
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
        _detail.OpenRequested += (_, _) => OpenSelected();

        _body.Controls.Add(_listCard);
        _body.Controls.Add(_detail);
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
        // 这一行显示的就是「配额 / 保留天数」，用户最想改它——做成可点，别让人去托盘里翻。
        _statusRight.Cursor = Cursors.Hand;
        _statusRight.Click += (_, _) => ShowQuotaMenu();

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
        // 头部是「大标题 + 计数 + 一行说明」三层，78 px 放不下第三行（会压到工具条上），改 92
        const int headH = 92;
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
        // 原来是贴在右边、和计数同一行 —— 但右边是「灵敏度 + 搜索」两个胶囊，
        // 那行字会被压在胶囊底下、还顶到窗口右边缘。改到左侧标题块下面单独一行。
        _sizeNote.Location = new Point(2, _count.Bottom + 4);
        _thLabel.Text = "灵敏度";
        int thLabelW = TextRenderer.MeasureText(_thLabel.Text, _thLabel.Font).Width;
        _thLabel.SetBounds(thPill.Left - 10 - thLabelW, 8 + (38 - _thLabel.Height) / 2, thLabelW, _thLabel.Height);

        // ---- 工具条：左四个动作、右两个筛选 ----
        const int btnY = (barH - 38) / 2;
        _btnPlay.SetBounds(0, btnY, 104, 38);
        _btnReveal.SetBounds(112, btnY, 118, 38);
        _btnDir.SetBounds(238, btnY, 118, 38);
        _btnDel.SetBounds(364, btnY, _list.SelectMode ? 124 : 88, 38);
        _btnParams.SetBounds(_btnDel.Right + 10, btnY, 118, 38);
        _btnCancel.SetBounds(_btnParams.Right + 10, btnY, 88, 38);
        var timePill = _timePill!;
        var kindPill = _kindPill!;
        timePill.SetBounds(_bar.Width - timePill.Width, btnY, timePill.Width, 38);
        kindPill.SetBounds(timePill.Left - 8 - kindPill.Width, btnY, kindPill.Width, 38);

        // ---- 正文：左边清单、右边详情 ----
        // 336 是量出来的：再窄下来元数据「2560 × 1440」就开始折行，再宽就让清单少一列。
        // 窗口本身很小时按比例收，别让清单被挤成一条缝。
        int sideW = Math.Clamp(_body.Width / 4, 300, 336);
        _detail.SetBounds(_body.Width - sideW, 0, sideW, _body.Height);
        _listCard!.SetBounds(0, 0, Math.Max(200, _body.Width - sideW - 16), _body.Height);
        _detail.FitPreview();

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

    // ------------------------------------------------------------------ 提示

    private const string ThresholdTip =
        "命中阈值：横幅识别结果与模板的匹配分。\r\n" +
        "分数 ≥ 阈值才判定为命中并自动保存片段。\r\n" +
        "调高更严格：误报少，但模糊、被遮挡的横幅可能漏掉；\r\n" +
        "调低更宽松：几乎不漏，但相似画面可能被当成击倒。\r\n" +
        "默认 0.70。";

    /// <summary>
    /// 给控件挂悬停说明：延迟 400ms（扫过去不弹），移开就收。
    /// 系统 ToolTip 是浅灰直角气泡，跟这套深色圆角界面不搭，所以用 HoverTip 自己画。
    /// </summary>
    private void Tip(Control c, string text)
    {
        System.Windows.Forms.Timer? t = null;
        c.MouseEnter += (_, _) =>
        {
            t = new System.Windows.Forms.Timer { Interval = 400 };
            t.Tick += (_, _) =>
            {
                t?.Stop();
                t?.Dispose();
                t = null;
                _tip.ShowFor(c, text);
            };
            t.Start();
        };
        c.MouseLeave += (_, _) =>
        {
            if (t is not null) { t.Stop(); t.Dispose(); t = null; }
            _tip.HideTip();
        };
    }

    private void WireTips()
    {
        Tip(_thLabel, ThresholdTip);
        if (_thPill is not null) Tip(_thPill, ThresholdTip);
        if (_kindPill is not null)
            Tip(_kindPill, "按识别到的横幅类型筛选：" +
                string.Join(" / ", ClipKinds.All.Select(ClipKinds.Label)) + "。");
        if (_timePill is not null) Tip(_timePill, "按片段保存的时间筛选，今天 / 最近 7 天 / 最近 30 天。");
        if (_searchPill is not null) Tip(_searchPill, "按文件名搜索，支持只输一段（比如 1028-2139）。");
    }

    /// <summary>自检用：把阈值说明按到标签上，方便截图看气泡长什么样。</summary>
    internal void ShowTestTip(string text) => _tip.ShowFor(_thLabel, text);

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
        // 值已经写在胶囊里（「0.70 · 默认推荐」），标签再挂一遍就重了
        _thLabel.Text = "灵敏度";
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

    /// <summary>改配额/保留天数的出口：库窗口只负责「问」，真正落到 policy + config.json 由宿主做。</summary>
    public Action<long, int>? OnQuotaChanged { get; set; }

    /// <summary>用户点了工具条上的「录制参数」。宿主（托盘）负责把面板弹出来。</summary>
    public Action? OnParamsRequested;

    /// <summary>状态栏这一行的「点开就能改」菜单：两个子项组，和托盘里那套档位一致。</summary>
    private void ShowQuotaMenu()
    {
        var menu = CreateQuotaMenu();
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(_statusRight, new Point(_statusRight.Width, 0), ToolStripDropDownDirection.AboveLeft);
    }

    /// <summary>
    /// 建出配额菜单但不弹出来——自检要能点它的子项，真 Show 出来会把模态焦点抢走。
    /// </summary>
    internal ContextMenuStrip CreateQuotaMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = UiKit.F95 };
        var capRoot = new ToolStripMenuItem("磁盘配额：" + QuotaPolicy.QuotaLabel(_policy.QuotaGb));
        var keepRoot = new ToolStripMenuItem("保留天数：" + QuotaPolicy.RetainLabel(_policy.RetainDays));

        foreach (var gb in QuotaPolicy.QuotaPresets)
        {
            var it = new ToolStripMenuItem(QuotaPolicy.QuotaLabel(gb)) { Checked = gb == _policy.QuotaGb };
            it.Click += (_, _) => OnQuotaChanged?.Invoke(gb, _policy.RetainDays);
            capRoot.DropDownItems.Add(it);
        }
        foreach (var days in QuotaPolicy.RetainPresets)
        {
            var it = new ToolStripMenuItem(QuotaPolicy.RetainLabel(days)) { Checked = days == _policy.RetainDays };
            it.Click += (_, _) => OnQuotaChanged?.Invoke(_policy.QuotaGb, days);
            keepRoot.DropDownItems.Add(it);
        }

        menu.Items.Add(capRoot);
        menu.Items.Add(keepRoot);
        return menu;
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
        if (_list.SelectMode) UpdateBatchButtons();   // 重建行会清空勾选，按钮文字得跟着回零
        UpdateStatus();
    }

    private void ShowSelection()
    {
        _detail.Bind(_list.SelectedClip, _shown.Count == 0);
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

        // 批量态时状态栏兼作提示：不然勾了 5 片却只看到「35 / 35 片」
        if (_list.SelectMode)
            _status.Text = _list.CheckedCount == 0
                ? "批量删除：勾选要删的片段（表头「全选」= 一次全勾）"
                : $"批量删除：已勾选 {_list.CheckedCount} 片，点「删除选中」确认";
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
        DeleteMany(sel);
    }

    // ------------------------------------------------------------------ 批量删除

    /// <summary>
    /// 进批量态：清单左边冒出勾选框、表头冒出「全选」，删除按钮变成「删除选中」。
    /// 为什么先点「删除」再出勾选框，而不是一直显示：这窗口平时是拿来翻片的，
    /// 每行前面常驻一个方框会把它变成「文件管理器」，看一眼片还得先无视一堆框。
    /// </summary>
    private void BeginBatch()
    {
        if (_shown.Count == 0)
        {
            MessageBox.Show(this, "这个筛选下没有片段，没有可删的。", "批量删除");
            return;
        }
        _list.SetSelectMode(true);
        UpdateBatchButtons();
        LayoutBands();
        UpdateStatus();   // 状态栏在批量态会换成「勾选要删的片段…」
    }

    private void EndBatch()
    {
        if (!_list.SelectMode) return;
        _list.SetSelectMode(false);
        UpdateBatchButtons();
        LayoutBands();
        UpdateStatus();
    }

    /// <summary>按钮文字跟着勾选数走；一片没勾时按钮是灰的（点它没有意义）。</summary>
    private void UpdateBatchButtons()
    {
        if (!_list.SelectMode)
        {
            _btnDel.Text = "删除";
            _btnDel.Enabled = true;
            _btnCancel.Visible = false;
            _btnCancel.Enabled = false;
            return;
        }
        int n = _list.CheckedCount;
        _btnDel.Text = n == 0 ? "删除选中" : $"删除选中（{n}）";
        _btnDel.Enabled = n > 0;
        _btnCancel.Visible = true;
        _btnCancel.Enabled = true;
        if (_bandsReady) UpdateStatus();   // 「已勾选 N 片」跟着勾选走
    }

    private void DeleteChecked()
    {
        var checkedClips = _list.CheckedClips;
        if (checkedClips.Count == 0)
        {
            MessageBox.Show(this, "还没勾选片段：点片段前面的方框，或点表头的「全选」。", "批量删除");
            return;
        }
        var ask = $"删掉勾选的 {checkedClips.Count} 片？\r\n\r\n" +
                  "文件会直接从磁盘删除，不进回收站。";
        if (MessageBox.Show(this, ask, "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        int ok = 0;
        foreach (var c in checkedClips) if (_lib.Delete(c)) ok++;
        if (ok != checkedClips.Count)
            MessageBox.Show(this, $"{checkedClips.Count} 片里删成功 {ok} 片，其余可能被占用。", "ApexHighlights");
        EndBatch();
        Reload();
    }

    /// <summary>自检/截图用：直接进批量态并勾上前几片（点按钮要真鼠标，截图脚本没法点）。</summary>
    internal void EnterBatchForShot(int checkCount = 3)
    {
        Reload();                       // 截图那会儿 Load 还没跑过，_shown 是空的
        if (_list.RowCount == 0) return; // 空库别弹「没有可删的」把截图脚本卡住
        if (!_list.SelectMode)
        {
            _list.SetSelectMode(true);
            UpdateBatchButtons();
            LayoutBands();
        }
        int n = Math.Min(checkCount, _list.RowCount);
        for (int i = 0; i < n; i++) _list.ToggleChecked(i);
    }

    private void DeleteMany(List<ClipInfo> sel)
    {
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

    // ------------------------------------------------------------------ 目录

    /// <summary>
    /// 点「选择目录」：挑一个文件夹当回顾库。
    /// 「要不要把现有片段搬过去」由宿主（托盘）问，见 OnClipsDirRequested —— 单开这个窗口看片时
    /// 没有采集器也没有配置可落，就只切显示。
    /// </summary>
    private void PickClipsDir()
    {
        var old = _lib.Dir;
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择回顾库文件夹（以后录的片段存在这里）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(old) ? old : Paths.DataRoot,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var target = Paths.NormalizeDir(dlg.SelectedPath);
        if (target.Length == 0)
        {
            MessageBox.Show(this, "这个路径不能用：要绝对路径，且不能带 < > | \" ? * 这些字符。", "回顾库目录");
            return;
        }
        if (SameDir(old, target)) return;
        if (OnClipsDirRequested is { } ask && !ask(target)) return;

        // 宿主（托盘）通常已经在回调里把库换好了，SetClipsDir 发现目录没变就什么都不做
        Paths.SetClipsRoot(target);
        SetClipsDir(target);
    }

    /// <summary>宿主（托盘）把目录改好之后，让本窗口跟着换一份库。</summary>
    public void SetClipsDir(string dir)
    {
        var full = Paths.NormalizeDir(dir);
        if (SameDir(_lib.Dir, full.Length == 0 ? Paths.DefaultClips : full)) return;
        _lib = new ClipLibrary(full.Length == 0 ? Paths.DefaultClips : full);
        Reload();
    }

    private static bool SameDir(string a, string b)
    {
        try
        {
            var x = Path.GetFullPath(a).TrimEnd('\\', '/');
            var y = Path.GetFullPath(b).TrimEnd('\\', '/');
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
