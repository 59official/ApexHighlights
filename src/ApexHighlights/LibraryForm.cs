using System.Diagnostics;
using System.Globalization;

namespace ApexHighlights;

/// <summary>
/// 回顾库窗口。
///
/// 形态上刻意做得很薄：列表 + 预览 + 几个按钮，**不内嵌播放器**（用户明确要的：
/// 双击用系统默认播放器打开）。所以这个窗口的职责只有「看清单、看一帧、删除、去目录」。
///
/// 缩略图和时长都是后台线程加载：录制可能正在同时跑，不能因为开个库窗口就卡住采集。
/// </summary>
public sealed class LibraryForm : Form
{
    private readonly ClipLibrary _lib;
    private QuotaPolicy _policy;

    private readonly ListView _list = new();
    private readonly ImageList _thumbs = new() { ImageSize = new Size(80, 45), ColorDepth = ColorDepth.Depth32Bit };
    private readonly PictureBox _preview = new();
    private readonly Label _meta = new();
    private readonly Label _note = new();
    private readonly ToolStripComboBox _kindFilter = new();
    private readonly ToolStripComboBox _timeFilter = new();
    private readonly ToolStripTextBox _search = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripLabel _thLabel = new();
    private readonly ToolStripComboBox _thCombo = new();

    private float _threshold;

    /// <summary>
    /// 当前命中阈值，由托盘传进来；本窗口只负责显示和回调，不自己存配置。
    /// setter 会顺手同步下拉（构造期对象初始化器赋值时下拉可能还没建，SyncThresholdCombo 里判过空）。
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
    private CancellationTokenSource? _loadCts;

    public LibraryForm(ClipLibrary lib, QuotaPolicy policy)
    {
        _lib = lib;
        _policy = policy;

        Text = "ApexHighlights — 回顾库";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 520);
        Size = new Size(1180, 700);
        Font = new Font("Microsoft YaHei UI", 9f);
        Icon = AppIcon.Shared;

        // ---------- 工具栏 ----------
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 3, 6, 3) };
        bar.Items.Add(Button("刷新", (_, _) => Reload()));
        bar.Items.Add(Button("播放", (_, _) => OpenSelected()));
        bar.Items.Add(Button("打开目录", (_, _) => RevealSelected()));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("删除", (_, _) => DeleteSelected()));

        // 灵敏度直接放工具栏：用户改完立刻看得到、也立刻生效。
        // 放在「回顾库」里而不是只放托盘菜单，是因为用户发现漏录/乱录的时候，
        // 十有八九正开着这个窗口在翻片段，就地能调最省事。
        bar.Items.Add(new ToolStripSeparator());
        _thLabel.Text = "灵敏度";
        bar.Items.Add(_thLabel);
        _thCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _thCombo.Width = 200;
        bar.Items.Add(_thCombo);

        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(new ToolStripLabel("种类"));
        _kindFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _kindFilter.Items.AddRange(new object[] { "全部", "击倒", "击杀", "被击倒" });
        _kindFilter.SelectedIndex = 0;
        _kindFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        bar.Items.Add(_kindFilter);

        bar.Items.Add(new ToolStripLabel("时间"));
        _timeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _timeFilter.Items.AddRange(new object[] { "全部", "今天", "最近 7 天", "最近 30 天" });
        _timeFilter.SelectedIndex = 0;
        _timeFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        bar.Items.Add(_timeFilter);

        bar.Items.Add(new ToolStripLabel("搜索"));
        _search.Width = 160;
        _search.ToolTipText = "文件名片段，比如 1028-13";
        _search.TextChanged += (_, _) => ApplyFilter();
        bar.Items.Add(_search);

        // ---------- 列表 ----------
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.Dock = DockStyle.Fill;
        _list.SmallImageList = _thumbs;
        _list.Columns.Add("时间", 130);
        _list.Columns.Add("种类", 70);
        _list.Columns.Add("分数", 60, HorizontalAlignment.Right);
        _list.Columns.Add("时长", 70, HorizontalAlignment.Right);
        _list.Columns.Add("分辨率", 100);
        _list.Columns.Add("音轨", 50);
        _list.Columns.Add("体积", 80, HorizontalAlignment.Right);
        _list.Columns.Add("文件名", 300);
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => ShowSelection();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
        };

        // ---------- 右侧预览 ----------
        var side = new Panel { Dock = DockStyle.Right, Width = 380, Padding = new Padding(10) };
        _preview.Dock = DockStyle.Top;
        _preview.Height = 214;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(28, 28, 32);
        _preview.DoubleClick += (_, _) => OpenSelected();

        _meta.Dock = DockStyle.Fill;
        _meta.Padding = new Padding(0, 10, 0, 0);
        _meta.ForeColor = Color.FromArgb(228, 228, 232);
        _note.Dock = DockStyle.Bottom;
        _note.Height = 46;
        _note.ForeColor = Color.FromArgb(150, 150, 158);

        side.BackColor = Color.FromArgb(40, 40, 46);
        // Dock 顺序：先填满的放最后加，否则 Fill 会盖住其它
        side.Controls.Add(_meta);
        side.Controls.Add(_note);
        side.Controls.Add(_preview);

        // ---------- 状态栏 ----------
        var strip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.Add(_status);

        Controls.Add(_list);
        Controls.Add(side);
        Controls.Add(bar);
        Controls.Add(strip);

        // 构造期就把档位建出来：这样外部用对象初始化器设 Threshold 时下拉已经存在，
        // setter 里的同步才生效；Load 里再建一次是给「先 Show 后设值」的顺序兜底。
        BuildThresholdItems();

        Load += (_, _) =>
        {
            BuildThresholdItems();
            Reload();
        };
    }

    /// <summary>
    /// 灵敏度下拉：第一项是「按模板自带」，后面是固定档位。
    /// 用 DropDownList + 固定档位而不是滑块，是为了让用户一眼看到「0.70 推荐」这种标签，
    /// 而不是面对一个 0.30–0.95 的无刻度滑块瞎拖。
    /// </summary>
    private void BuildThresholdItems()
    {
        _thCombo.Items.Clear();
        _thCombo.Items.Add("按模板自带");
        foreach (var v in DetectPolicy.Presets)
            _thCombo.Items.Add($"{v:F2}  {DetectPolicy.HintFor(v)}");
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
        _thLabel.Text = Threshold > 0 ? $"灵敏度 {Threshold:F2}" : "灵敏度";
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

    private static ToolStripButton Button(string text, EventHandler onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoSize = false, Width = 74 };
        b.Click += onClick;
        return b;
    }

    public void Reload()
    {
        _loadCts?.Cancel();
        _all = _lib.Scan();
        _all.Reverse();   // 新的在前，这里不是淘汰顺序
        ApplyFilter();
        LoadThumbs();
    }

    private void ApplyFilter()
    {
        IEnumerable<ClipInfo> q = _all;

        var kind = _kindFilter.SelectedIndex switch
        {
            1 => (ClipKind?)ClipKind.Knock,
            2 => ClipKind.Kill,
            3 => ClipKind.Knocked,
            _ => null,
        };
        if (kind is not null) q = q.Where(c => c.Kind == kind);

        var now = DateTime.Now;
        var cut = _timeFilter.SelectedIndex switch
        {
            1 => now.Date,
            2 => now.AddDays(-7),
            3 => now.AddDays(-30),
            _ => DateTime.MinValue,
        };
        if (cut > DateTime.MinValue) q = q.Where(c => c.CreatedLocal >= cut);

        var s = _search.Text.Trim();
        if (s.Length > 0) q = q.Where(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase));

        _shown = q.ToList();

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _thumbs.Images.Clear();
            foreach (var c in _shown)
            {
                var meta = Mp4Probe.Read(c.Path);   // 只读 box 头，微秒级
                var it = new ListViewItem(c.CreatedLocal.ToString("MM-dd HH:mm:ss")) { Tag = c };
                it.SubItems.Add(KindName(c.Kind));
                it.SubItems.Add(c.Score.ToString("F2", CultureInfo.InvariantCulture));
                it.SubItems.Add(Mp4Probe.Fmt(meta.Seconds));
                it.SubItems.Add(meta.Valid ? $"{meta.Width}x{meta.Height}" : "—");
                it.SubItems.Add(meta.HasAudio ? "有" : "无");
                it.SubItems.Add(QuotaReport.Mb(c.Bytes));
                it.SubItems.Add(c.Name);
                it.ImageIndex = -1;
                _list.Items.Add(it);
            }
        }
        finally { _list.EndUpdate(); }

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else ShowSelection();
        UpdateStatus();
    }

    private static string KindName(ClipKind k) => k switch
    {
        ClipKind.Kill => "击杀",
        ClipKind.Knocked => "被击倒",
        _ => "击倒",
    };

    /// <summary>后台把缩略图一张张塞进 ImageList，不阻塞 UI 也不阻塞录制。</summary>
    private async void LoadThumbs()
    {
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        // 先占位，保证 ImageIndex 与行号对应，后面直接换图
        var placeholder = new Bitmap(80, 45);
        using (var g = Graphics.FromImage(placeholder))
            g.Clear(Color.FromArgb(52, 52, 58));
        _thumbs.Images.Add(placeholder);

        var items = _list.Items.Cast<ListViewItem>().ToList();
        for (int i = 0; i < items.Count; i++)
        {
            items[i].ImageIndex = 0;
            items[i].ImageKey = "";
        }

        foreach (var it in items)
        {
            if (token.IsCancellationRequested || IsDisposed) return;
            var clip = (ClipInfo)it.Tag!;
            Bitmap? bmp = null;
            try { bmp = await Task.Run(() => ShellThumb.Get(clip.Path, 160, 90), token); }
            catch (OperationCanceledException) { return; }
            catch { }
            if (IsDisposed || token.IsCancellationRequested) { bmp?.Dispose(); return; }
            if (bmp is null) continue;

            // ImageList.Images 不能随便重排（索引就是行绑定），所以追加再改 ImageIndex
            int idx;
            _thumbs.Images.Add(bmp);
            idx = _thumbs.Images.Count - 1;
            it.ImageIndex = idx;
        }
    }

    private ClipInfo? Selected()
    {
        if (_list.SelectedItems.Count == 0) return null;
        return _list.SelectedItems[0].Tag as ClipInfo;
    }

    private void ShowSelection()
    {
        var c = Selected();
        if (c is null)
        {
            _preview.Image = null;
            _meta.Text = _shown.Count == 0 ? "这个筛选下没有片段。\r\n\r\n录一段之后回来按「刷新」。" : "选一条看看。";
            _note.Text = "";
            return;
        }

        var meta = Mp4Probe.Read(c.Path);
        _preview.Image?.Dispose();
        _preview.Image = ShellThumb.Get(c.Path, 720, 405);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(c.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"种类 {KindName(c.Kind)}    分数 {c.Score:F2}");
        sb.AppendLine($"时长 {Mp4Probe.Fmt(meta.Seconds)}    分辨率 {(meta.Valid ? $"{meta.Width}x{meta.Height}" : "—")}");
        sb.AppendLine($"音轨 {(meta.HasAudio ? "有" : "无")}    编码 {meta.Codec}");
        sb.AppendLine($"体积 {QuotaReport.Mb(c.Bytes)}");
        _meta.Text = sb.ToString();

        _note.Text = "双击或按 Enter 用系统播放器打开\r\nDelete 删除";
    }

    private void UpdateStatus()
    {
        long total = _all.Sum(c => c.Bytes);
        var shownTotal = _shown.Sum(c => c.Bytes);
        var q = _policy.QuotaGb <= 0 ? "不限" : $"{_policy.QuotaGb}GB";
        var r = _policy.RetainDays <= 0 ? "永久" : $"{_policy.RetainDays} 天";
        var free = QuotaReport.Mb(QuotaEnforcer.FreeBytes(_lib.Dir));
        _status.Text = $"{_shown.Count}/{_all.Count} 片（{QuotaReport.Mb(shownTotal)} / 合计 {QuotaReport.Mb(total)}）   " +
                       $"配额 {q}，保留 {r}   磁盘可用 {free}";
    }

    private void OpenSelected()
    {
        var c = Selected();
        if (c is null) return;
        try { Process.Start(new ProcessStartInfo(c.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打不开：" + ex.Message, "ApexHighlights"); }
    }

    private void RevealSelected()
    {
        var c = Selected();
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
        if (disposing)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _preview.Image?.Dispose();
            _thumbs.Dispose();
        }
        base.Dispose(disposing);
    }
}
