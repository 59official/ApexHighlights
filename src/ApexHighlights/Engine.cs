using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHighlights;

/// <summary>
/// 一个游戏的配置（games/&lt;id&gt;.json）。坐标全部以 <see cref="RefWidth"/>x<see cref="RefHeight"/> 为基准，
/// 运行时按实际窗口尺寸等比缩放，所以同一份配置能跑 1080p / 1440p / 4K。
/// </summary>
public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] WindowTitles { get; set; } = Array.Empty<string>();
    public int RefWidth { get; set; } = 2560;
    public int RefHeight { get; set; } = 1440;
    public BandDef Band { get; set; } = new();
    public float MinGapMs { get; set; } = 500;
    public int PreRollSec { get; set; } = 30;
    public int PostRollSec { get; set; } = 5;
    public List<BannerTemplate> Templates { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static GameProfile Load(string jsonPath)
    {
        var p = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(jsonPath), JsonOptions)
                ?? throw new InvalidDataException($"配置解析失败: {jsonPath}");
        if (p.Templates.Count == 0) throw new InvalidDataException("配置里没有 templates");
        if (p.Band.W <= 0 || p.Band.H <= 0) throw new InvalidDataException("band 尺寸无效");
        p.BaseDir = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
        return p;
    }

    /// <summary>JSON 所在目录，模板图片的相对路径基于它解析。</summary>
    [JsonIgnore] public string BaseDir { get; private set; } = ".";

    /// <summary>按 id 找配置：显式路径 → 数据目录 → 当前目录 → 从 exe 目录逐级向上找 games/&lt;id&gt;.json</summary>
    public static string ResolvePath(string idOrPath)
    {
        if (File.Exists(idOrPath)) return Path.GetFullPath(idOrPath);

        var name = idOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? idOrPath : idOrPath + ".json";
        var tried = new List<string>();

        // 数据目录优先：安装版把内置配置释放到这里，用户改过的也在这里
        var data = Path.Combine(Paths.Games, name);
        tried.Add(data);
        if (File.Exists(data)) return data;

        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "games", name);
        tried.Add(cwd);
        if (File.Exists(cwd)) return cwd;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "games", name);
            tried.Add(candidate);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"找不到游戏配置 {name}。试过: " + string.Join(" | ", tried));
    }
}

public sealed class BandDef
{
    /// <summary>条带左上角 X（基准分辨率坐标）。</summary>
    public int X { get; set; } = 640;
    public int Y { get; set; } = 1000;
    public int W { get; set; } = 1280;
    public int H { get; set; } = 120;
}

/// <summary>一个 banner 模板：一张只含“固定锚点字形”的灰度图 + 独立阈值。</summary>
public sealed class BannerTemplate
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string File { get; set; } = "";
    public float Threshold { get; set; } = 0.60f;

    [JsonIgnore] public int W { get; set; }
    [JsonIgnore] public int H { get; set; }
    [JsonIgnore] public float[] Hp { get; set; } = Array.Empty<float>();
    [JsonIgnore] public float Std { get; set; }
    [JsonIgnore] public float N => W * H;
}

/// <summary>一次打分的结果。</summary>
public readonly record struct Detection(string Label, float Score, int X, int Y, float Threshold);

/// <summary>
/// 通用 banner 检测器：高通滤波 + 归一化互相关（NCC），多模板取最高分。
///   hp(x) = x - boxblur25(x)          （25x25 均值，边缘复制填充）
///   ncc   = corr / (n * sd * std(tpl))，窗口 sd &lt;= 3 时记 0（近零方差守门）
/// 算法与 probe/ncc_test3.py 一致；实时化靠“粗扫 step4 → 局部细化 step1”。
/// </summary>
public sealed class BannerEngine
{
    private const int CoarseStep = 4;
    private const int RefineRadius = 5;

    private readonly List<BannerTemplate> _tpl = new();
    private readonly int _bw, _bh;

    public GameProfile Profile { get; }

    /// <summary>
    /// 用户自调的命中阈值，&gt;0 时覆盖每个模板自带的 threshold；0 表示按模板。
    /// 放在引擎上而不是改模板对象，是因为模板阈值来自 games/&lt;id&gt;.json（只读的内置配置，
    /// 发布版里从程序内部释放出来），用户改的偏好得写到数据目录的 config.json 里，
    /// 两者不能互相污染。运行时可随意改，下一次 Score 立刻生效。
    /// </summary>
    public float ThresholdOverride { get; set; }

    /// <summary>当前实际生效的阈值（给日志和界面显示用）。</summary>
    public float EffectiveThreshold => ThresholdOverride > 0 ? ThresholdOverride : Profile.Templates[0].Threshold;

    /// <summary>套用一份检测策略（启动时、或用户在界面上调完之后调用）。</summary>
    public void Apply(DetectPolicy policy) => ThresholdOverride = policy.Threshold;

    public BannerEngine(GameProfile profile)
    {
        Profile = profile;
        _bw = profile.Band.W;
        _bh = profile.Band.H;
        foreach (var t in profile.Templates)
        {
            var path = Path.IsPathRooted(t.File) ? t.File : Path.Combine(profile.BaseDir, t.File);
            if (!File.Exists(path)) throw new FileNotFoundException($"模板图不存在: {path}（模板 {t.Id}）");
            LoadTemplate(t, path);
            _tpl.Add(t);
        }
    }

    private static void LoadTemplate(BannerTemplate t, string path)
    {
        using var bmp = new Bitmap(path);
        t.W = bmp.Width; t.H = bmp.Height;
        var gray = new byte[t.W * t.H];
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var p = (byte*)data.Scan0;
                for (int y = 0; y < t.H; y++)
                {
                    var row = p + y * data.Stride;
                    for (int x = 0; x < t.W; x++)
                    {
                        var b = row[x * 4]; var g = row[x * 4 + 1]; var r = row[x * 4 + 2]; var a = row[x * 4 + 3];
                        // 透明像素按 0 处理，方便用带 alpha 的切图
                        int luma = a == 0 ? 0 : (b * 114 + g * 587 + r * 299 + 500) / 1000;
                        gray[y * t.W + x] = (byte)luma;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        t.Hp = HighPass(gray, t.W, t.H);
        double mean = 0;
        foreach (var v in t.Hp) mean += v;
        mean /= t.Hp.Length;
        double var = 0;
        for (int i = 0; i < t.Hp.Length; i++) { t.Hp[i] -= (float)mean; var += t.Hp[i] * t.Hp[i]; }
        t.Std = (float)Math.Sqrt(var / t.Hp.Length);
    }

    /// <summary>对一条 band（灰度、行优先 bw*bh）打分，返回最高分的模板、分数与左上角坐标。</summary>
    public Detection Score(ReadOnlySpan<byte> band)
    {
        if (band.Length != _bw * _bh)
            throw new ArgumentException($"band 必须是 {_bw}x{_bh}，实际 {band.Length}");

        var hp = HighPass(band, _bw, _bh);
        var (ii1, ii2) = Integral2(hp, _bw, _bh);
        int iw = _bw + 1;

        var first = Profile.Templates[0];
        var best = new Detection(first.Label, 0f, 0, 0, Th(first));
        foreach (var t in _tpl)
        {
            var (s, x, y) = ScoreOne(hp, ii1, ii2, iw, t);
            if (s > best.Score) best = new Detection(t.Label, s, x, y, Th(t));
        }
        return best;
    }

    /// <summary>某个模板实际生效的阈值：用户覆盖优先，否则用模板自带的。</summary>
    private float Th(BannerTemplate t) => ThresholdOverride > 0 ? ThresholdOverride : t.Threshold;

    private (float Score, int X, int Y) ScoreOne(float[] hp, double[] ii1, double[] ii2, int iw, BannerTemplate t)
    {
        int maxY = _bh - t.H, maxX = _bw - t.W;
        if (maxY <= 0 || maxX <= 0) return (0f, 0, 0);

        Span<(float score, int x, int y)> top = stackalloc (float, int, int)[3];
        top.Clear();
        for (int y = 0; y <= maxY; y += CoarseStep)
            for (int x = 0; x <= maxX; x += CoarseStep)
                InsertTop3(top, NccAt(hp, ii1, ii2, iw, t, x, y), x, y);

        float best = 0; int bx = 0, by = 0;
        foreach (var c in top)
        {
            if (c.score <= 0) continue;
            int y0 = Math.Max(0, c.y - RefineRadius), y1 = Math.Min(maxY, c.y + RefineRadius);
            int x0 = Math.Max(0, c.x - RefineRadius), x1 = Math.Min(maxX, c.x + RefineRadius);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float s = NccAt(hp, ii1, ii2, iw, t, x, y);
                    if (s > best) { best = s; bx = x; by = y; }
                }
        }
        return (best, bx, by);
    }

    private static void InsertTop3(Span<(float score, int x, int y)> top, float s, int x, int y)
    {
        if (s <= top[2].score) return;
        if (s > top[0].score) { top[2] = top[1]; top[1] = top[0]; top[0] = (s, x, y); }
        else if (s > top[1].score) { top[2] = top[1]; top[1] = (s, x, y); }
        else top[2] = (s, x, y);
    }

    private float NccAt(float[] hp, double[] ii1, double[] ii2, int iw, BannerTemplate t, int x, int y)
    {
        int y0 = y, y1 = y + t.H, x0 = x, x1 = x + t.W;
        double s1 = ii1[y1 * iw + x1] - ii1[y0 * iw + x1] - ii1[y1 * iw + x0] + ii1[y0 * iw + x0];
        double s2 = ii2[y1 * iw + x1] - ii2[y0 * iw + x1] - ii2[y1 * iw + x0] + ii2[y0 * iw + x0];
        double sd = Math.Sqrt(Math.Max(s2 - s1 * s1 / t.N, 0) / t.N);
        if (sd <= 3.0) return 0f;
        return Corr(hp, t, x, y) / (float)(t.N * sd * t.Std);
    }

    /// <summary>模板与 band 在 (x,y) 处的原始内积（模板已去均值，等效于减掉窗口均值后的相关）。</summary>
    private float Corr(float[] band, BannerTemplate t, int ox, int oy)
    {
        int vw = Vector<float>.Count;
        float acc = 0;
        for (int i = 0; i < t.H; i++)
        {
            var b = band.AsSpan((oy + i) * _bw + ox, t.W);
            var tt = t.Hp.AsSpan(i * t.W, t.W);
            int j = 0;
            var vacc = Vector<float>.Zero;
            for (; j <= t.W - vw; j += vw)
                vacc += new Vector<float>(b.Slice(j, vw)) * new Vector<float>(tt.Slice(j, vw));
            acc += Vector.Dot(vacc, Vector<float>.One);
            for (; j < t.W; j++) acc += b[j] * tt[j];
        }
        return acc;
    }

    /// <summary>高通：img - boxblur(k=25)。</summary>
    private static float[] HighPass(ReadOnlySpan<byte> src, int w, int h)
    {
        var blur = BoxBlur(src, w, h, 25);
        var res = new float[src.Length];
        for (int i = 0; i < src.Length; i++) res[i] = src[i] - blur[i];
        return res;
    }

    /// <summary>25x25 均值滤波，边缘复制填充；用积分图 O(1) 取窗口和。</summary>
    private static float[] BoxBlur(ReadOnlySpan<byte> src, int w, int h, int k)
    {
        int r = k / 2;
        int pw = w + 2 * r, ph = h + 2 * r;
        var ii = new double[(ph + 1) * (pw + 1)];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Clamp(y - r, 0, h - 1);
            double rowSum = 0;
            int cur = (y + 1) * (pw + 1), prev = y * (pw + 1);
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Clamp(x - r, 0, w - 1);
                rowSum += src[sy * w + sx];
                ii[cur + x + 1] = ii[prev + x + 1] + rowSum;
            }
        }
        var res = new float[w * h];
        double inv = 1.0 / (k * k);
        int st = pw + 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double s = ii[(y + k) * st + (x + k)] - ii[y * st + (x + k)]
                         - ii[(y + k) * st + x] + ii[y * st + x];
                res[y * w + x] = (float)(s * inv);
            }
        return res;
    }

    private static (double[] ii1, double[] ii2) Integral2(float[] a, int w, int h)
    {
        int iw = w + 1;
        var ii1 = new double[iw * (h + 1)];
        var ii2 = new double[iw * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double r1 = 0, r2 = 0;
            int cur = (y + 1) * iw, prev = y * iw;
            for (int x = 0; x < w; x++)
            {
                float v = a[y * w + x];
                r1 += v; r2 += (double)v * v;
                ii1[cur + x + 1] = ii1[prev + x + 1] + r1;
                ii2[cur + x + 1] = ii2[prev + x + 1] + r2;
            }
        }
        return (ii1, ii2);
    }
}
