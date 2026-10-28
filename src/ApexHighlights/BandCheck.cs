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
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.bmp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
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

        var rows = new List<(string Name, string Full, float Score, float InName)>();
        foreach (var f in files)
        {
            if (!TryLoadGray(f, bw, bh, out var gray, out var err))
            {
                Console.WriteLine($"    {Path.GetFileName(f),-34} 读不出: {err}");
                continue;
            }
            var det = engine.Score(gray);
            float inName = ParseScoreFromName(Path.GetFileName(f));
            rows.Add((Path.GetFileName(f), f, det.Score, inName));
            // 旧版 900 宽条带（2026-10-27 那批）取景位置和当时模板都跟现在不同，
            // 分数没法跟文件名对齐，只作参考、不参与阈值统计。
            bool legacy = IsLegacyWidth(f, bw);
            string flag = legacy ? "" : inName > 0 && Math.Abs(inName - det.Score) > 0.02f ? "  [!] 与文件名不符" : "";
            if (legacy) flag = "  [旧条带]";
            Console.WriteLine($"    {Path.GetFileName(f),-34} score={det.Score:F3}  x={det.X},y={det.Y}  文件名={inName:F2}{flag}");
        }

        Console.WriteLine("[4] 各阈值下会触发哪些（只统计当前条带尺寸的图）");
        var live = rows.Where(r => !IsLegacyWidth(r.Full, bw)).ToList();
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

    /// <summary>旧版 900 宽条带？</summary>
    private static bool IsLegacyWidth(string path, int wantW)
    {
        try
        {
            using var bmp = new Bitmap(path);
            return bmp.Width != wantW;
        }
        catch { return false; }
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

    /// <summary>把 24 位 BMP 读成灰度条带；尺寸不符就缩（历史图有 900 宽的）。</summary>
    private static bool TryLoadGray(string path, int wantW, int wantH, out byte[] gray, out string err)
    {
        gray = Array.Empty<byte>();
        err = "";
        try
        {
            using var bmp = new Bitmap(path);
            if (bmp.Height != wantH)
            {
                Console.WriteLine($"    [!] {Path.GetFileName(path)} 是 {bmp.Width}x{bmp.Height}，高度与条带 {wantH} 不符，跳过");
                err = "尺寸不符";
                return false;
            }
            using var buf = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(buf)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            var rect = new Rectangle(0, 0, buf.Width, buf.Height);
            var data = buf.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var raw = new byte[data.Stride * buf.Height];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                if (buf.Width == wantW)
                {
                    gray = new byte[wantW * wantH];
                    for (int y = 0; y < wantH; y++)
                        for (int x = 0; x < wantW; x++)
                        {
                            int i = y * data.Stride + x * 3;
                            gray[y * wantW + x] = (byte)((raw[i] + raw[i + 1] + raw[i + 2]) / 3);
                        }
                }
                else
                {
                    // 老版本用 900 宽条带（取景 x=900 起），新条带 x=640 起 → 往右挪 260。
                    // 不能缩放：缩放过模板尺寸和内容都会变，分数没有可比性。
                    int pad = 260;
                    gray = new byte[wantW * wantH];
                    for (int y = 0; y < wantH; y++)
                        for (int x = 0; x < wantW; x++)
                        {
                            int sx = x - pad;
                            if (sx < 0 || sx >= buf.Width) continue;
                            int i = y * data.Stride + sx * 3;
                            gray[y * wantW + x] = (byte)((raw[i] + raw[i + 1] + raw[i + 2]) / 3);
                        }
                }
            }
            finally { buf.UnlockBits(data); }
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }
}
