using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 阈值回归：把以前录制时存下来的 band 图（hit_*.bmp / near_*.bmp）重新过一遍检测器，
/// 打印每张的分数，以及在不同阈值下会判「命中」还是「不命中」。
///
/// 为什么需要它：阈值是个纯数字，改了以后没法靠看代码判断「会不会漏录 / 会不会乱录」。
/// 2026-10-28 那局实测留下 11 张图（6 次真击倒 0.69–0.98、2 次误判 0.626/0.630），
/// 正好当回归集：调完阈值跑一遍，就能确认既挡住误判又没误杀真命中。
/// </summary>
internal static class BandCheck
{
    public static int Run(BannerEngine engine, string dir, float[]? thresholds = null)
    {
        var files = CaptureDiagnostics.Files(dir, "*.bmp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine($"[X] {dir} 里没有 bmp");
            return 5;
        }

        var prof = engine.Profile;
        int bw = prof.Band.W, bh = prof.Band.H;
        var ths = thresholds is { Length: > 0 } ? thresholds : new[] { 0.60f, 0.65f, 0.70f, 0.75f };

        Console.WriteLine($"[1] 条带 {bw}x{bh}，模板 " +
            string.Join("+", prof.Templates.Select(t => $"{t.Id}({t.W}x{t.H},th={t.Threshold:F2})")));
        Console.WriteLine($"[2] 目录 {dir}，{files.Length} 张图");
        Console.WriteLine("[3] 逐张分数（文件名里的分数是当时实测值，用来交叉核对）");

        var rows = new List<(string Name, bool Legacy, float Score)>();
        var engines = new Dictionary<(int Width, int Height), BannerEngine> { [(bw, bh)] = engine };
        foreach (var f in files)
        {
            if (!TryLoadGray(f, bw, bh, out var gray, out var size, out bool legacy, out var err))
            {
                Console.WriteLine($"    {Path.GetFileName(f),-34} 读不出: {err}");
                continue;
            }
            if (!engines.TryGetValue(size, out var scorer))
            {
                scorer = new BannerEngine(prof, size.Width, size.Height) { ThresholdOverride = engine.ThresholdOverride };
                scorer.SetHudScale(engine.HudScalePct);
                engines[size] = scorer;
            }
            var det = scorer.Score(gray);
            float inName = ParseScoreFromName(Path.GetFileName(f));
            rows.Add((Path.GetFileName(f), legacy, det.Score));
            // 旧版 900 宽条带（2026-10-27 那批）取景位置和当时模板都跟现在不同，
            // 分数没法跟文件名对齐，只作参考、不参与阈值统计。
            string flag = legacy ? "" : inName > 0 && Math.Abs(inName - det.Score) > 0.02f ? "  [!] 与文件名不符" : "";
            if (legacy) flag = "  [旧条带]";
            Console.WriteLine($"    {Path.GetFileName(f),-34} score={det.Score:F3}  x={det.X},y={det.Y}  文件名={inName:F2}{flag}");
        }

        Console.WriteLine("[4] 各阈值下会触发哪些（排除历史窄条带）");
        var live = rows.Where(r => !r.Legacy).ToList();
        foreach (var th in ths)
        {
            var on = live.Where(r => r.Score >= th).ToList();
            var off = live.Where(r => r.Score < th).ToList();
            Console.WriteLine($"    阈值 {th:F2}: 命中 {on.Count} 张 / 挡掉 {off.Count} 张");
            if (off.Count > 0)
                Console.WriteLine("             挡掉: " + string.Join(" ", off.Select(o => $"{o.Name}({o.Score:F2})")));
        }

        // 最高分被挡掉的那张，就是漏录风险
        var maxOff = live.Where(r => r.Score < ths[^1]).OrderByDescending(r => r.Score).FirstOrDefault();
        if (maxOff.Name is not null)
            Console.WriteLine($"[5] 最严阈值 {ths[^1]:F2} 下仍被挡掉的最高分 = {maxOff.Score:F3}（{maxOff.Name}）—— 若它其实是真命中，说明阈值太高");
        return 0;
    }

    private static float ParseScoreFromName(string name)
    {
        // hit_00828_0.98.bmp -> 0.98
        int us = name.LastIndexOf('_');
        int dot = name.LastIndexOf('.');
        if (us < 0 || dot <= us) return 0;
        var s = name[(us + 1)..dot];
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>新版诊断图按留存时的条带尺寸复查，历史窄条带保留原有的偏移补齐。</summary>
    private static bool TryLoadGray(string path, int wantW, int wantH, out byte[] gray,
        out (int Width, int Height) size, out bool legacy, out string err)
    {
        gray = Array.Empty<byte>();
        size = (wantW, wantH);
        legacy = false;
        err = "";
        try
        {
            using var bmp = new Bitmap(path);
            bool managed = CaptureDiagnostics.IsManagedImage(path);
            if (managed) size = (bmp.Width, bmp.Height);
            else if (bmp.Height != wantH)
            {
                Console.WriteLine($"    [!] {Path.GetFileName(path)} 是 {bmp.Width}x{bmp.Height}，高度与条带 {wantH} 不符，跳过");
                err = "尺寸不符";
                return false;
            }
            legacy = !managed && bmp.Width != wantW;
            using var buf = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(buf)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            var rect = new Rectangle(0, 0, buf.Width, buf.Height);
            var data = buf.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var raw = new byte[data.Stride * buf.Height];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                // 旧 900 宽条带从 x=900 起，比 1280 宽条带晚 260 像素。
                int pad = legacy ? 260 : 0;
                gray = new byte[checked(size.Width * size.Height)];
                int copyWidth = Math.Min(size.Width - pad, buf.Width);
                for (int y = 0; y < size.Height; y++)
                    for (int x = 0; x < copyWidth; x++)
                    {
                        int i = y * data.Stride + x * 3;
                        gray[y * size.Width + x + pad] = (byte)((raw[i] + raw[i + 1] + raw[i + 2]) / 3);
                    }
            }
            finally { buf.UnlockBits(data); }
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }
}
