using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 界面层的自检：抽缩略图、读元数据、验筛选逻辑、演练删除 → 还原。
///
/// 为什么要这么一个：库窗口是 WinForms，肉眼测不了（模型不能读图），
/// 而「窗口能开」和「缩略图真的有画面」是两件事——空图也是 Bitmap，
/// 只看非 null 会被骗。所以这里把像素统计也打出来，空图会立刻暴露。
/// </summary>
internal static class UiTest
{
    public static int Run(string dir, int samples = 3)
    {
        int fail = 0;
        Console.WriteLine($"[1] 库目录 {dir}");

        var lib = new ClipLibrary(dir);
        var all = lib.Scan();
        Console.WriteLine($"[2] 扫描到 {all.Count} 片，合计 {QuotaReport.Mb(lib.TotalBytes(all))}");
        if (all.Count == 0) { Console.WriteLine("[!] 库里没有片段，先录一段再来"); return 9; }

        // ---- 元数据 ----
        foreach (var c in all)
        {
            var m = Mp4Probe.Read(c.Path);
            var ok = m.Valid && m.Seconds > 0;
            if (!ok) fail++;
            Console.WriteLine($"    {(ok ? "ok " : "BAD")} {c.Name,-30} {Mp4Probe.Fmt(m.Seconds),7} " +
                              $"{(m.Valid ? $"{m.Width}x{m.Height}" : "—"),10} 音轨={(m.HasAudio ? "有" : "无")} {m.Codec}");
        }

        // ---- 缩略图（重点：证明真有画面，不是空图）----
        int n = Math.Min(samples, all.Count);
        for (int i = all.Count - n; i < all.Count; i++)
        {
            var c = all[i];
            var bmp = ShellThumb.Get(c.Path, 160, 90);
            if (bmp is null) { Console.WriteLine($"    BAD 缩略图 {c.Name} → null"); fail++; continue; }
            var stat = Stat(bmp);
            var ok = stat.Std > 2.0 && stat.Unique > 8;
            if (!ok) fail++;
            Console.WriteLine($"    {(ok ? "ok " : "BAD")} 缩略图 {c.Name,-30} {bmp.Width}x{bmp.Height} " +
                              $"亮度 {stat.Mean:F1} 标准差 {stat.Std:F2} 颜色数 {stat.Unique}");
            bmp.Dispose();
        }

        // ---- 大预览（库窗口右侧那一张）----
        var last = all[^1];
        var big = ShellThumb.Get(last.Path, 720, 405);
        if (big is null) { Console.WriteLine("    BAD 大预览 → null"); fail++; }
        else
        {
            var stat = Stat(big);
            Console.WriteLine($"    {(stat.Std > 2.0 ? "ok " : "BAD")} 大预览 {big.Width}x{big.Height} 标准差 {stat.Std:F2}");
            if (stat.Std <= 2.0) fail++;
            big.Dispose();
        }

        // ---- 筛选逻辑（纯函数，不碰 UI）----
        fail += FilterChecks(all);

        // ---- 删除后能扫回来（用副本，不动真库）----
        fail += DeleteCheck(lib, all[^1]);

        Console.WriteLine(fail == 0 ? "[ok] 界面层自检全过" : $"[!] {fail} 项失败");
        return fail == 0 ? 0 : 9;
    }

    private static int FilterChecks(List<ClipInfo> all)
    {
        int fail = 0;
        var now = DateTime.Now;

        // 和 LibraryForm.ApplyFilter 里同一套判定，改一处要同时改
        int Kind(ClipKind k) => all.Count(c => c.Kind == k);
        Log("击倒", Kind(ClipKind.Knock));
        Log("击杀", Kind(ClipKind.Kill));
        Log("被击倒", Kind(ClipKind.Knocked));

        var today = all.Count(c => c.CreatedLocal >= now.Date);
        var w7 = all.Count(c => c.CreatedLocal >= now.AddDays(-7));
        var w30 = all.Count(c => c.CreatedLocal >= now.AddDays(-30));
        Console.WriteLine($"    今天 {today} 片，最近 7 天 {w7} 片，最近 30 天 {w30} 片");
        if (!(w30 >= w7 && w7 >= today)) { Console.WriteLine("    BAD 时间筛选不单调"); fail++; }

        var s = "apex";
        var byName = all.Count(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (byName != all.Count) { Console.WriteLine($"    BAD 搜索 {s} 只命中 {byName}/{all.Count}"); fail++; }
        else Console.WriteLine($"    搜索 \"{s}\" 命中 {byName}/{all.Count}");

        var noHit = all.Count(c => c.Name.Contains("zzz_nothing", StringComparison.OrdinalIgnoreCase));
        if (noHit != 0) { Console.WriteLine("    BAD 无关搜索居然命中"); fail++; }

        void Log(string label, int n) => Console.WriteLine($"    {label} {n} 片");
        return fail;
    }

    private static int DeleteCheck(ClipLibrary lib, ClipInfo src)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ah_uitest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var copy = Path.Combine(tmpDir, Path.GetFileName(src.Path));
            File.WriteAllBytes(copy, new byte[1024]);   // 内容无关，只验扫描/删除
            var l2 = new ClipLibrary(tmpDir);
            var got = l2.Scan();
            if (got.Count != 1) { Console.WriteLine("    BAD 临时库扫描不到副本"); return 1; }
            if (!l2.Delete(got[0])) { Console.WriteLine("    BAD 删除失败"); return 1; }
            if (l2.Scan().Count != 0) { Console.WriteLine("    BAD 删完还能扫到"); return 1; }
            Console.WriteLine("    ok  删除后库里立刻消失");
            return 0;
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    private readonly record struct Px(double Mean, double Std, int Unique);

    /// <summary>缩略图的像素统计：全黑/全灰的图标准差接近 0，能一眼看出没取到画面。</summary>
    private static Px Stat(Bitmap bmp)
    {
        long sum = 0, sum2 = 0, cnt = 0;
        var uniq = new HashSet<int>();
        for (int y = 0; y < bmp.Height; y += 2)
        {
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                int v = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                sum += v; sum2 += (long)v * v; cnt++;
                uniq.Add(c.ToArgb());
            }
        }
        var mean = (double)sum / cnt;
        var vari = (double)sum2 / cnt - mean * mean;
        return new Px(mean, Math.Sqrt(Math.Max(0, vari)), uniq.Count);
    }
}
