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

        // 去重模拟：按时间顺序喂进去，看会砍掉几段（与 Capture.IsDuplicate 同一套判据）
        var kept = new List<(string Name, BannerKind Kind, string Text)>();
        var dropped = new List<string>();
        (string Text, long Ms) last = ("", long.MinValue);
        foreach (var r in rows)
        {
            long ms = MsFromName(r.Name);
            if (!string.IsNullOrWhiteSpace(r.Text) && r.Text == last.Text && ms - last.Ms <= 3000)
            {
                dropped.Add($"{r.Name}（{Trim(r.Text, 40)}）");
                continue;
            }
            last = (r.Text, ms);
            kept.Add(r);
        }
        Console.WriteLine();
        Console.WriteLine($"[6] 去重（同一文案 3 秒内视为同一次事件）：保留 {kept.Count} 段，砍掉 {dropped.Count} 段");
        foreach (var d in dropped) Console.WriteLine($"      砍 {d}");
        Console.WriteLine($"      → 用户实际看到的片段数从 {rows.Count} 变成 {kept.Count}");

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
