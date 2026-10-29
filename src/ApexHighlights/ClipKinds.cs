using System.Globalization;

namespace ApexHighlights;

/// <summary>
/// 「片段种类」的唯一事实来源：<see cref="ClipKind"/> ↔ 文件名记号 ↔ 中文名 ↔
/// 检测器种类（<see cref="BannerKind"/>）。
///
/// 这三个映射以前散在三处：Capture.WriteClip 拼后缀、ClipLibrary.Parse 拆后缀、
/// LibraryForm.KindName 写中文。改一个种类要同时改三处，漏一处就出现「库里显示
/// 助攻击倒、文件名却是 rk」这种对不上的情况。现在只有这张表说了算。
/// </summary>
internal static class ClipKinds
{
    /// <summary>文件名记号：默认种类（击倒）刻意用空串，保持 v1 的 <c>apex_1028-…</c> 老命名。</summary>
    public static string Token(ClipKind k) => k switch
    {
        ClipKind.Kill => "k",
        ClipKind.Knocked => "kd",
        ClipKind.ReKnock => "rk",
        ClipKind.AssistKnock => "ak",
        ClipKind.Elim => "el",
        ClipKind.AssistElim => "ae",
        _ => "",
    };

    public static string Label(ClipKind k) => k switch
    {
        ClipKind.Kill => "击杀",
        ClipKind.Knocked => "被击倒",
        ClipKind.ReKnock => "再次击倒",
        ClipKind.AssistKnock => "助攻击倒",
        ClipKind.Elim => "小队淘汰",
        ClipKind.AssistElim => "助攻淘汰",
        _ => "击倒",
    };

    /// <summary>
    /// 真正会出现的种类，顺序 = 菜单/筛选器里的顺序。
    ///
    /// 以前这份顺序是手写在 LibraryForm 里的，比这里多出「击杀」「被击倒」两条：
    /// <see cref="BannerKind"/> 根本判不出这两种（它只有 Knock/ReKnock/AssistKnock/
    /// Elim/AssistElim），所以那两条永远是空的。用户照着列表一看「被击倒也有呀」
    /// 就以为录错了。现在筛选器和托盘开关都从这一个数组取，不存在第二份名单。
    /// </summary>
    public static readonly ClipKind[] All =
    {
        ClipKind.Knock,
        ClipKind.ReKnock,
        ClipKind.AssistKnock,
        ClipKind.Elim,
        ClipKind.AssistElim,
    };

    public static bool TryToken(string token, out ClipKind kind)
    {
        switch (token)
        {
            case "k": kind = ClipKind.Kill; return true;
            case "kd": kind = ClipKind.Knocked; return true;
            case "rk": kind = ClipKind.ReKnock; return true;
            case "ak": kind = ClipKind.AssistKnock; return true;
            case "el": kind = ClipKind.Elim; return true;
            case "ae": kind = ClipKind.AssistElim; return true;
            default: kind = ClipKind.Knock; return false;
        }
    }

    /// <summary>检测器读出来的横幅种类 → 落盘的片段种类。未识别按击倒处理，和旧行为一致。</summary>
    public static ClipKind FromBanner(BannerKind b) => b switch
    {
        BannerKind.ReKnock => ClipKind.ReKnock,
        BannerKind.AssistKnock => ClipKind.AssistKnock,
        BannerKind.Elim => ClipKind.Elim,
        BannerKind.AssistElim => ClipKind.AssistElim,
        _ => ClipKind.Knock,
    };
}

/// <summary>
/// 片段文件名契约：<c>{gameId}[_{kindToken}]_{MMdd-HHmmss}_{score:F2}.mp4</c>。
///
/// 文件名是回顾库的唯一索引源（没有数据库），所以「怎么拼」和「怎么拆」必须是
/// 同一个模块：以前一边在 Capture 拼、一边在 ClipLibrary 拆，合同只活在注释里，
/// 两边各改各的就对不上了。
/// </summary>
internal static class ClipName
{
    public const string TimeFormat = "MMdd-HHmmss";

    public static string Build(string gameId, ClipKind kind, DateTime local, float score)
    {
        var token = ClipKinds.Token(kind);
        var stem = token.Length == 0 ? gameId : gameId + "_" + token;
        return stem + "_" + local.ToString(TimeFormat, CultureInfo.InvariantCulture)
             + "_" + score.ToString("F2", CultureInfo.InvariantCulture) + ".mp4";
    }

    /// <summary>
    /// 从路径/文件名里拆出五元组。从尾部数最稳：gameId 自己带下划线也不受影响。
    /// 年份不在名字里，按「不晚于今天」的最近一次补（跨年时 MMdd 会绕回去）。
    /// </summary>
    public static bool TryParse(string path, DateTime now,
        out string gameId, out ClipKind kind, out DateTime local, out float score)
    {
        gameId = "";
        kind = ClipKind.Knock;
        local = default;
        score = 0;

        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        if (parts.Length < 3) return false;

        if (!DateTime.TryParseExact(parts[^2], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stamp)) return false;

        if (!float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            score = 0;

        // 倒数第三段是已知记号才算种类；否则它属于 gameId，不能吞掉。
        bool hasKind = parts.Length >= 4 && ClipKinds.TryToken(parts[^3], out kind);
        gameId = string.Join('_', hasKind ? parts[..^3] : parts[..^2]);

        var dt = new DateTime(now.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second);
        if (dt > now.AddDays(1)) dt = dt.AddYears(-1);
        local = dt;
        return true;
    }
}
