using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexHighlights;

/// <summary>
/// 检测灵敏度（用户可自调）。存在 config.json 的顶层 "Threshold" 字段里，
/// 和配额那些字段共用一个文件 —— 用户只需要记住一个配置文件。
///
/// 为什么单独一个类而不是塞进 QuotaPolicy：配额是「删旧的」策略，阈值是「认不认这张横幅」，
/// 两者失效原因完全不同（配额坏了最多乱删，阈值坏了会漏录或满库乱录），
/// 所以加载/保存各自独立，任何一边写坏都不影响另一边。
/// </summary>
public sealed class DetectPolicy
{
    /// <summary>
    /// 命中阈值：NCC 分数 &gt;= 这个值才算一次击倒。
    /// 0 表示「按 games/&lt;id&gt;.json 里每个模板自带的 threshold」。
    ///
    /// 默认 0.70 的来历（2026-10-28 一局真机 8 次命中的复盘）：
    /// 6 次真击倒落在 0.69–0.98，2 次误判落在 0.626 / 0.630（同一处画面 1 秒内重复触发，
    /// 两块补丁相关系数 0.979）。0.65 刚好能挡住误判，但 0.694 这个真命中离它太近，所以取 0.70。
    /// </summary>
    public float Threshold { get; set; } = 0.70f;

    /// <summary>
    /// 合并窗口（秒）：相邻两次命中间隔 ≤ 这个值，视为同一场交火，切成一段。
    /// 0 = 不合并，每次命中各出一段。
    ///
    /// 默认 8 的来历：Apex 横幅在屏幕上停留两秒多，期间每 0.5s 就会再触发一次 NCC，
    /// 于是一次击倒会连出 3 段视频（实测 19:43:06 / :07 / :08 三条，各 80MB）。
    /// 8 秒是 spec §4 早就定下的值 —— 一场交火里连击倒两个也只该给一段。
    /// </summary>
    public int MergeSec { get; set; } = 8;

    /// <summary>
    /// 游戏内 HUD 缩放的百分比（0 = 自动）。用户 2026-10-29 提的：与其让程序猜，
    /// 不如让他直接选自己的 HUD 有多大。
    ///
    /// 为什么要它：NCC 对尺度几乎没有容差 —— 实测把条带按 0.9/1.1 缩放后，
    /// 分数从 0.995 掉到 0.40/0.46（阈值 0.6）。所以只要 HUD 缩放不是 100%，就得
    /// 用对应尺度的模板。用户选 0（自动）时，程序只在「像但不够像」的灰区才补扫其它尺度。
    /// </summary>
    public int HudScalePct { get; set; }

    /// <summary>给界面/日志用的可读描述。</summary>
    public string Describe =>
        Threshold <= 0 ? "按模板自带" : $"{Threshold:F2}";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从 config.json 读。文件不存在、写坏了、字段缺失，统统退回默认值，绝不因此启动失败。</summary>
    public static DetectPolicy Load(string? path)
    {
        var p = new DetectPolicy();
        try
        {
            if (path is not null && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<DetectPolicy>(File.ReadAllText(path), Json);
                if (loaded is not null) p = loaded;
            }
        }
        catch { /* 配置坏了就用默认值 */ }
        return Clamp(p);
    }

    /// <summary>
    /// 写回 config.json：只改 Threshold 这一个字段，其它字段（配额等）原样保留。
    /// 用 JsonObject 而不是重新序列化整个配置，就是为了不把别人的字段冲掉。
    /// </summary>
    public bool Save(string path)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                root = string.IsNullOrWhiteSpace(text)
                    ? new JsonObject()
                    : (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) as JsonObject ?? new JsonObject());
            }
            else root = new JsonObject();

            root["Threshold"] = Math.Round(Threshold, 2);
            root["MergeSec"] = MergeSec;
            root["HudScalePct"] = HudScalePct;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToJsonString(Json));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把用户输入夹到有意义的范围：0（按模板）或 0.30–0.95；合并窗口 0–<see cref="CapturePolicy.MergeMax"/> 秒
    /// （上限跟录制参数面板共用同一个常量，不然面板写 30、这里认 60，用户会看到「填了 45 却变成 30」）。
    /// </summary>
    public static DetectPolicy Clamp(DetectPolicy p)
    {
        if (p.MergeSec < 0) p.MergeSec = 0;
        if (p.MergeSec > CapturePolicy.MergeMax) p.MergeSec = CapturePolicy.MergeMax;
        if (p.Threshold <= 0) { p.Threshold = 0; return p; }
        if (p.Threshold < 0.30f) p.Threshold = 0.30f;
        if (p.Threshold > 0.95f) p.Threshold = 0.95f;
        // HUD 缩放只认面板里给的那几档，别的值一律当「自动」
        if (Array.IndexOf(HudScales, p.HudScalePct) < 0) p.HudScalePct = 0;
        return p;
    }

    /// <summary>HUD 缩放档位：0 = 自动（灰区补扫），其余是用户明确选的百分比。</summary>
    public static readonly int[] HudScales = { 0, 80, 90, 100, 110, 120 };

    public static string HudLabel(int pct) => pct == 0 ? "自动（推荐）" : $"{pct}%";

    public static string HudHint(int pct) => pct == 0
        ? "默认：平时只用 100% 模板，分数落在灰区时自动补扫 80–120%"
        : $"按 {pct}% 的 HUD 缩放识别（游戏内设置是多少就选多少，识别更快也更准）";

    /// <summary>界面上给用户选的档位。间隔 0.05，够细也不至于点半天。</summary>
    public static readonly float[] Presets = { 0.55f, 0.60f, 0.65f, 0.70f, 0.75f, 0.80f, 0.85f, 0.90f };

    /// <summary>每个档位配一句人话，免得用户不知道调高调低会怎样。</summary>
    public static string HintFor(float v) => v switch
    {
        <= 0.60f => "宽松：几乎不漏，但可能把别的画面当成击倒（会多录）",
        <= 0.70f => "推荐：默认档，实测能挡住重复触发的误判",
        <= 0.80f => "偏严：只认很清楚的横幅，闪太快/被遮挡时会漏",
        _ => "很严：几乎只认完整显示的横幅，漏录风险高",
    };

    /// <summary>
    /// 下拉框里用的短标签。胶囊控件只有两百来像素，HintFor 那几行长句放不下，
    /// 所以列表里只给四五个字的结论，长句留给提示条。
    /// </summary>
    public static string ShortHintFor(float v) => v switch
    {
        <= 0.60f => "宽松",
        <= 0.70f => "默认推荐",
        <= 0.80f => "偏严",
        _ => "很严",
    };
}
