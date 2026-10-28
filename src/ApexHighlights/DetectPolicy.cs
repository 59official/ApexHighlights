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

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToJsonString(Json));
            return true;
        }
        catch { return false; }
    }

    /// <summary>把用户输入夹到有意义的范围：0（按模板）或 0.30–0.95；合并窗口 0–60 秒。</summary>
    public static DetectPolicy Clamp(DetectPolicy p)
    {
        if (p.MergeSec < 0) p.MergeSec = 0;
        if (p.MergeSec > 60) p.MergeSec = 60;
        if (p.Threshold <= 0) { p.Threshold = 0; return p; }
        if (p.Threshold < 0.30f) p.Threshold = 0.30f;
        if (p.Threshold > 0.95f) p.Threshold = 0.95f;
        return p;
    }

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
