using System.Drawing.Drawing2D;

namespace ApexHighlights;

/// <summary>
/// 「录制参数」面板：四根滑杆（比特率 / 击杀前 / 击杀后 / 合并窗口），拖完立刻生效。
///
/// 入口有两个（回顾库工具条、托盘右键），但面板只有这一份 —— 两处各自拼一套 UI 必然会
/// 在某次改动里走形。谁打开它，谁就传进当前值和「值变了」的回调。
/// </summary>
internal sealed class ParamsForm : Form
{
    private const int W = 520, H = 404, PadX = 24, TitleH = 72;

    private readonly ParamSlider _bitrate = new();
    private readonly ParamSlider _pre = new();
    private readonly ParamSlider _post = new();
    private readonly ParamSlider _merge = new();

    /// <summary>值变了（已经落盘、采集器也已经改过）。参数：码率 / 前摇秒 / 后摇秒 / 合并秒。</summary>
    public Action<int, int, int, int>? Changed;

    private readonly int _defBitrate, _defPre, _defPost, _defMerge;

    public ParamsForm(int bitrate, int preRoll, int postRoll, int merge,
                      int bitrateMin = CapturePolicy.BitrateMin, int bitrateMax = CapturePolicy.BitrateMax)
    {
        _defBitrate = 28;
        _defPre = 15;
        _defPost = 15;
        _defMerge = 8;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = UiKit.Bg;
        ClientSize = new Size(W, H);
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "录制参数";

        _bitrate.Label = "比特率（Mbps）";
        _bitrate.Min = bitrateMin;
        _bitrate.Max = bitrateMax;
        _bitrate.Step = 1;
        _bitrate.Value = bitrate;

        _pre.Label = "击杀前（回放多少秒）";
        _pre.Min = 0; _pre.Max = CapturePolicy.RollMax; _pre.Step = 1; _pre.Value = preRoll;
        _pre.Format = v => v == 0 ? "0s（事件发生前无录制）" : v + "s";

        _post.Label = "击杀后（继续录多少秒）";
        _post.Min = 0; _post.Max = CapturePolicy.RollMax; _post.Step = 1; _post.Value = postRoll;
        _post.Format = v => v == 0 ? "0s（事件后立即结束）" : v + "s";

        _merge.Label = "合并窗口（多久算同一场）";
        _merge.Min = 0; _merge.Max = CapturePolicy.RollMax; _merge.Step = 1; _merge.Value = merge;
        _merge.Format = v => v == 0 ? "0s（每次命中各出一段）" : v + "s";

        var sliders = new[] { _bitrate, _pre, _post, _merge };
        for (int i = 0; i < sliders.Length; i++)
        {
            var s = sliders[i];
            s.SetBounds(PadX, TitleH + 10 + i * 60, W - PadX * 2, 58);
            s.Committed += Fire;
            Controls.Add(s);
        }

        var reset = new RoundButton("恢复默认", 104) { Style = RoundButton.Look.Ghost };
        reset.Click += (_, _) =>
        {
            _bitrate.Value = _defBitrate;
            _pre.Value = _defPre;
            _post.Value = _defPost;
            _merge.Value = _defMerge;
            Fire();
        };
        reset.SetBounds(PadX, H - 56, 104, 38);
        Controls.Add(reset);

        var done = new RoundButton("完成", 96) { Style = RoundButton.Look.Primary };
        done.Click += (_, _) => Close();
        done.SetBounds(W - PadX - 96, H - 56, 96, 38);
        Controls.Add(done);

        // 无边框窗口的圆角：Region 会在四角留出真正的透明，不然会露出方形黑角
        using (var path = UiKit.RoundRect(new Rectangle(0, 0, W, H), 14))
            Region = new Region(path);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    // ── 自检用（--ui-test 走这几个口子，不去摸私有字段）──────────────────────
    internal (int Bitrate, int Pre, int Post, int Merge) Values =>
        (_bitrate.Value, _pre.Value, _post.Value, _merge.Value);

    /// <summary>等价于用户把四根滑杆各拖到某处：改值并按一次面板的回调。</summary>
    internal void TestSet(int bitrate, int pre, int post, int merge)
    {
        _bitrate.Value = bitrate;
        _pre.Value = pre;
        _post.Value = post;
        _merge.Value = merge;
        Fire();
    }

    /// <summary>拖完一根滑杆就往外报一次，立刻生效（用户说了实时生效，不要「保存」按钮）。</summary>
    private void Fire() => Changed?.Invoke(_bitrate.Value, _pre.Value, _post.Value, _merge.Value);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiKit.Bg);
        using (var pen = new Pen(UiKit.Line))
            g.DrawPath(pen, UiKit.RoundRect(new Rectangle(0, 0, W - 1, H - 1), 14));

        TextRenderer.DrawText(g, "录制参数", UiKit.F11B, new Rectangle(PadX, 14, 240, 26), UiKit.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "拖完立刻生效，不用保存", UiKit.F9,
            new Rectangle(PadX, 44, 320, 18), UiKit.Dim,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);

        // 右上角关闭：画一个 ✕，命中区域在 OnMouseDown 里判
        var close = new Rectangle(W - 44, 14, 28, 28);
        var hot = close.Contains(PointToClient(Cursor.Position));
        TextRenderer.DrawText(g, "✕", UiKit.F95, close,
            hot ? UiKit.Text : UiKit.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private Rectangle CloseHit => new(W - 44, 14, 28, 28);

    // 无边框窗口：标题区可拖动
    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x0201, HTCAPTION = 2;
        if (m.Msg == WM_LBUTTONDOWN)
        {
            int y = (short)((long)m.LParam >> 16);
            int x = (short)((long)m.LParam & 0xFFFF);
            if (y < TitleH && !CloseHit.Contains(x, y))
            {
                ReleaseCapture();
                SendMessage(Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, (IntPtr)HTCAPTION, IntPtr.Zero);
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (CloseHit.Contains(e.Location)) Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Invalidate(new Rectangle(W - 46, 12, 32, 32));      // ✕ 的 hover 高亮
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
