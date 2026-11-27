using System.Drawing.Drawing2D;
using System.Globalization;

namespace ApexHighlights;

internal sealed class PictureForm : Form
{
    private const int FormWidth = 560, FormHeight = 378, Pad = 24, TitleHeight = 56;
    private readonly PillSelect _source = new() { AccessibleName = "画面参考来源" };
    private readonly CueTextBox _width = new() { AccessibleName = "参考宽度", MaxLength = 5 };
    private readonly CueTextBox _height = new() { AccessibleName = "参考高度", MaxLength = 5 };
    private readonly PillSelect _saveNear = new() { AccessibleName = "近失诊断图" };
    private PictureSettings? _result;

    public static PictureSettings? Edit(PictureSettings settings, PictureLayout? active, IWin32Window? owner = null)
    {
        using var form = new PictureForm(settings.Normalize(), active);
        form.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        var result = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        return result == DialogResult.OK ? form._result : null;
    }

    private PictureForm(PictureSettings settings, PictureLayout? active)
    {
        Text = "画面配置";
        Icon = AppIcon.Shared;
        Font = UiKit.F95;
        BackColor = UiKit.Bg;
        ForeColor = UiKit.Text;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        DoubleBuffered = true;

        var saved = ApexVideoConfig.Read(ApexVideoConfig.DefaultPath());
        var status = new SurfaceCard();
        var capture = MakeLabel(active is { } layout
            ? $"采集窗口 {layout.Capture}    画面参考 {layout.Game}"
            : "等待游戏窗口", UiKit.Text);
        var details = MakeLabel(active is { } picture
            ? $"检测区 {picture.Band.W}×{picture.Band.H} @ {picture.Band.X},{picture.Band.Y}"
            : "录像尺寸跟随 Windows 采集窗口", UiKit.Dim);
        var config = MakeLabel(saved is { } value
            ? $"Apex 保存值 {value.Size}（{value.WidthKey} / {value.HeightKey}）"
            : "未读到 Apex 保存值，自动模式将使用采集窗口尺寸", UiKit.Dim);
        status.Controls.AddRange([capture, details, config]);

        var sourceLabel = MakeLabel("画面参考", UiKit.Text);
        var sizeLabel = MakeLabel("手动宽高", UiKit.Text);
        var multiply = MakeLabel("×", UiKit.Dim);
        multiply.TextAlign = ContentAlignment.MiddleCenter;
        var nearLabel = MakeLabel("近失诊断图", UiKit.Text);
        var hint = MakeLabel("仅保留 OCR 确认的横幅，保存 7 天。", UiKit.Dim);
        var validation = MakeLabel("", UiKit.Danger);
        var widthPill = InputPill(_width, settings.Width);
        var heightPill = InputPill(_height, settings.Height);

        _source.Items.AddRange(["自动读取 Apex 配置", "使用采集窗口", "手动指定"]);
        _source.SelectedIndex = (int)settings.Source;
        _saveNear.Items.AddRange(["关闭", "保留"]);
        _saveNear.SelectedIndex = settings.SaveNearFrames ? 1 : 0;
        _source.SelectedIndexChanged += (_, _) => UpdateManualInputs();
        UpdateManualInputs();

        var close = new RoundButton("×", 36) { AccessibleName = "关闭", DialogResult = DialogResult.Cancel, TabStop = false };
        var cancel = new RoundButton("取消") { DialogResult = DialogResult.Cancel };
        var save = new RoundButton("保存") { Style = RoundButton.Look.Primary };
        save.Click += (_, _) => Save();
        CancelButton = cancel;
        AcceptButton = save;
        Controls.AddRange([status, sourceLabel, _source, sizeLabel, widthPill, multiply, heightPill,
            nearLabel, _saveNear, hint, validation, close, cancel, save]);

        Arrange();
        Load += (_, _) => Arrange();
        DpiChanged += (_, _) => Arrange();

        void UpdateManualInputs()
        {
            bool manual = _source.SelectedIndex == (int)PictureSource.Manual;
            widthPill.Enabled = heightPill.Enabled = manual;
            validation.Text = "";
        }

        void Save()
        {
            bool validWidth = TrySize(_width.Text, PictureSize.MinWidth, out int width);
            bool validHeight = TrySize(_height.Text, PictureSize.MinHeight, out int height);
            if (_source.SelectedIndex == (int)PictureSource.Manual && (!validWidth || !validHeight))
            {
                validation.Text = $"请输入有效宽高，宽 {PictureSize.MinWidth}–{PictureSize.MaxSize}，高 {PictureSize.MinHeight}–{PictureSize.MaxSize}。";
                var invalid = validWidth ? _height : _width;
                invalid.Focus();
                invalid.SelectAll();
                return;
            }

            _result = settings with
            {
                Source = (PictureSource)_source.SelectedIndex,
                Width = validWidth ? width : settings.Width,
                Height = validHeight ? height : settings.Height,
                SaveNearFrames = _saveNear.SelectedIndex == 1,
            };
            DialogResult = DialogResult.OK;
        }

        void Arrange()
        {
            SuspendLayout();
            ClientSize = new Size(Px(FormWidth), Px(FormHeight));
            using var outline = UiKit.RoundRect(ClientRectangle, Px(14));
            Region = new Region(outline);
            Place(status, Pad, TitleHeight, FormWidth - 2 * Pad, 81);
            Place(capture, 14, 8, 480, 20);
            Place(details, 14, 30, 480, 20);
            Place(config, 14, 52, 480, 20);
            Place(sourceLabel, Pad, 153, 110, 36);
            Place(_source, 146, 153, 270, 36);
            Place(sizeLabel, Pad, 198, 110, 36);
            Place(widthPill, 146, 198, 122, 36);
            Place(multiply, 268, 198, 26, 36);
            Place(heightPill, 294, 198, 122, 36);
            Place(nearLabel, Pad, 243, 110, 36);
            Place(_saveNear, 146, 243, 132, 36);
            Place(hint, 292, 243, 244, 36);
            Place(validation, Pad, 288, FormWidth - 2 * Pad, 24);
            Place(close, FormWidth - Pad - 36, 12, 36, 32);
            Place(cancel, FormWidth - Pad - 196, 322, 92, 38);
            Place(save, FormWidth - Pad - 92, 322, 92, 38);
            ResumeLayout();
            Invalidate();
        }
    }

    private int Px(int value) => UiKit.Px(this, value);

    private void Place(Control control, int x, int y, int width, int height) =>
        control.SetBounds(Px(x), Px(y), Px(width), Px(height));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = UiKit.RoundRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Px(14));
        using var pen = new Pen(UiKit.Line);
        g.DrawPath(pen, border);
        TextRenderer.DrawText(g, Text, UiKit.F11B, new Rectangle(Px(Pad), 0, Px(360), Px(TitleHeight)), UiKit.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WmNcHitTest = 0x84, HtClient = 1, HtCaption = 2;
        if (m.Msg != WmNcHitTest || m.Result != (IntPtr)HtClient) return;
        long screen = m.LParam.ToInt64();
        var point = PointToClient(new Point(unchecked((short)screen), unchecked((short)(screen >> 16))));
        if (point.Y >= 0 && point.Y < Px(TitleHeight)) m.Result = (IntPtr)HtCaption;
    }

    private static Label MakeLabel(string text, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
    };

    private static Pill InputPill(CueTextBox input, int number)
    {
        var pill = new Pill { Fill = UiKit.PillFill };
        input.Text = number.ToString(CultureInfo.InvariantCulture);
        input.TextAlign = HorizontalAlignment.Center;
        pill.Controls.Add(input);
        pill.Resize += (_, _) => PlaceInput();
        pill.HandleCreated += (_, _) => PlaceInput();
        return pill;

        void PlaceInput()
        {
            int inset = UiKit.Px(pill, 12);
            input.SetBounds(inset, (pill.Height - input.PreferredHeight) / 2,
                Math.Max(1, pill.Width - 2 * inset), input.PreferredHeight);
        }
    }

    private static bool TrySize(string text, int minimum, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value >= minimum && value <= PictureSize.MaxSize;
}
