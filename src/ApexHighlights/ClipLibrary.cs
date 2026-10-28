using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHighlights;

/// <summary>片段种类。v1 只产出 Knock，另外两种是给淘汰优先级和未来 v2 留的位。</summary>
public enum ClipKind
{
    /// <summary>我击倒别人</summary>
    Knock = 0,
    /// <summary>我击杀别人（含击倒后补刀）</summary>
    Kill = 1,
    /// <summary>我被击倒</summary>
    Knocked = 2,
}

/// <summary>回顾库里的一条片段。文件名是唯一索引源，不额外维护数据库。</summary>
public sealed class ClipInfo
{
    public required string Path { get; init; }
    public required string GameId { get; init; }
    public required DateTime CreatedLocal { get; init; }
    public required long Bytes { get; init; }
    public required ClipKind Kind { get; init; }
    public required float Score { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
    public double Seconds => 0;
}

/// <summary>
/// 片段库：扫描目录、解析文件名、统计体积。
///
/// 文件名契约：<c>{gameId}_{MMdd-HHmmss}_{score:F2}.mp4</c>，比如
/// <c>apex_1028-130333_0.99.mp4</c>。可选地在 gameId 后加种类后缀
/// <c>_k</c> / <c>_kd</c> 表示击杀 / 被击倒，缺省即击倒。
/// 用文件名而不是 sqlite，是因为这个库要能被用户直接看懂、直接改。
/// </summary>
public sealed class ClipLibrary
{
    private readonly string _dir;
    public ClipLibrary(string dir) => _dir = dir;
    public string Dir => _dir;

    /// <summary>按落盘时间升序返回（最旧的在前，正好是淘汰顺序的起点）。</summary>
    public List<ClipInfo> Scan()
    {
        var list = new List<ClipInfo>();
        if (!Directory.Exists(_dir)) return list;
        foreach (var p in Directory.EnumerateFiles(_dir, "*.mp4", SearchOption.TopDirectoryOnly))
        {
            var info = Parse(p);
            if (info is not null) list.Add(info);
        }
        list.Sort((a, b) => a.CreatedLocal.CompareTo(b.CreatedLocal));
        return list;
    }

    public static ClipInfo? Parse(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        if (parts.Length < 3) return null;

        // 文件名契约（从尾部数最稳）：... _{MMdd-HHmmss}_{score}
        // 种类后缀紧跟在 gameId 之后，所以 gameId 本身带下划线时也不受影响。
        var timePart = parts[^2];
        var scorePart = parts[^1];
        if (!DateTime.TryParseExact(timePart, "MMdd-HHmmss", null,
                System.Globalization.DateTimeStyles.None, out var t)) return null;
        if (!float.TryParse(scorePart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var score)) score = 0;

        string kindTok = parts.Length >= 4 ? parts[^3] : "";
        var kind = kindTok switch
        {
            "k" => ClipKind.Kill,
            "kd" => ClipKind.Knocked,
            _ => ClipKind.Knock,
        };
        // gameId = 种类记号之前的全部（可能本身含下划线）
        var gameId = parts.Length >= 4 ? string.Join('_', parts[..^3]) : parts[0];

        // 跨年时 MMdd 会回到 1 月，这里把年份补成「不晚于今天」的最近一次
        var now = DateTime.Now;
        var dt = new DateTime(now.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
        if (dt > now.AddDays(1)) dt = dt.AddYears(-1);

        long bytes;
        try { bytes = new FileInfo(path).Length; } catch { return null; }

        return new ClipInfo
        {
            Path = path,
            GameId = gameId,
            CreatedLocal = dt,
            Bytes = bytes,
            Kind = kind,
            Score = score,
        };
    }

    public long TotalBytes(IEnumerable<ClipInfo> clips) => clips.Sum(c => c.Bytes);

    public bool Delete(ClipInfo clip)
    {
        try { File.Delete(clip.Path); return !File.Exists(clip.Path); }
        catch { return false; }
    }
}

/// <summary>配额/保留期策略。默认值来自 docs/apex-v1-spec.md §5。</summary>
public sealed class QuotaPolicy
{
    /// <summary>库里片段总体积上限，默认 40GB。</summary>
    public long QuotaGb { get; set; } = 40;
    /// <summary>超过这个天数就清掉，默认 30 天；0 表示永久保留。</summary>
    public int RetainDays { get; set; } = 30;
    /// <summary>磁盘可用空间低于这个值就优雅停录，默认 2GB。</summary>
    public long WatermarkGb { get; set; } = 2;
    /// <summary>停止检查磁盘间隔。</summary>
    public int CheckMinutes { get; set; } = 60;

    [JsonIgnore]
    public long QuotaBytes => QuotaGb <= 0 ? long.MaxValue : QuotaGb * (1L << 30);
    [JsonIgnore]
    public long WatermarkBytes => WatermarkGb <= 0 ? 0 : WatermarkGb * (1L << 30);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>从 config.json 读；文件不存在或写坏了都退回默认值，绝不因此启动失败。</summary>
    public static QuotaPolicy Load(string? path)
    {
        var p = new QuotaPolicy();
        try
        {
            if (path is not null && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<QuotaPolicy>(File.ReadAllText(path), Json);
                if (loaded is not null) p = loaded;
            }
        }
        catch { /* 配置坏了就用默认值 */ }
        return p;
    }

    public void Save(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
        }
        catch { }
    }
}

/// <summary>一次清理的结果，便于日志和自检断言。</summary>
public sealed record QuotaReport(
    int Scanned, long BeforeBytes,
    List<string> DeletedByAge, List<string> DeletedByQuota,
    int Kept, long AfterBytes, string? Note)
{
    public int DeletedTotal => DeletedByAge.Count + DeletedByQuota.Count;
    public long BytesFreed => BeforeBytes - AfterBytes;
    public override string ToString()
        => $"{Scanned} 片 {Mb(BeforeBytes)} → 保留 {Kept} 片 {Mb(AfterBytes)}" +
           $"（期内删 {DeletedByAge.Count}、超配删 {DeletedByQuota.Count}，释放 {Mb(BytesFreed)}）" +
           (Note is null ? "" : "  " + Note);

    public static string Mb(long b) => b >= 1L << 30 ? $"{b / 1024.0 / 1024 / 1024:F2}GB" : $"{b / 1024.0 / 1024:F0}MB";
}

/// <summary>
/// 配额清理器。
///
/// 两条独立的删除理由，顺序很重要：
///   1. **超期**：`CreatedLocal` 早于 `now - RetainDays` 的一律删，不看体积。
///   2. **超配**：还超配额时，按淘汰优先级从旧到新删，直到落回配额内。
///
/// 淘汰优先级是「被击倒 &gt; 击倒 &gt; 击杀」——即先删队友/自己被击倒的，最少删自己击杀的高光。
/// 同一优先级内部按时间从旧到新。
/// </summary>
public static class QuotaEnforcer
{
    /// <summary>被删得越早，值越高。</summary>
    private static int Priority(ClipKind k) => k switch
    {
        ClipKind.Knocked => 0,
        ClipKind.Knock => 1,
        _ => 2,
    };

    public static QuotaReport Run(ClipLibrary lib, QuotaPolicy p, DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        var clips = lib.Scan();
        long before = clips.Sum(c => c.Bytes);
        var byAge = new List<string>();
        var byQuota = new List<string>();

        var alive = new List<ClipInfo>(clips);

        if (p.RetainDays > 0)
        {
            var cutoff = now.AddDays(-p.RetainDays);
            foreach (var c in clips.Where(c => c.CreatedLocal < cutoff).ToList())
            {
                if (lib.Delete(c)) { byAge.Add(c.Name); alive.Remove(c); }
            }
        }

        long total = alive.Sum(c => c.Bytes);
        if (total > p.QuotaBytes)
        {
            // 稀缺的「自己击杀」留到最后，先牺牲被击倒和无种类的
            foreach (var c in alive.OrderBy(c => Priority(c.Kind))
                                   .ThenBy(c => c.CreatedLocal)
                                   .ToList())
            {
                if (total <= p.QuotaBytes) break;
                if (lib.Delete(c)) { byQuota.Add(c.Name); total -= c.Bytes; alive.Remove(c); }
            }
        }

        string? note = null;
        if (alive.Count == 0 && clips.Count > 0) note = "库已清空";
        return new QuotaReport(clips.Count, before, byAge, byQuota, alive.Count, total, note);
    }

    /// <summary>写片段所在盘还剩多少字节；拿不到时返回 long.MaxValue（当作不缺水）。</summary>
    public static long FreeBytes(string dir)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(dir);
            var root = System.IO.Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return long.MaxValue; }
    }

    /// <summary>磁盘水位是否已经低到该停录了。</summary>
    public static bool BelowWatermark(string dir, QuotaPolicy p)
        => p.WatermarkBytes > 0 && FreeBytes(dir) < p.WatermarkBytes;
}
