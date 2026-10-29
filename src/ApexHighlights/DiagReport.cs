using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ApexHighlights;

/// <summary>
/// 「为什么这一局一次都没录上」的自检报告。
///
/// 输入是 console 模式写出来的逐帧 CSV（`--log=` 时自动生成 `&lt;log&gt;.csv`，
/// 每行 <c>毫秒,分数,x,y</c>，每个检测帧一行），输出是一份人话报告：
/// 最高分、分数分布、当时用的模板/HUD/阈值、以及一句结论。
///
/// 存在的理由：识别这条链路有四种完全不同的死法（模板没匹配上 / 卡在灰区 / 到了阈值
/// 但被种类开关拦掉 / 录了但被合并窗口吞了），光看「没录上」分不出是哪一种。
/// </summary>
internal static class DiagReport
{
    public static int Run(string pathOrDir)
    {
        var csv = Resolve(pathOrDir);
        if (csv is null)
        {
            Console.WriteLine($"[X] 在 {pathOrDir} 里没找到 *.csv —— 用 `--log=xxx.log` 跑一次才会生成逐帧数据");
            return 2;
        }

        Console.WriteLine($"[0] 报告数据 {csv}");
        Console.WriteLine($"[1] 程序 {typeof(DiagReport).Assembly.GetName().Version} · 数据目录 {Paths.DataRoot}");

        // 模板 / HUD / 阈值：现在磁盘上真正生效的那一份
        try
        {
            var prof = GameProfile.Load(GameProfile.ResolvePath("apex"));
            var det = DetectPolicy.Load(Paths.ConfigFile);
            Console.WriteLine($"[2] 配置 {GameProfile.ResolvePath("apex")}");
            Console.WriteLine($"    条带 {prof.Band.W}x{prof.Band.H} @({prof.Band.X},{prof.Band.Y})  模板 {prof.Templates.Count} 张");
            foreach (var t in prof.Templates)
                Console.WriteLine($"      {t.Id,-12} {t.File,-22} th={t.Threshold:F2}  {t.Label}");
            Console.WriteLine($"    HUD 缩放={DetectPolicy.HudLabel(det.HudScalePct)}  命中阈值覆盖={(det.Threshold > 0 ? det.Threshold.ToString("F2") : "0（按模板）")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[2] 读配置失败（不影响下面的分数统计）: {ex.Message}");
        }

        try { Console.WriteLine($"[3] OCR {BannerOcr.LanguageList()}"); } catch { }

        var scores = new List<(long Ms, float Score, int X, int Y)>();
        foreach (var line in File.ReadLines(csv))
        {
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!long.TryParse(parts[0], out var ms)) continue;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc)) continue;
            if (!int.TryParse(parts[2], out var x)) continue;
            if (!int.TryParse(parts[3], out var y)) continue;
            scores.Add((ms, sc, x, y));
        }

        if (scores.Count == 0)
        {
            Console.WriteLine("[X] CSV 里没有一条有效数据——这一局没跑起来采集（窗口没找到？分辨率变了？）");
            return 3;
        }

        var best = scores.MaxBy(s => s.Score);
        var p95 = scores.OrderByDescending(s => s.Score).Skip(scores.Count / 20).First().Score;
        int below = scores.Count(s => s.Score < 0.20f);
        int gray = scores.Count(s => s.Score is >= 0.20f and < 0.30f);
        int near = scores.Count(s => s.Score is >= 0.30f and < 0.60f);
        int over = scores.Count(s => s.Score >= 0.60f);

        Console.WriteLine($"[4] 逐帧 {scores.Count} 帧，最高 {best.Score:F4}（{(best.Ms / 1000.0):F1}s，位置 x={best.X} y={best.Y}），P95 {p95:F4}");
        Console.WriteLine($"[5] 分布  <0.20:{below}  0.20-0.30:{gray}  0.30-0.60:{near}  >=0.60:{over}");

        Console.WriteLine("[6] 结论：" + Verdict(best.Score, over, near, gray));

        var bmps = Directory.EnumerateFiles(Path.GetDirectoryName(csv)!, "diag_top*.bmp")
                            .OrderBy(f => f).ToList();
        if (bmps.Count > 0)
        {
            Console.WriteLine($"[7] 最高分的 {bmps.Count} 帧条带（发给开发者能直接看出问题）：");
            foreach (var f in bmps) Console.WriteLine("      " + f);
        }
        return 0;
    }

    private static string Verdict(float max, int over, int near, int gray)
    {
        if (over > 0)
            return "检测是通的（有帧过了阈值）。这一段里漏录要看别处：种类开关、合并窗口、配额，或者那几秒其实不是击倒横幅。";

        if (near > 0)
            return $"最高 {max:F2} 卡在灰区（0.30–0.60）：横幅在、但和模板对不齐。九成是 HUD/UI 缩放不是 100% —— " +
                   "去托盘「HUD 缩放」手动指定 80/90/110/120 逐个试，或者看下面存下来的条带图。";

        if (gray > 0)
            return $"最高 {max:F2}，比噪声高一点但远不够：多半是条带位置偏了（窗口不是 16:9？）或横幅字体/语言对不上。看条带图最快。";

        return "整段几乎没有像横幅的画面（最高分接近噪声）。先确认：这 60 秒里真的在游戏里、窗口标题是 Apex、以及游戏内语言是不是这三种之一。";
    }

    private static string? Resolve(string pathOrDir)
    {
        if (File.Exists(pathOrDir)) return Path.GetFullPath(pathOrDir);
        if (!Directory.Exists(pathOrDir)) return null;
        return Directory.EnumerateFiles(pathOrDir, "*.csv")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
    }

    /// <summary>把 8 位灰度条带写成最普通的 BMP（8bpp 调色板），任何看图软件都能开。</summary>
    public static void WriteGrayBmp(string path, byte[] gray, int w, int h)
    {
        int stride = (w + 3) & ~3;
        int dataSize = stride * h;
        const int offset = 14 + 40 + 256 * 4;
        var buf = new byte[offset + dataSize];

        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        BitConverter.GetBytes(buf.Length).CopyTo(buf, 2);
        BitConverter.GetBytes(offset).CopyTo(buf, 10);
        BitConverter.GetBytes(40).CopyTo(buf, 14);
        BitConverter.GetBytes(w).CopyTo(buf, 18);
        BitConverter.GetBytes(h).CopyTo(buf, 22);
        BitConverter.GetBytes((short)1).CopyTo(buf, 26);
        BitConverter.GetBytes((short)8).CopyTo(buf, 28);
        BitConverter.GetBytes(dataSize).CopyTo(buf, 34);

        for (int i = 0; i < 256; i++)
        {
            buf[54 + i * 4 + 0] = (byte)i;
            buf[54 + i * 4 + 1] = (byte)i;
            buf[54 + i * 4 + 2] = (byte)i;
        }

        // BMP 的行是自下而上
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(gray, (h - 1 - y) * w, buf, offset + y * stride, w);

        File.WriteAllBytes(path, buf);
    }
}
