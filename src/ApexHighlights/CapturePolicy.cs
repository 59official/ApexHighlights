using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApexHighlights;

/// <summary>
/// 音轨来源。四档互斥，托盘里是单选（用户 2026-10-29 定的）。
///
/// 分档的理由：整机回环抓的是「扬声器上的一切」，队友的 Discord 语音、后台的音乐全在里面；
/// 进程回环只抓游戏自己出声。这两种「游戏声」对用户来说是完全不同的东西，所以不能混成一档。
/// </summary>
public enum AudioMode
{
    /// <summary>不录声音（片段是无音轨 mp4，体积最小）。</summary>
    None = 0,

    /// <summary>
    /// 只录游戏进程（默认）。进程级回环，Discord / 浏览器 / 系统提示音进不来。
    /// 默认给这一档是因为录出来最"干净"——用户要的是集锦，不是桌面录音。
    /// </summary>
    GameOnly = 1,

    /// <summary>整机回环 + 麦克风（全都要）。</summary>
    SystemAndMic = 2,

    /// <summary>整机回环，不含麦克风。</summary>
    SystemOnly = 3,
}

/// <summary>
/// 录制反馈方式。三档互斥（用户 2026-10-29 定的：提示音和浮层会打架，只留一种）。
/// </summary>
public enum NoticeMode
{
    /// <summary>什么都不提示。</summary>
    None = 0,

    /// <summary>只响一声「叮」（落盘时）。</summary>
    Beep = 1,

    /// <summary>右上角滑出浮层（默认）：开始录一条提示、存好一条提示。</summary>
    Toast = 2,
}

/// <summary>
/// 录制参数（用户可自调）：画质码率 + 音轨来源 + 提示方式 + 逐种类开关 + 成片存哪。
/// 存在 config.json 的顶层 "BitrateMbps" / "AudioMode" / "Notice" / "SkipKinds" / "ClipsDir" 字段里，
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
    public int BitrateMbps { get; set; } = 28;

    /// <summary>
    /// 音轨来源，见 <see cref="AudioMode"/>。默认「只录游戏」。
    ///
    /// 老配置里是 Audio/Mic 两个布尔，Load 时会自动折算过来（见 <see cref="Migrate"/>），
    /// 所以升级不会让谁的音轨突然变了。
    /// </summary>
    // 属性叫 Audio（代码里读着顺），但 config.json 里写成 AudioMode —— 加上 JsonPropertyName 对齐，
    // 不然写盘写 AudioMode、读盘读 Audio，用户手改的那份配置永远读不回来。
    [JsonPropertyName("AudioMode")]
    public AudioMode Audio { get; set; } = AudioMode.GameOnly;

    /// <summary>录制反馈方式，见 <see cref="NoticeMode"/>。默认滑出浮层。</summary>
    public NoticeMode Notice { get; set; } = NoticeMode.Toast;

    /// <summary>要不要音轨：只有「不录声音」这一档没有。</summary>
    [JsonIgnore]
    public bool AnyAudio => Audio != AudioMode.None;

    /// <summary>
    /// 命中之前往回录多少秒（用户 2026-10-29 要的滑杆，默认 15）。
    /// 以前这个是写在游戏档案里的 30 秒常量，没人能动——每段 35 秒里有 30 秒是跑图画面。
    /// </summary>
    public int PreRollSec { get; set; } = 15;

    /// <summary>
    /// 命中之后继续录多少秒（默认 15）。它同时也决定「一段什么时候算结束」：
    /// 最后一次命中之后安静满这个时长才收尾。
    /// </summary>
    public int PostRollSec { get; set; } = 15;

    /// <summary>只录游戏进程时，托盘菜单里显示「已回退系统」——进程回环没起来过。</summary>
    [JsonIgnore]
    public bool GameFellBack { get; set; }

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

    private static readonly JsonDocumentOptions JsonDoc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
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
                var text = File.ReadAllText(path);
                var root = JsonNode.Parse(text, documentOptions: JsonDoc) as JsonObject ?? new JsonObject();

                // 老配置只有 Audio/Mic 两个布尔，没有 AudioMode。必须**反序列化之前**把它们换掉：
                // 换成 AudioMode 之前直接 Deserialize 会撞上「布尔值喂给枚举」→ JsonException →
                // 整份配置退回默认（用户的码率、阈值一起丢），所以先改写 JSON 再反序列化。
                if (root["AudioMode"] is null)
                {
                    root["AudioMode"] = Migrate(Bool(root["Audio"], true), Bool(root["Mic"], false)).ToString();
                    root.Remove("Audio");
                    root.Remove("Mic");
                }

                var loaded = JsonSerializer.Deserialize<CapturePolicy>(root.ToJsonString(), Json);
                if (loaded is not null) p = loaded;
            }
        }
        catch { /* 配置坏了就用默认值 */ }
        return Clamp(p);
    }

    private static bool Bool(JsonNode? n, bool fallback)
    {
        try { return n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback; }
        catch { return fallback; }
    }

    /// <summary>旧版两个布尔 → 四档。(false,true) 那一档已经删了，折成「整机 + 麦克风」保住录音意图。</summary>
    public static AudioMode Migrate(bool audio, bool mic) => (audio, mic) switch
    {
        (false, false) => AudioMode.None,
        (false, true) => AudioMode.SystemAndMic,
        (true, true) => AudioMode.SystemAndMic,
        _ => AudioMode.SystemOnly,
    };

    /// <summary>
    /// 写回 config.json：只改自己的字段，阈值/配额等原样保留。
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
                    : (JsonNode.Parse(text, documentOptions: JsonDoc) as JsonObject ?? new JsonObject());
            }
            else root = new JsonObject();

            root["BitrateMbps"] = BitrateMbps;
            root["PreRollSec"] = PreRollSec;
            root["PostRollSec"] = PostRollSec;
            root["AudioMode"] = Audio.ToString();
            root["Notice"] = Notice.ToString();
            // 老字段已经没人读了，代码里留着只会让手改配置的人以为它还有效
            root.Remove("Audio");
            root.Remove("Mic");
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

    /// <summary>夹到有意义的范围：5–200 Mbps（再低没法看，再高硬盘撑不住），枚举值不认识就回默认档。</summary>
    public static CapturePolicy Clamp(CapturePolicy p)
    {
        if (p.BitrateMbps < BitrateMin) p.BitrateMbps = BitrateMin;
        if (p.BitrateMbps > BitrateMax) p.BitrateMbps = BitrateMax;
        if (p.PreRollSec < 0) p.PreRollSec = 0;
        if (p.PreRollSec > RollMax) p.PreRollSec = RollMax;
        if (p.PostRollSec < 0) p.PostRollSec = 0;
        if (p.PostRollSec > RollMax) p.PostRollSec = RollMax;
        if (!Enum.IsDefined(p.Audio)) p.Audio = AudioMode.GameOnly;
        if (!Enum.IsDefined(p.Notice)) p.Notice = NoticeMode.Toast;
        // 去重，并丢掉本版本已经没有的种类（老配置里可能留着 Kill/Knocked）
        p.SkipKinds = p.SkipKinds?.Where(k => Array.IndexOf(ClipKinds.All, k) >= 0).Distinct().ToList()
                      ?? new List<ClipKind>();
        // 路径写坏了就当没设过（回到默认目录）—— 不能让它把录制带崩
        p.ClipsDir = Paths.NormalizeDir(p.ClipsDir);
        return p;
    }

    // 滑杆范围（面板和 Clamp 共用一套数，免得两处各写一半对不上）
    public const int BitrateMin = 4;
    public const int BitrateMax = 60;
    public const int RollMax = 30;

    /// <summary>音轨档位在菜单里的顺序（默认档排第一，用户从上往下看就是推荐顺序）。</summary>
    public static readonly AudioMode[] AudioModes =
    {
        AudioMode.GameOnly,
        AudioMode.SystemAndMic,
        AudioMode.SystemOnly,
        AudioMode.None,
    };

    public static readonly NoticeMode[] NoticeModes =
    {
        NoticeMode.Toast,
        NoticeMode.Beep,
        NoticeMode.None,
    };

    public static string AudioLabel(AudioMode m) => m switch
    {
        AudioMode.GameOnly => "只录游戏（推荐）",
        AudioMode.SystemAndMic => "系统声音 + 麦克风",
        AudioMode.SystemOnly => "系统声音（不含麦克风）",
        _ => "不录声音",
    };

    public static string AudioHint(AudioMode m) => m switch
    {
        AudioMode.GameOnly => "只抓 Apex 自己的声音；Discord、浏览器音乐不会进来",
        AudioMode.SystemAndMic => "声卡上的一切 + 你的麦克风",
        AudioMode.SystemOnly => "声卡上的一切，包括队友语音；不录麦克风",
        _ => "片段没有音轨，体积最小",
    };

    public static string NoticeLabel(NoticeMode n) => n switch
    {
        NoticeMode.Toast => "滑出提示（推荐）",
        NoticeMode.Beep => "只响一声",
        _ => "不提示",
    };

    public static string NoticeHint(NoticeMode n) => n switch
    {
        NoticeMode.Toast => "右上角滑出一个小窗，开始录和存好各一次",
        NoticeMode.Beep => "存好时「叮」一声，不动画面",
        _ => "完全静默",
    };
}
