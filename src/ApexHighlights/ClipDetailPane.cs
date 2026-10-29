using System.Globalization;

namespace ApexHighlights;

/// <summary>
/// 回顾库右侧的详情面板：一张大图 + 元数据 + 底部操作提示。
///
/// 对外只有一个状态入口：Bind(片段或 null)。加载缩略图、读 mp4 头、格式化数字
/// 都在里面做掉，外层不需要知道 ShellThumb / Mp4Probe / 单位换算这些东西。
/// </summary>
internal sealed class ClipDetailPane : Panel
{
    // 面板只有 336 宽，原来那句「双击或 Enter 用系统播放器打开 · Delete 删除」会折成两行被裁掉。
    // 这行是提示不是说明书，两行就该砍字数——「播放」「删除」两个动词留着就够用了。
    private const string Hint = "双击 / Enter 播放 · Delete 删除";

    private readonly SurfaceCard _card = new();
    private readonly PreviewBox _preview = new();
    private readonly MetaList _meta = new();
    private readonly Label _note = new();

    /// <summary>双击大图时抛出去，让外层去调播放器。</summary>
    public event EventHandler? OpenRequested;

    public ClipDetailPane()
    {
        BackColor = UiKit.Bg;

        _card.Dock = DockStyle.Fill;
        _card.Padding = new Padding(UiKit.Px(this, 12));

        _preview.Dock = DockStyle.Top;
        _preview.Height = UiKit.Px(this, 186);
        _preview.EmptyText = "选一条看看";
        _preview.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        // 键值表的两列也是设计像素：不缩放的话「分辨率」这种三字标题会被切掉
        _meta.RowHeight = UiKit.Px(this, 27);
        _meta.KeyWidth = UiKit.Px(this, 52);
        var metaPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, UiKit.Px(this, 10), 0, UiKit.Px(this, 6)), BackColor = UiKit.Surface };
        _meta.Dock = DockStyle.Fill;
        metaPad.Controls.Add(_meta);

        _note.Dock = DockStyle.Bottom;
        _note.Height = UiKit.Px(this, 40);
        _note.AutoEllipsis = true;      // 窗口再窄下去宁可省略号，也别折行糊成一片
        _note.ForeColor = UiKit.Dim;
        _note.Font = UiKit.F9;
        _note.TextAlign = ContentAlignment.MiddleLeft;

        _card.Controls.Add(metaPad);
        _card.Controls.Add(_note);
        _card.Controls.Add(_preview);
        Controls.Add(_card);

        // 卡片是 Dock=Fill，宽度最后落在它身上，所以听它的大小变化最准
        _card.Resize += (_, _) => FitPreview();
    }

    /// <summary>
    /// 绑定一条片段；null 表示没有选中。filterEmpty 用来区分「筛选后一条都没有」
    /// 和「还没选」——这两种情况该给不一样的提示。
    /// </summary>
    public void Bind(ClipInfo? clip, bool filterEmpty)
    {
        SetPreview(null);

        if (clip is null)
        {
            _meta.Set(("提示", filterEmpty ? "这个筛选下没有片段" : "在左边选一条"));
            _note.Text = filterEmpty ? "录一段之后回来按 F5 刷新" : Hint;
            return;
        }

        var meta = Mp4Probe.Read(clip.Path);
        SetPreview(ShellThumb.Get(clip.Path, 720, 405));

        _meta.Set(
            ("时间", clip.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss")),
            ("种类", ClipKinds.Label(clip.Kind)),
            ("分数", clip.Score.ToString("F3", CultureInfo.InvariantCulture)),
            ("时长", Mp4Probe.Fmt(meta.Seconds)),
            ("分辨率", meta.Valid ? $"{meta.Width} × {meta.Height}" : "—"),
            ("音轨", meta.HasAudio ? $"有 · {meta.Codec}" : "无"),
            ("体积", QuotaReport.Mb(clip.Bytes)));

        _note.Text = Hint;
    }

    private void SetPreview(Image? img)
    {
        var old = _preview.Image;
        _preview.Image = img;
        if (!ReferenceEquals(old, img)) old?.Dispose();
    }

    /// <summary>
    /// 预览按 16:9 跟着宽度走：面板收窄了还留 212 高，图上下就会空一条黑边，
    /// 白白把元数据往下推。缩略图本身是 16:9 取的，所以这个比例也是「不变形」的那个。
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int w = _card.ClientSize.Width - _card.Padding.Horizontal;
        if (w <= 0) return;
        int h = Math.Clamp((int)Math.Round(w * 9.0 / 16.0), 120, 240);
        if (_preview.Height != h) _preview.Height = h;
    }

    /// <summary>面板的「真高」不靠外部传：布局跑完就按当前宽度校正一次预览高度。</summary>
    public void FitPreview()
    {
        OnResize(EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SetPreview(null);
        base.Dispose(disposing);
    }
}
