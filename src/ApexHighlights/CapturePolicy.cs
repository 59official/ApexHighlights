using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApexHighlights;

/// <summary>
/// 录制参数（用户可自调）：画质码率 + 音轨开关 + 逐种类开关 + 成片存哪。
/// 存在 config.json 的顶层 "BitrateMbps" / "Audio" / "SkipKinds" / "ClipsDir" 字段里，
/// 跟配额、阈值共用一个文件。
///
/// 为什么又是独立一个类：DetectPolicy 管「认不认这张横幅」，QuotaPolicy 管「删旧的」，
/// 这里管「录成什么样、录到哪」。三者的失效方式完全不同（码率填错只是糊或占盘，阈值写坏会漏录，
/// 配额写坏会乱删），所以各自 Load/Save，谁也冲不掉谁。
/// </summary>
public sealed class CapturePolicy
{
    /// <summary>
    /// 视频码率，单位 Mbps。默认 20（v1 的老默认值，改了会让老用户的库悄悄变大，所以不动它）。
    ///
    /// 2026-10-28 用户反馈「20 有点糊」：1080p60 的 Apex 快速转视角时 20Mbps 确实不够，
    /// H.264 在运动块多的时候会把树/草糊成一片。30–40 是这台机器实测的清晰度拐点，
    /// 所以菜单里把 30/40 标成推荐，但默认值保持不变。
    /// </summary>
    public int BitrateMbps { get; set; } = 20;

    /// <summary>录不录音轨（系统回环 = 游戏声，不碰麦克风）。关掉后片段是无音轨的 mp4。</summary>
    public bool Audio { get; set; } = true;

    /// <summary>
    /// 不在录的种类（托盘里关掉的开关）。存 enum 名字，config.json 里能直接读懂；
    /// 缺字段 = 全录，所以老配置升上来行为不变。
    /// </summary>
    public List<ClipKind> SkipKinds { get; set; } = new();

    /// <summary>
    /// 成片放哪个文件夹（绝对路径）。空 = 默认位置（数据目录\captures\clips），
    /// 老配置没有这个字段就是空，行为不变。
    ///
    /// 用户 2026-10-29 提的：想自己挑盘点——1440p60 一小时 7GB 上下，系统盘很容易被塞满。
    /// 只有成片跟着走，环形分片是临时文件，始终留在数据目录里。
    /// </summary>
    public string ClipsDir { get; set; } = "";

    /// <summary>实际生效的成片目录（配上 <see cref="Paths.SetClipsRoot"/> 用）。</summary>
    [JsonIgnore]
    public string EffectiveClipsDir => ClipsDir.Length == 0 ? Paths.DefaultClips : ClipsDir;

    /// <summary>这个种类要不要录。托盘开关和 Capture 的闸门共用这一条判断。</summary>
    public bool Records(ClipKind k) => !SkipKinds.Contains(k);

    /// <summary>开/关一个种类的录制，返回改完之后「还在录」的状态。</summary>
    public bool SetRecords(ClipKind k, bool on)
    {
        SkipKinds.Remove(k);
        if (!on) SkipKinds.Add(k);
        return on;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 种类写成 "ReKnock" 这种名字而不是 3 —— 用户手改 config.json 时看得懂
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>换算成 Capture/EncoderChain 用的 bps。</summary>
    public int BitrateBps => BitrateMbps * 1_000_000;

    /// <summary>从 config.json 读。文件不存在、写坏了、字段缺失，统统退回默认值，绝不因此启动失败。</summary>
    public static CapturePolicy Load(string? path)
    {
        var p = new CapturePolicy();
        try
        {
            if (path is not null && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<CapturePolicy>(File.ReadAllText(path), Json);
                if (loaded is not null) p = loaded;
            }
        }
        catch { /* 配置坏了就用默认值 */ }
        return Clamp(p);
    }

    /// <summary>
    /// 写回 config.json：只改自己这两个字段，阈值/配额等原样保留。
    /// 跟 DetectPolicy.Save 一个写法 —— 用 JsonObject 局部改，而不是重新序列化整份配置。
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

            root["BitrateMbps"] = BitrateMbps;
            root["Audio"] = Audio;
            var skips = new JsonArray();
            foreach (var k in SkipKinds) skips.Add(k.ToString());
            root["SkipKinds"] = skips;
            root["ClipsDir"] = ClipsDir;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToJsonString(Json));
            return true;
        }
        catch { return false; }
    }

    /// <summary>夹到有意义的范围：5–200 Mbps（再低没法看，再高硬盘撑不住）。</summary>
    public static CapturePolicy Clamp(CapturePolicy p)
    {
        if (p.BitrateMbps < 5) p.BitrateMbps = 5;
        if (p.BitrateMbps > 200) p.BitrateMbps = 200;
        // 去重，并丢掉本版本已经没有的种类（老配置里可能留着 Kill/Knocked）
        p.SkipKinds = p.SkipKinds?.Where(k => Array.IndexOf(ClipKinds.All, k) >= 0).Distinct().ToList()
                      ?? new List<ClipKind>();
        // 路径写坏了就当没设过（回到默认目录）—— 不能让它把录制带崩
        p.ClipsDir = Paths.NormalizeDir(p.ClipsDir);
        return p;
    }

    /// <summary>菜单上给的档位。10–100 覆盖「省空间」到「几乎无损」，中间 20→30→40 是主战场。</summary>
    public static readonly int[] BitratePresets = { 10, 15, 20, 30, 40, 60, 80, 100 };

    /// <summary>每档一句人话：让用户知道多点一档要付出多少硬盘。</summary>
    public static string HintForBitrate(int mbps) => mbps switch
    {
        <= 15 => "省空间，1080p60 转视角会明显糊",
        <= 20 => "默认：以前一直用的档，快速转视角有点糊",
        <= 30 => "推荐：清晰度和体积的平衡点",
        <= 40 => "推荐：细节明显更清楚，体积约 1.5 倍",
        <= 60 => "很清晰，一小时约 20GB",
        _ => "近乎无损，硬盘吃得快",
    };
}
