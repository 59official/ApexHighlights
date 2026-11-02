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
    private const int W = 520, H = 452, PadX = 24, TitleH = 60;

    // 面板里的尺寸都是按 96 DPI 写的「设计像素」，实际窗口按当前 DPI 放大——
    // 不这么做的话，125% 缩放时字号自动变大、矩形还是老尺寸，副标题就会被切掉上半截
    // （用户真机截图：『拖完立刻生效，不用保存』只剩半行）。
    private float S => DeviceDpi / 96f;
    private int Si(int v) => (int)Math.Round(v * S);

    private readonly ParamSlider _bitrate = new();
    private readonly ParamSlider _pre = new();
    private readonly ParamSlider _post = new();
    private readonly ParamSlider _merge = new();

    /// <summary>拖动中值变了：只该拿去改采集器（立刻看到效果），别写盘。参数：码率 / 前摇 / 后摇 / 合并。</summary>
    public Action<int, int, int, int, VideoCodec>? Changed;

    /// <summary>松手了（或键盘调完、点了恢复默认）：这时候才写盘。</summary>
    public Action<int, int, int, int, VideoCodec>? Committed;

    private readonly int _defBitrate, _defPre, _defPost, _defMerge;

    private readonly CodecPicker _codec = new();

    public ParamsForm(int bitrate, int preRoll, int postRoll, int merge, VideoCodec codec = CapturePolicy.DefaultCodec,
                      int bitrateMin = CapturePolicy.BitrateMin, int bitrateMax = CapturePolicy.BitrateMax)
    {
        _defBitrate = 30;
        _defPre = 15;
        _defPost = 15;
        _defMerge = 8;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = UiKit.Bg;
        // 几何自己按 DPI 算（见 S/Si），就别让 WinForms 再缩一遍——Toast 那边同款写法
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Si(W), Si(H));
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
        _merge.Min = 0; _merge.Max = CapturePolicy.MergeMax; _merge.Step = 1; _merge.Value = merge;
        _merge.Format = v => v == 0 ? "0s（每次命中各出一段）" : v + "s";

        _codec.Label = "视频编码";
        _codec.Value = codec;

        var sliders = new[] { _bitrate, _pre, _post, _merge };
        for (int i = 0; i < sliders.Length; i++)
        {
            var s = sliders[i];
            s.SetBounds(Si(PadX), Si(TitleH + 10 + i * 60), Si(W - PadX * 2), Si(58));
            s.Dragged += Fire;          // 拖动中：实时改采集器
            s.Committed += Commit;      // 松手：落盘
            Controls.Add(s);
        }

        // 第五行不是滑杆：两个互斥的胶囊（H.264 / HEVC）
        _codec.SetBounds(Si(PadX), Si(TitleH + 10 + 4 * 60), Si(W - PadX * 2), Si(48));
        _codec.Changed += _ => Commit();     // 编码是离散选择，点了就落盘
        Controls.Add(_codec);

        var reset = new RoundButton("恢复默认", 104) { Style = RoundButton.Look.Ghost };
        reset.Click += (_, _) =>
        {
            _bitrate.Value = _defBitrate;
            _pre.Value = _defPre;
            _post.Value = _defPost;
            _merge.Value = _defMerge;
            _codec.Value = CapturePolicy.DefaultCodec;
            Fire();
            Commit();          // 恢复默认是明确动作，立刻落盘
        };
        reset.SetBounds(Si(PadX), Si(H - 56), Si(104), Si(38));
        Controls.Add(reset);

        var done = new RoundButton("完成", 96) { Style = RoundButton.Look.Primary };
        done.Click += (_, _) => Close();
        done.SetBounds(Si(W - PadX - 96), Si(H - 56), Si(96), Si(38));
        Controls.Add(done);

        // 无边框窗口的圆角：Region 会在四角留出真正的透明，不然会露出方形黑角
        using (var path = UiKit.RoundRect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Si(14)))
            Region = new Region(path);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    /// <summary>
    /// 两个互斥的胶囊（H.264 / HEVC）。和滑杆一样自绘，颜色跟 UiKit 一套。
    /// 单独的控件而不是在 ParamsForm 里画：命中测试和「谁亮着」的状态都关在自己身上。
    /// </summary>
    private sealed class CodecPicker : Control
    {
        public string Label = "";
        public event Action<VideoCodec>? Changed;

        private VideoCodec _value = CapturePolicy.DefaultCodec;
        private int _hover = -1;

        public CodecPicker()
        {
            DoubleBuffered = true;
            // 顺序有讲究：必须先开 SupportsTransparentBackColor，再设透明背色，
            // 反了会抛「Control does not support transparent background colors」。
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        private float S => DeviceDpi / 96f;
        private int Si(int v) => (int)Math.Round(v * S);

        /// <summary>改值但一声不响（自检里模拟「用户把状态摆成这样」，不该引发落盘）。</summary>
        public void SetValueSilently(VideoCodec v)
        {
            _value = v;
            Invalidate();
        }

        public VideoCodec Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                Invalidate();
                Changed?.Invoke(_value);
            }
        }

        private Rectangle Chip(int i)
        {
            int w = Si(96), h = Si(34), gap = Si(8);
            int total = w * 2 + gap;
            int x = Width - total;
            int y = (Height - h) / 2;
            return new Rectangle(x + i * (w + gap), y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var label = new Rectangle(0, 0, Width - Si(220), Height);
            TextRenderer.DrawText(g, Label, UiKit.F95, label, UiKit.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            for (int i = 0; i < VideoCodecs.All.Length; i++)
            {
                var c = VideoCodecs.All[i];
                var r = Chip(i);
                bool on = c == _value;
                using (var path = UiKit.RoundRect(r, Si(10)))
                {
                    using var fill = new SolidBrush(on ? UiKit.Accent
                        : (_hover == i ? Color.FromArgb(38, 41, 50) : UiKit.PillFill));
                    g.FillPath(fill, path);
                }
                TextRenderer.DrawText(g, VideoCodecs.Label(c), UiKit.F95, r,
                    on ? Color.FromArgb(24, 25, 30) : UiKit.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = -1;
            for (int i = 0; i < VideoCodecs.All.Length; i++) if (Chip(i).Contains(e.Location)) hit = i;
            if (hit != _hover) { _hover = hit; Invalidate(); }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1) { _hover = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < VideoCodecs.All.Length; i++)
            {
                if (!Chip(i).Contains(e.Location)) continue;
                Value = VideoCodecs.All[i];
                return;
            }
            base.OnMouseDown(e);
        }
    }

    // ── 自检用（--ui-test 走这几个口子，不去摸私有字段）──────────────────────
    internal (int Bitrate, int Pre, int Post, int Merge, VideoCodec Codec) Values =>
        (_bitrate.Value, _pre.Value, _post.Value, _merge.Value, _codec.Value);

    internal VideoCodec CodecValue => _codec.Value;

    /// <summary>等价于用户把四根滑杆各拖到某处：改值并按一次面板的回调。</summary>
    internal void TestSet(int bitrate, int pre, int post, int merge, VideoCodec codec = VideoCodec.H264)
    {
        _bitrate.Value = bitrate;
        _pre.Value = pre;
        _post.Value = post;
        _merge.Value = merge;
        _codec.SetValueSilently(codec);   // 静默改：点胶囊算「一次完成的动作」会顺带落盘，自检时不该触发
        Fire();
    }

    /// <summary>自检用：模拟「松手」这一刻。</summary>
    internal void TestCommit() => Commit();

    /// <summary>拖完一根滑杆就往外报一次，立刻生效（用户说了实时生效，不要「保存」按钮）。</summary>
    private void Fire() => Changed?.Invoke(_bitrate.Value, _pre.Value, _post.Value, _merge.Value, _codec.Value);

    private void Commit() => Committed?.Invoke(_bitrate.Value, _pre.Value, _post.Value, _merge.Value, _codec.Value);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiKit.Bg);
        using (var pen = new Pen(UiKit.Line))
            g.DrawPath(pen, UiKit.RoundRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Si(14)));

        // 标题与副标题：先量字高再画，别用写死的 26/18 —— DPI 一放大就切字
        const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int padX = Si(PadX);
        int textW = ClientSize.Width - padX * 2 - Si(44);
        int titleY = Si(12);
        int titleH = TextRenderer.MeasureText(g, "录制参数", UiKit.F11B, new Size(int.MaxValue, int.MaxValue), tf).Height;
        TextRenderer.DrawText(g, "录制参数", UiKit.F11B, new Rectangle(padX, titleY, textW, titleH), UiKit.Text, tf);

        int subY = titleY + titleH + Si(2);
        int subH = TextRenderer.MeasureText(g, "拖完立刻生效，不用保存", UiKit.F9, new Size(int.MaxValue, int.MaxValue), tf).Height;
        TextRenderer.DrawText(g, "拖完立刻生效，不用保存", UiKit.F9,
            new Rectangle(padX, subY, textW, subH), UiKit.Dim, tf);

        // 右上角关闭：画一个 ✕，命中区域在 OnMouseDown 里判
        var close = CloseHit;
        var hot = close.Contains(PointToClient(Cursor.Position));
        TextRenderer.DrawText(g, "✕", UiKit.F95, close,
            hot ? UiKit.Text : UiKit.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private Rectangle CloseHit => new(ClientSize.Width - Si(44), Si(12), Si(28), Si(28));

    // 无边框窗口：标题区可拖动
    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x0201, HTCAPTION = 2;
        if (m.Msg == WM_LBUTTONDOWN)
        {
            int y = (short)((long)m.LParam >> 16);
            int x = (short)((long)m.LParam & 0xFFFF);
            if (y < Si(TitleH) && !CloseHit.Contains(x, y))
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
        var r = CloseHit; r.Inflate(2, 2);
        Invalidate(r);                                     // ✕ 的 hover 高亮
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
