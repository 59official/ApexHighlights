namespace ApexHighlights;

/// <summary>
/// 横幅 OCR 分类的回归自检：拿真实对局录下的 <c>hit_*.bmp</c> 重跑分类，
/// 确认种类判定和去重逻辑都对。
///
/// 为什么要单独一个自检：OCR 依赖系统语言包，换台机器、换个 Windows 版本，
/// 识别结果都可能变。分类错了不会崩，但会把「助攻击倒」录成「击倒」，
/// 用户是看不出来的 —— 所以必须有办法在真实素材上验证。
/// </summary>
internal static class OcrCheck
{
    public static int Run(string dir)
    {
        Console.WriteLine($"[1] 目录 {dir}");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("[X] 目录不存在");
            return 2;
        }

        var files = Directory.GetFiles(dir, "hit_*.bmp")
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        Console.WriteLine($"[2] 找到 {files.Count} 张 hit_*.bmp");
        if (files.Count == 0) return 2;

        var ocr = new BannerOcr();

        // 预热：首次识别会加载语言模型（约 300ms），必须排除在均值之外
        var warm = files[0];
        ocr.Classify(warm);

        Console.WriteLine($"[3] OCR 引擎 {ocr.EngineName}");
        if (ocr.Err.Length > 0) Console.WriteLine($"[!] {ocr.Err}");
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<(string Name, BannerKind Kind, string Text)>();
        foreach (var f in files)
        {
            var (kind, text) = ocr.Classify(f);
            rows.Add((Path.GetFileNameWithoutExtension(f), kind, text));
        }
        sw.Stop();

        foreach (var (name, kind, text) in rows)
            Console.WriteLine($"  {name,-22} [{BannerOcr.Label(kind),-5}] {Trim(text, 70)}");

        var recognized = rows.Count(r => r.Kind != BannerKind.Unknown);
        Console.WriteLine();
        Console.WriteLine($"[4] {rows.Count} 张：识别出种类 {recognized}，未识别 {rows.Count - recognized}；" +
                          $"平均 {sw.Elapsed.TotalMilliseconds / rows.Count:F1} ms/张");

        // 按种类汇总
        Console.WriteLine("[5] 分类汇总");
        foreach (var g in rows.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"      {BannerOcr.Label(g.Key),-5} {g.Count()} 张");

        // 合并模拟：与 Capture.ClipLoop 同一套判据 —— 命中间隔 ≤ MergeMs(8s) 的并成一段。
        // 这里用文案判重是**错的**（玩家名中英混排，OCR 每次读出的乱码都不一样），
        // 所以只按时间合并。
        var groups = new List<List<(string Name, BannerKind Kind, string Text)>>();
        foreach (var r in rows)
        {
            long ms = MsFromName(r.Name);
            if (groups.Count > 0 && ms - MsFromName(groups[^1][^1].Name) <= 8000)
                groups[^1].Add(r);
            else
                groups.Add(new List<(string, BannerKind, string)> { r });
        }
        Console.WriteLine();
        Console.WriteLine($"[6] 合并（相邻命中 ≤ 8s 并成一段）：{rows.Count} 次命中 → {groups.Count} 段");
        foreach (var g in groups.Where(g => g.Count > 1))
            Console.WriteLine($"      并 {string.Join(" + ", g.Select(x => x.Name))}");
        Console.WriteLine($"      → 用户实际看到的片段数从 {rows.Count} 变成 {groups.Count}");

        return recognized == 0 ? 5 : 0;
    }

    /// <summary>hit_00876_0.61 → 876 秒（文件名第二段是条带时刻，单位秒）。</summary>
    private static long MsFromName(string name)
    {
        var parts = name.Split('_');
        if (parts.Length >= 2 && long.TryParse(parts[1], out var sec)) return sec * 1000L;
        return 0;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
