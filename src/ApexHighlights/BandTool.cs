using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace ApexHighlights;

/// <summary>
/// 条带标定器：在一张截图里拖一个框，直接告诉你 <c>games/&lt;id&gt;.json</c> 的 <c>band</c> 该写什么，
/// 顺手把框里那一截存成模板 PNG。
///
/// 为什么要这个：<c>band</c> 是「基准分辨率下的像素坐标」，而基准分辨率可以选成截图的原始尺寸。
/// 以前自定义游戏只能拿量尺对着截图数像素，数错一个数字就是「怎么都识别不到」。
///
/// 用法：<c>ApexHighlights.exe --band-tool=shot.png [--game=apex]</c>
/// </summary>
internal static class BandTool
{
    public static int Run(string imagePath, string? gameId, string? outJson)
    {
        if (!File.Exists(imagePath))
        {
            Console.WriteLine($"[X] 找不到图片：{imagePath}");
            return 2;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new BandForm(imagePath, gameId ?? "apex", outJson);
        Application.Run(form);
        return 0;
    }

    /// <summary>自绘画布。Control.DoubleBuffered 是 protected，只能自己派生一个。</summary>
    private sealed class Canvas : Panel
    {
        public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    private sealed class BandForm : Form
    {
        private readonly string _imagePath;
        private readonly string _gameId;
        private readonly string? _outJson;
        private readonly Bitmap _image;

        private readonly Canvas _canvas = new() { Dock = DockStyle.Fill };
        private readonly Panel _side = new() { Dock = DockStyle.Right, Width = 320 };
        private readonly Label _info = new();
        private readonly Panel _preview = new();
        private readonly TextBox _json = new();

        private Rectangle _sel;          // 图像坐标系里的选区
        private Point _dragFrom;
        private bool _dragging;
        private float _scale = 1f;       // 图像 → 控件
        private PointF _origin;          // 图像左上角在控件里的位置

        public BandForm(string imagePath, string gameId, string? outJson)
        {
            _imagePath = imagePath;
            _gameId = gameId;
            _outJson = outJson;
            using (var fs = File.OpenRead(imagePath)) _image = new Bitmap(fs);

            Text = $"条带标定 · {Path.GetFileName(imagePath)}（{_image.Width}×{_image.Height}）";
            ClientSize = new Size(1180, 760);
            MinimumSize = new Size(900, 600);
            BackColor = UiKit.Bg;
            ForeColor = UiKit.Text;
            Font = UiKit.F9;
            StartPosition = FormStartPosition.CenterScreen;
            HandleCreated += (_, _) => UiKit.ApplyWindowChrome(this);

            _canvas.BackColor = UiKit.Sunken;
            _canvas.Paint += CanvasPaint;
            _canvas.MouseDown += CanvasDown;
            _canvas.MouseMove += CanvasMove;
            _canvas.MouseUp += CanvasUp;

            BuildSide();
            Controls.Add(_canvas);
            Controls.Add(_side);

            // 默认给一个「屏幕下半部中间」的框当起点，比空手拖快得多
            int w0 = Math.Max(40, _image.Width / 3), h0 = Math.Max(24, _image.Height / 12);
            _sel = new Rectangle((_image.Width - w0) / 2, _image.Height * 7 / 10 - h0 / 2, w0, h0);
            UpdateJson();
        }

        private void BuildSide()
        {
            _side.BackColor = UiKit.Surface;
            _side.Padding = new Padding(16);

            var title = new Label
            {
                Text = "怎么用",
                Font = UiKit.F10,
                ForeColor = UiKit.Text,
                AutoSize = true,
                Location = new Point(16, 12),
            };
            var help = new Label
            {
                Text = "在左边截图上按住左键拖一个框，\n框住横幅里那截「击倒/擊倒」文字\n（不要框太宽，带上名字和数字\n会让模板变脏）。\n\n拖完就有 band 和模板了。",
                AutoSize = true,
                ForeColor = UiKit.Dim,
                Location = new Point(16, 40),
            };

            _info.AutoSize = true;
            _info.ForeColor = UiKit.Accent;
            _info.Location = new Point(16, 148);

            _preview.Location = new Point(16, 178);
            _preview.Size = new Size(288, 120);
            _preview.BackColor = UiKit.Sunken;
            _preview.Paint += (_, e) => DrawPreview(e.Graphics);

            _json.Multiline = true;
            _json.ScrollBars = ScrollBars.Vertical;
            _json.Font = new Font("Consolas", 9f);
            _json.BackColor = UiKit.Sunken;
            _json.ForeColor = UiKit.Text;
            _json.BorderStyle = BorderStyle.FixedSingle;
            _json.Location = new Point(16, 312);
            _json.Size = new Size(288, 200);

            var copy = new RoundButton("复制 JSON", 288);
            copy.Location = new Point(16, 524);
            copy.Click += (_, _) =>
            {
                if (_json.Text.Length == 0) return;
                try { Clipboard.SetText(_json.Text); } catch { }
            };

            var saveTpl = new RoundButton("存成模板 PNG", 288);
            saveTpl.Location = new Point(16, 572);
            saveTpl.Click += (_, _) => SaveTemplate();

            var saveJson = new RoundButton("写进 games/<id>.json", 288) { Style = RoundButton.Look.Primary };
            saveJson.Location = new Point(16, 620);
            saveJson.Click += (_, _) => SaveJson();

            _side.Controls.AddRange(new Control[] { title, help, _info, _preview, _json, copy, saveTpl, saveJson });
        }

        // ---- 画布 ----

        private void CanvasPaint(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(UiKit.Sunken);
            if (_canvas.Width <= 0 || _canvas.Height <= 0) return;

            _scale = Math.Min((float)_canvas.Width / _image.Width, (float)_canvas.Height / _image.Height);
            float dw = _image.Width * _scale, dh = _image.Height * _scale;
            _origin = new PointF((_canvas.Width - dw) / 2, (_canvas.Height - dh) / 2);

            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_image, new RectangleF(_origin.X, _origin.Y, dw, dh));

            // 选区之外压一层半透明黑，让框内更亮
            var r = ToControl(_sel);
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                var full = new RectangleF(_origin.X, _origin.Y, dw, dh);
                var clip = new Region(full);
                clip.Exclude(r);
                g.FillRegion(dim, clip);
            }
            using var pen = new Pen(UiKit.Accent, 2f);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        private Rectangle ToControl(Rectangle imgRect) => new(
            (int)Math.Round(_origin.X + imgRect.X * _scale),
            (int)Math.Round(_origin.Y + imgRect.Y * _scale),
            Math.Max(1, (int)Math.Round(imgRect.Width * _scale)),
            Math.Max(1, (int)Math.Round(imgRect.Height * _scale)));

        private Point ToImage(Point ctl) => new(
            (int)Math.Round((ctl.X - _origin.X) / _scale),
            (int)Math.Round((ctl.Y - _origin.Y) / _scale));

        private void CanvasDown(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragFrom = Clamp(ToImage(e.Location));
        }

        private void CanvasMove(object? s, MouseEventArgs e)
        {
            if (!_dragging) return;
            var now = Clamp(ToImage(e.Location));
            _sel = Rectangle.FromLTRB(
                Math.Min(_dragFrom.X, now.X), Math.Min(_dragFrom.Y, now.Y),
                Math.Max(_dragFrom.X, now.X), Math.Max(_dragFrom.Y, now.Y));
            UpdateJson();
            _canvas.Invalidate();
            _preview.Invalidate();
        }

        private void CanvasUp(object? s, MouseEventArgs e)
        {
            _dragging = false;
            if (_sel.Width < 8 || _sel.Height < 8)
            {
                // 手抖点了一下：别把框缩没了
                _sel = Rectangle.FromLTRB(_sel.X, _sel.Y, _sel.X + Math.Max(8, _sel.Width), _sel.Y + Math.Max(8, _sel.Height));
            }
            UpdateJson();
            _canvas.Invalidate();
            _preview.Invalidate();
        }

        private Point Clamp(Point p) => new(
            Math.Clamp(p.X, 0, _image.Width), Math.Clamp(p.Y, 0, _image.Height));

        private void DrawPreview(Graphics g)
        {
            g.Clear(UiKit.Sunken);
            if (_sel.Width < 2 || _sel.Height < 2) return;
            var src = Rectangle.Intersect(_sel, new Rectangle(0, 0, _image.Width, _image.Height));
            if (src.Width < 2 || src.Height < 2) return;

            float k = Math.Min((float)(_preview.Width - 8) / src.Width, (float)(_preview.Height - 8) / src.Height);
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;    // 看清像素，方便判断有没有框脏
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_image, new Rectangle((_preview.Width - w) / 2, (_preview.Height - h) / 2, w, h), src, GraphicsUnit.Pixel);
        }

        // ---- 输出 ----

        private string BandJson()
        {
            var b = Rectangle.Intersect(_sel, new Rectangle(0, 0, _image.Width, _image.Height));
            var o = new JsonObject
            {
                ["refWidth"] = _image.Width,
                ["refHeight"] = _image.Height,
                ["band"] = new JsonObject { ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height },
            };
            return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        private void UpdateJson()
        {
            _json.Text = BandJson();
            _info.Text = _sel.Width < 2 ? "还没拖框" : $"选区 {_sel.X},{_sel.Y}  {_sel.Width}×{_sel.Height}";
            _preview.Invalidate();
        }

        private string TemplateDir => Path.Combine(Paths.DataRoot, "games", _gameId);
        private string TemplateName => Path.Combine(TemplateDir, "band.png");

        private void SaveTemplate()
        {
            var src = Rectangle.Intersect(_sel, new Rectangle(0, 0, _image.Width, _image.Height));
            if (src.Width < 8 || src.Height < 8) { MessageBox.Show(this, "先拖一个框。", "ApexHighlights"); return; }
            try
            {
                Directory.CreateDirectory(TemplateDir);
                using (var crop = _image.Clone(src, _image.PixelFormat)) crop.Save(TemplateName, System.Drawing.Imaging.ImageFormat.Png);
                var entry = new JsonObject
                {
                    ["id"] = _gameId + "-band",
                    ["label"] = _gameId,
                    ["file"] = $"{_gameId}/band.png",
                    ["threshold"] = 0.6,
                };
                Clipboard.SetText(entry.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show(this, $"模板已存到\n{TemplateName}\n\n模板条目（已复制到剪贴板，粘到 templates 数组里）：\n{entry.ToJsonString()}", "ApexHighlights");
            }
            catch (Exception ex) { MessageBox.Show(this, "存模板失败：" + ex.Message, "ApexHighlights"); }
        }

        private void SaveJson()
        {
            var path = _outJson ?? Path.Combine(Paths.DataRoot, "games", _gameId + ".json");
            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"没有 {path}\n先用下面的 JSON 手工建一个，或者加 --out=<路径> 指定。", "ApexHighlights");
                return;
            }
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                var b = Rectangle.Intersect(_sel, new Rectangle(0, 0, _image.Width, _image.Height));
                root["refWidth"] = _image.Width;
                root["refHeight"] = _image.Height;
                root["band"] = new JsonObject { ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height };
                File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show(this, $"已写入 {path}\n\n（重新开始录制才会生效）", "ApexHighlights");
            }
            catch (Exception ex) { MessageBox.Show(this, "写入失败：" + ex.Message, "ApexHighlights"); }
        }
    }
}
