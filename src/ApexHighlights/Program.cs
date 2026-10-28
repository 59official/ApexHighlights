using System.Diagnostics;

namespace ApexHighlights;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string game = "apex";
            string? logPath = null, title = null, offline = null;
            int fps = 60, gop = 0, fakeHit = 0, bitrate = 20_000_000, dump = 0, duration = 0;
            bool encode = true;
            bool audio = true;
            bool beep = true;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--offline") offline = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
                else if (a.StartsWith("--game=", StringComparison.OrdinalIgnoreCase)) game = a[7..];
                else if (a.StartsWith("--log=", StringComparison.OrdinalIgnoreCase)) logPath = a[6..];
                else if (a.StartsWith("--title=", StringComparison.OrdinalIgnoreCase)) title = a[8..];
                else if (a.StartsWith("--fps=", StringComparison.OrdinalIgnoreCase)) fps = int.Parse(a[6..]);
                else if (a.StartsWith("--bitrate=", StringComparison.OrdinalIgnoreCase)) bitrate = int.Parse(a[10..]) * 1_000_000;
                else if (a == "--no-encode") encode = false;
                else if (a == "--no-audio") audio = false;
                else if (a == "--no-beep") beep = false;
                else if (a.StartsWith("--gop=", StringComparison.OrdinalIgnoreCase)) gop = int.Parse(a[6..]);
                else if (a.StartsWith("--fake-hit=", StringComparison.OrdinalIgnoreCase)) fakeHit = int.Parse(a[11..]);
                else if (a.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase)) dump = int.Parse(a[7..]);
                else if (a.StartsWith("--duration=", StringComparison.OrdinalIgnoreCase)) duration = int.Parse(a[11..]);
                else if (!a.StartsWith("--")) title = a;
            }

            if (args.Length >= 2 && args[0] == "--mp4-check")
                return Mp4Check.Run(args[1]);

            if (args.Length >= 2 && args[0] == "--audio-check")
                return AudioCheck.Run(args[1]);

            if (args.Length >= 2 && args[0] == "--beep-test")
                return Beep.Dump(args[1]);

            if (args.Any(a => a == "--nv12-test")) return Nv12Test();

            if (args.Any(a => a == "--aac-probe")) return AacProbe.Run();

            if (args.Any(a => a == "--audio-test"))
            {
                var s = args.FirstOrDefault(a => a.StartsWith("--sec=", StringComparison.OrdinalIgnoreCase));
                return AudioTest.Run(s is not null ? int.Parse(s[6..]) : 3);
            }

            if (args.Any(a => a == "--enc-test"))
                return EncTest.Run(2560, 1440, 180, @"D:\played\captures\enc_test");

            if (args.Any(a => a == "--av-test"))
            {
                var s = args.FirstOrDefault(a => a.StartsWith("--sec=", StringComparison.OrdinalIgnoreCase));
                return AvTest.Run(@"D:\played\captures\av_test", s is not null ? int.Parse(s[6..]) : 4);
            }

            if (args.Any(a => a == "--gpu-test"))
            {
                var sm = args.FirstOrDefault(a => a.StartsWith("--surface-mode=", StringComparison.OrdinalIgnoreCase));
                if (sm is not null) NvEncoder.SurfaceMode = int.Parse(sm[15..]);
                Console.WriteLine($"[0] surface-mode={NvEncoder.SurfaceMode}");
                return GpuTest.Run(2560, 1440, 180, @"D:\played\captures\gpu_test");
            }

            var profile = GameProfile.Load(GameProfile.ResolvePath(game));
            var engine = new BannerEngine(profile);
            return offline is not null
                ? Offline(engine, offline.Length == 0 ? @"D:\played\probe\bands.bin" : offline)
                : Live(engine, dump, title ?? (profile.WindowTitles.Length > 0 ? profile.WindowTitles[0] : profile.Name), logPath, fps, bitrate, encode, gop, fakeHit, duration, audio, beep);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[X] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>离线：对预导出的 band 序列跑检测器，验证 C# 实现与 Python 结果一致。</summary>
    private static int Offline(BannerEngine engine, string binPath)
    {
        var prof = engine.Profile;
        int bw0 = prof.Band.W, bh0 = prof.Band.H;
        float th = prof.Templates[0].Threshold;

        using var fs = File.OpenRead(binPath);
        using var br = new BinaryReader(fs);
        int bh = br.ReadInt32(), bw = br.ReadInt32();
        Console.WriteLine($"[1] band 文件 {bw}x{bh}；配置 {bw0}x{bh0}；模板 " +
            string.Join("+", prof.Templates.Select(t => $"{t.Id}({t.W}x{t.H})")) + $"；阈值 {th:F2}");

        // 兼容旧的 900 宽条带（历史取景 x=900 起）：把它摆到新条带里的正确位置
        int pad = bw == bw0 ? 0 : Math.Max(0, 900 - prof.Band.X);

        var band = new byte[bw0 * bh0];
        var raw = new byte[bw * bh];
        var res = new List<(int ts, float score)>();
        var sw = new Stopwatch();
        while (fs.Position < fs.Length)
        {
            int ts = br.ReadInt32();
            if (br.Read(raw, 0, raw.Length) != raw.Length) break;
            if (pad == 0) Buffer.BlockCopy(raw, 0, band, 0, raw.Length);
            else
            {
                for (int y = 0; y < bh && y < bh0; y++)
                    for (int x = 0; x < bw0; x++)
                    {
                        int sx = Math.Clamp(x - pad, 0, bw - 1);
                        band[y * bw0 + x] = raw[y * bw + sx];
                    }
            }
            sw.Start();
            var d = engine.Score(band);
            sw.Stop();
            res.Add((ts, d.Score));
        }

        Console.WriteLine($"[2] {res.Count} 帧，平均 {sw.Elapsed.TotalMilliseconds / Math.Max(1, res.Count):F1} ms/帧");

        var sc = res.Select(r => r.score).OrderBy(v => v).ToArray();
        Console.WriteLine($"[3] 分数 p50={Pct(sc, 50):F3} p95={Pct(sc, 95):F3} p99={Pct(sc, 99):F3} max={sc[^1]:F3}");

        foreach (var t in new[] { th - 0.05f, th, 0.70f, 0.80f })
        {
            var ev = Merge(res.Where(r => r.score >= t).ToList(), 3000);
            Console.WriteLine($"[4] 阈值 {t:F2}: {res.Count(r => r.score >= t)} 帧 → {ev.Count} 事件: " +
                string.Join(" | ", ev.Select(e => $"{e.start}s({e.peak:F2},x{e.count})")));
        }

        var top = res.OrderByDescending(r => r.score).Take(12).ToList();
        Console.WriteLine("[5] Top12: " + string.Join(" ", top.Select(t => $"{t.ts / 1000.0:F1}s/{t.score:F2}")));

        var events = Merge(res.Where(r => r.score >= th).ToList(), 3000);
        var outside = res.Where(r => !events.Any(e => r.ts >= e.start && r.ts <= e.end)).ToList();
        Console.WriteLine($"[6] 事件外最高分 = {(outside.Count > 0 ? outside.Max(o => o.score) : 0):F3}");
        return 0;
    }

    private static double Pct(float[] sorted, double p)
        => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)(sorted.Length * p / 100.0), 0, sorted.Length - 1)];

    private static List<(int start, int end, float peak, int count)> Merge(List<(int ts, float score)> hits, int gapMs)
    {
        var ev = new List<(int start, int end, float peak, int count)>();
        foreach (var (ts, s) in hits.OrderBy(h => h.ts))
        {
            if (ev.Count > 0 && ts - ev[^1].end <= gapMs)
            {
                var e = ev[^1];
                ev[^1] = (e.start, ts, Math.Max(e.peak, s), e.count + 1);
            }
            else ev.Add((ts, ts, s, 1));
        }
        return ev;
    }

    private static int Nv12Test()
    {
        const int w = 2560, h = 1440;
        var bgra = new byte[w * h * 4];
        // 4 个色块：白 / 黑 / 红 / 绿
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte r, g, b;
                if (x < w / 4) { r = g = b = 255; }
                else if (x < w / 2) { r = g = b = 0; }
                else if (x < w * 3 / 4) { r = 255; g = 0; b = 0; }
                else { r = 0; g = 255; b = 0; }
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }

        var dst = new byte[w * h * 3 / 2];
        Nv12.FromBgra(bgra, w * 4, w, h, dst);
        int ys = w * h;
        Console.WriteLine($"白 Y={dst[(h / 2) * w + w / 8]} (16+219=235)  U={dst[ys + (h / 4) * w]} V={dst[ys + (h / 4) * w + 1]} (128,128)");
        Console.WriteLine($"黑 Y={dst[(h / 2) * w + w * 3 / 8]} (16)  U={dst[ys + (h / 4) * w + 2]} V={dst[ys + (h / 4) * w + 3]} (128,128)");
        Console.WriteLine($"红 Y={dst[(h / 2) * w + w * 5 / 8]} (63)  U={dst[ys + (h / 4) * w + 4]} V={dst[ys + (h / 4) * w + 5]} (90,240)");
        Console.WriteLine($"绿 Y={dst[(h / 2) * w + w * 7 / 8]} (173) U={dst[ys + (h / 4) * w + 6]} V={dst[ys + (h / 4) * w + 7]} (54,34)");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) Nv12.FromBgra(bgra, w * 4, w, h, dst);
        Console.WriteLine($"转换 {sw.Elapsed.TotalMilliseconds / 20:F2} ms/帧 @2560x1440");
        return 0;
    }

    private static int Live(BannerEngine engine, int dumpAtSec, string title, string? logPath, int fps, int bitrate, bool encode, int gop, int fakeHit, int durationSec, bool audio = true, bool beep = true)
    {
        var prof = engine.Profile;

        // 每次跑留存历史：把上一局的 log/csv 改名归档，别被覆盖
        if (logPath is not null)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    var stamp = File.GetLastWriteTime(logPath).ToString("MMdd-HHmmss");
                    var dir = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
                    var baseName = Path.GetFileNameWithoutExtension(logPath);
                    File.Move(logPath, Path.Combine(dir, $"{baseName}-{stamp}.log"), true);
                    if (File.Exists(logPath + ".csv"))
                        File.Move(logPath + ".csv", Path.Combine(dir, $"{baseName}-{stamp}.log.csv"), true);
                }
            }
            catch { }
        }

        // 自己写日志：不依赖 cmd 的 tee，双击也不会「闪一下就没了」
        StreamWriter? log = null;
        if (logPath is not null)
        {
            try
            {
                log = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
                log.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}  game={prof.Id}  target=\"{title}\"");
            }
            catch (Exception ex) { Console.WriteLine($"[!] 日志开不了: {ex.Message}"); }
        }
        void Out(string s) { Console.WriteLine(s); log?.WriteLine(s); }

        var hwnd = Native.ResolveWindow(title);
        if (hwnd == IntPtr.Zero)
        {
            Out($"[X] 没找到标题含 \"{title}\" 的窗口 —— 游戏是不是还没开 / 还停在大厅？");
            Out("[X] 可以在别的窗口上跑 WgcTest.exe --list 看看有哪些窗口");
            return 2;
        }

        Out($"[1] 目标窗口: \"{Native.TitleOf(hwnd)}\"");
        using var cap = new Capture(hwnd, engine)
        {
            DiagPath = logPath is null ? null : logPath + ".csv",
            TargetFps = fps,
            EncodedFps = fps,
            Bitrate = bitrate,
        };
        cap.Detected += d =>
        {
            Out($"[HIT] {d.WallClock:HH:mm:ss}  +{d.Ms / 1000.0,7:F1}s  [{d.Label}]  score={d.Score:F2}  at({d.X},{d.Y})");
            if (d.ClipPath is not null) Out($"[CLIP] {d.ClipPath}  {d.Bytes / 1024.0 / 1024.0:F1} MB");
        };
        if (encode)
        {
            cap.GopFrames = gop > 0 ? gop : fps;
            cap.FakeHitAtSec = fakeHit;
            cap.DumpAtSec = dumpAtSec;
            cap.AudioEnabled = audio;
            cap.BeepOnSave = beep;
            cap.EnableEncoding(90);
            Out($"[2b] 编码 {cap.Width}x{cap.Height} @{fps}fps {bitrate / 1_000_000}Mbps，环形 90s，音轨{(audio ? "开" : "关")}，提示音{(beep ? "开" : "关")}");
        }
        Out($"[2] 采集已启动 {cap.Width}x{cap.Height}；条带 {prof.Band.W}x{prof.Band.H}@{prof.Band.X},{prof.Band.Y}；采集 {fps}fps / 检测 10fps");
        Out($"[3] 模板 {string.Join(",", prof.Templates.Select(t => $"{t.Id}({t.W}x{t.H}@{t.Threshold:F2})"))}；命中存条带到 {cap.OutDir}");
        if (cap.Width < 1200) Out("[!] 窗口小于 1200 宽，检测走的缩放路径；游戏建议 2560x1440");

        var tick = 0;
        var sw = Stopwatch.StartNew();
        while (durationSec == 0 || sw.ElapsedMilliseconds < durationSec * 1000L)
        {
            Thread.Sleep(10000);
            Out($"[stat +{++tick * 10}s] {cap.Stats()}");
        }
        Out($"[end {sw.ElapsedMilliseconds / 1000.0:F1}s] {cap.Stats()}");
        return 0;
    }
}
