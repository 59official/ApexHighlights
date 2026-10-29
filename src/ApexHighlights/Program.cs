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
            int fps = 60, gop = 0, fakeHit = 0, bitrate = 20_000_000, dump = 0, duration = 0, traySelfTest = 0;
            bool encode = true;
            bool audio = true;
            bool beep = true;
            bool bitrateGiven = false;   // --bitrate= 给了就要覆盖 config.json 里的值
            bool ocr = true;     // 横幅 OCR 分类 + 去重；--no-ocr 关掉（没装语言包时用）
            bool tray = true;    // 默认常驻托盘（用户要的形态）；--no-tray 退回纯控制台
            bool ui = false;     // --ui：先把库窗口打开再开始录

            // --data= 必须在任何路径被访问前生效（Paths 是懒初始化的）
            var dataArg = args.FirstOrDefault(a => a.StartsWith("--data=", StringComparison.OrdinalIgnoreCase));
            if (dataArg is not null) Paths.Override(dataArg[7..]);
            Paths.EnsureGames();
            Console.WriteLine($"[0] 数据目录 {Paths.DataRoot}");

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--offline") offline = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
                else if (a.StartsWith("--game=", StringComparison.OrdinalIgnoreCase)) game = a[7..];
                else if (a.StartsWith("--log=", StringComparison.OrdinalIgnoreCase)) logPath = a[6..];
                else if (a.StartsWith("--title=", StringComparison.OrdinalIgnoreCase)) title = a[8..];
                else if (a.StartsWith("--fps=", StringComparison.OrdinalIgnoreCase)) fps = int.Parse(a[6..]);
                else if (a.StartsWith("--bitrate=", StringComparison.OrdinalIgnoreCase)) { bitrate = int.Parse(a[10..]) * 1_000_000; bitrateGiven = true; }
                else if (a == "--no-encode") encode = false;
                else if (a == "--no-audio") audio = false;
                else if (a == "--no-beep") beep = false;
                else if (a == "--no-ocr") ocr = false;
                else if (a == "--tray") tray = true;
                else if (a == "--no-tray") tray = false;
                else if (a == "--ui") ui = true;
                else if (a.StartsWith("--gop=", StringComparison.OrdinalIgnoreCase)) gop = int.Parse(a[6..]);
                else if (a.StartsWith("--fake-hit=", StringComparison.OrdinalIgnoreCase)) fakeHit = int.Parse(a[11..]);
                else if (a.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase)) dump = int.Parse(a[7..]);
                else if (a.StartsWith("--duration=", StringComparison.OrdinalIgnoreCase)) duration = int.Parse(a[11..]);
                else if (a.StartsWith("--tray-test=", StringComparison.OrdinalIgnoreCase)) traySelfTest = int.Parse(a[12..]);
                else if (!a.StartsWith("--")) title = a;
            }

            if (args.Length >= 2 && args[0] == "--mp4-check")
                return Mp4Check.Run(args[1]);

            if (args.Length >= 2 && args[0] == "--audio-check")
                return AudioCheck.Run(args[1]);

            if (args.Length >= 2 && args[0] == "--beep-test")
                return Beep.Dump(args[1]);

            if (args.Any(a => a == "--quota-test")) return QuotaTest.Run();

            if (args.Any(a => a == "--lib-list" || a == "--quota-run"))
                return LibCli(args);

            if (args.Any(a => a == "--lib-ui"))
                return LibUi(args);

            if (args.Any(a => a.StartsWith("--thumb-probe=", StringComparison.OrdinalIgnoreCase)))
                return ThumbProbe.Run(args.First(a => a.StartsWith("--thumb-probe=", StringComparison.OrdinalIgnoreCase))[14..]);

            if (args.Any(a => a == "--ui-test"))
            {
                var d = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))?[6..]
                        ?? Paths.Clips;
                var n = args.FirstOrDefault(a => a.StartsWith("--samples=", StringComparison.OrdinalIgnoreCase));
                return UiTest.Run(d, n is not null ? int.Parse(n[10..]) : 3);
            }

            if (args.Length >= 2 && args[0] == "--probe")
                return Probe(args[1]);

            if (args.Any(a => a == "--nv12-test")) return Nv12Test();

            if (args.Any(a => a == "--aac-probe")) return AacProbe.Run();

            if (args.Any(a => a == "--audio-test"))
            {
                var s = args.FirstOrDefault(a => a.StartsWith("--sec=", StringComparison.OrdinalIgnoreCase));
                return AudioTest.Run(s is not null ? int.Parse(s[6..]) : 3);
            }

            if (args.Any(a => a == "--enc-test"))
                return EncTest.Run(2560, 1440, 180, Path.Combine(Paths.Captures, "enc_test"));

            if (args.Any(a => a == "--av-test"))
            {
                var s = args.FirstOrDefault(a => a.StartsWith("--sec=", StringComparison.OrdinalIgnoreCase));
                return AvTest.Run(Path.Combine(Paths.Captures, "av_test"), s is not null ? int.Parse(s[6..]) : 4);
            }

            if (args.Any(a => a == "--gpu-test"))
            {
                var sm = args.FirstOrDefault(a => a.StartsWith("--surface-mode=", StringComparison.OrdinalIgnoreCase));
                if (sm is not null) NvEncoder.SurfaceMode = int.Parse(sm[15..]);
                Console.WriteLine($"[0] surface-mode={NvEncoder.SurfaceMode}");
                return GpuTest.Run(2560, 1440, 180, Path.Combine(Paths.Captures, "gpu_test"));
            }

            // 阈值回归：拿历史 band 图重跑检测器，确认某档阈值既挡掉误判又不误杀真命中
            if (args.Any(a => a == "--band-check"))
            {
                var bcProfile = GameProfile.Load(GameProfile.ResolvePath(game));
                var bcEngine = new BannerEngine(bcProfile);
                var bcDir = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))?[6..]
                            ?? Paths.Captures;
                var thArg2 = args.FirstOrDefault(a => a.StartsWith("--threshold=", StringComparison.OrdinalIgnoreCase));
                var list = new List<float>();
                if (thArg2 is not null)
                    foreach (var part in thArg2[12..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (float.TryParse(part, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v2)) list.Add(v2);
                return BandCheck.Run(bcEngine, bcDir, list.ToArray());
            }

            // 横幅 OCR 回归：拿真实对局录下的 hit_*.bmp 重跑分类，确认种类判定正确、去重有效
            if (args.Any(a => a == "--ocr-check"))
            {
                var ocrDir = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))?[6..]
                             ?? Paths.Captures;
                return OcrCheck.Run(ocrDir);
            }

            var profile = GameProfile.Load(GameProfile.ResolvePath(game));
            var engine = new BannerEngine(profile);

            // 配置：--config= 指定，否则找数据目录下的 config.json（配额 + 阈值共用这一个文件）
            var cfgPath = args.FirstOrDefault(a => a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))?[9..]
                          ?? Paths.ConfigFile;
            var quota = QuotaPolicy.Load(cfgPath);

            // 检测阈值：命令行 --threshold= 优先（调试/回归用），否则读 config.json
            var detect = DetectPolicy.Load(cfgPath);
            var thArg = args.FirstOrDefault(a => a.StartsWith("--threshold=", StringComparison.OrdinalIgnoreCase));
            if (thArg is not null && float.TryParse(thArg[12..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var thv))
                detect = DetectPolicy.Clamp(new DetectPolicy { Threshold = thv });
            engine.Apply(detect);

            // 录制参数（码率 + 音轨）：config.json 里的值先当底，命令行只覆盖它给过的那一项，
            // 然后把这个「最终生效值」同时喂给采集器和托盘菜单 —— 两边显示必须一致。
            var capture = CapturePolicy.Load(cfgPath);
            if (bitrateGiven && bitrate > 0) capture.BitrateMbps = Math.Max(1, bitrate / 1_000_000);
            if (!audio) capture.Audio = false;
            capture = CapturePolicy.Clamp(capture);
            bitrate = capture.BitrateBps;
            audio = capture.Audio;
            // 成片目录：必须在 Capture 构造之前定下来（Capture.ClipDir 的默认值读 Paths.Clips）
            Paths.SetClipsRoot(capture.ClipsDir);
            Console.WriteLine($"[2d] 画质码率 {capture.BitrateMbps}Mbps（config.json 的 BitrateMbps，托盘「画质码率」可调）");
            Console.WriteLine($"[2e] 音轨 {(capture.Audio ? "开" : "关")}（config.json 的 Audio，托盘「音轨」可调）");
            Console.WriteLine($"[2f] 成片目录 {Paths.Clips}{(capture.ClipsDir.Length == 0 ? "（默认）" : "")}（config.json 的 ClipsDir，托盘「片段目录」可改）");

            return offline is not null
                ? Offline(engine, offline.Length == 0 ? Path.Combine(Paths.DataRoot, "probe", "bands.bin") : offline)
                : Live(engine, dump, title ?? (profile.WindowTitles.Length > 0 ? profile.WindowTitles[0] : profile.Name), logPath, fps, bitrate, encode, gop, fakeHit, duration, audio, beep, quota, tray, ui, traySelfTest, detect, cfgPath, ocr,
                       int.TryParse(args.FirstOrDefault(a => a.StartsWith("--fake-repeat=", StringComparison.OrdinalIgnoreCase))?[14..], out var fr) && fr > 0 ? fr : 1,
                       int.TryParse(args.FirstOrDefault(a => a.StartsWith("--fake-gap=", StringComparison.OrdinalIgnoreCase))?[11..], out var fg) && fg > 0 ? fg : 700,
                       capture);
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

    /// <summary>
    /// 库的命令行入口：`--lib-list` 打印现状，`--quota-run` 真跑一次清理。
    /// 默认库目录是 captures/clips，可用 `--dir=` 改；`--config=` 指定配额配置。
    /// </summary>
    private static int LibCli(string[] args)
    {
        bool doRun = args.Any(a => a == "--quota-run");
        var dir = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))?[6..]
                  ?? Paths.Clips;
        var cfgPath = args.FirstOrDefault(a => a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))?[9..]
                      ?? Paths.ConfigFile;
        var policy = QuotaPolicy.Load(cfgPath);
        var lib = new ClipLibrary(dir);

        var clips = lib.Scan();
        long total = clips.Sum(c => c.Bytes);
        Console.WriteLine($"[1] 库 {dir}");
        Console.WriteLine($"    {clips.Count} 片，合计 {QuotaReport.Mb(total)}；" +
                          $"配额 {policy.QuotaGb}GB，保留 {(policy.RetainDays == 0 ? "永久" : policy.RetainDays + " 天")}，" +
                          $"水位 {policy.WatermarkGb}GB");
        Console.WriteLine($"    所在盘可用 {QuotaReport.Mb(QuotaEnforcer.FreeBytes(dir))}");

        if (clips.Count > 0)
        {
            Console.WriteLine("[2] 列表（旧→新）");
            foreach (var c in clips)
            {
                var kind = c.Kind switch { ClipKind.Kill => "击杀", ClipKind.Knocked => "被击倒", _ => "击倒" };
                Console.WriteLine($"    {c.CreatedLocal:MM-dd HH:mm:ss}  {QuotaReport.Mb(c.Bytes),8}  {kind,-6}  {c.Score:F2}  {c.Name}");
            }
        }

        if (QuotaEnforcer.BelowWatermark(dir, policy))
            Console.WriteLine($"[!] 可用空间已低于水位 {policy.WatermarkGb}GB —— 真跑会暂停录制");

        if (doRun)
        {
            var rep = QuotaEnforcer.Run(lib, policy);
            Console.WriteLine($"[3] 清理: {rep}");
            foreach (var n in rep.DeletedByAge) Console.WriteLine($"    过期删除 {n}");
            foreach (var n in rep.DeletedByQuota) Console.WriteLine($"    超配删除 {n}");
        }
        return 0;
    }

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

    /// <summary>
    /// 单独打开回顾库窗口（不启动录制）。调试 UI 用，也算一个给用户的正经入口。
    /// </summary>
    private static int LibUi(string[] args)
    {
        var dir = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))?[6..]
                  ?? Paths.Clips;
        var cfgPath = args.FirstOrDefault(a => a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))?[9..]
                      ?? Paths.ConfigFile;
        var policy = QuotaPolicy.Load(cfgPath);
        var shot = args.FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))?[7..];

        // DPI 和视觉样式必须在第一个窗口创建之前设置，否则只有第一屏是对的。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine($"[1] 回顾库 {dir}");
        var form = new LibraryForm(new ClipLibrary(dir), policy);

        if (shot is null)
        {
            Application.Run(form);
            return 0;
        }

        var menuField = args.FirstOrDefault(a => a.StartsWith("--shot-menu=", StringComparison.OrdinalIgnoreCase))?[12..];
        // 菜单有 330ms 的滑出动画：想抓中间帧就传 --shot-menu-delay=120
        var delayArg = args.FirstOrDefault(a => a.StartsWith("--shot-menu-delay=", StringComparison.OrdinalIgnoreCase))?[18..];
        var menuDelay = int.TryParse(delayArg, out var d) ? d : 250;
        return Shot(form, shot, menuField, menuDelay);
    }

    /// <summary>
    /// 把回顾库窗口抓成 PNG。自检（--ui-test）只验得出逻辑对不对，样子还得看图。
    /// 用 PrintWindow(PW_RENDERFULLCONTENT) 而不是屏幕截图：窗口被别的窗口盖住、
    /// 或者跑在最小化的桌面上时，屏幕截图会是黑的。
    /// </summary>
    private static int Shot(Form form, string file, string? menuField = null, int menuDelay = 250)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(40, 40);
        form.Show();

        // 缩略图和视频元数据是异步补的，等它们画上去再抓，不然抓到的是空壳。
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        // 下拉菜单是独立的顶层窗口，PrintWindow 抓主窗口时抓不到，单独抓一张。
        // 有些胶囊外面还包了一层 Panel（带圆角/阴影的那种），所以字段可能不是 PillSelect 本身：
        // 是 Panel 就往下找第一个 PillSelect 孩子。
        if (menuField is not null && UiTest.Field(form, menuField) is { } host
            && (host as PillSelect ?? (host as Control)?.Controls.OfType<PillSelect>().FirstOrDefault()) is { } sel
            && sel.ShowMenu() is { } menu)
        {
            Application.DoEvents();
            Thread.Sleep(menuDelay);
            Application.DoEvents();

            // 调试用：APEX_SHOT_SCREEN=路径 时顺带截一块真实屏幕，
            // PrintWindow 只拿得到窗口内容，拿不到窗口在屏幕上的位置。
            var screenPath = Environment.GetEnvironmentVariable("APEX_SHOT_SCREEN");
            if (!string.IsNullOrEmpty(screenPath))
            {
                var r = Rectangle.Union(menu.Bounds, form.Bounds);
                r.Intersect(Screen.PrimaryScreen!.Bounds);
                using var snap = new Bitmap(r.Width, r.Height);
                using (var g = Graphics.FromImage(snap))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);
                snap.Save(screenPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"[4] 屏幕截图 {screenPath}  {r}");
            }
            using (var mb = new Bitmap(Math.Max(1, menu.Width), Math.Max(1, menu.Height)))
            {
                Native.PrintWindowTo(menu.Handle, mb);
                var mp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!,
                                      Path.GetFileNameWithoutExtension(file) + "_menu.png");
                mb.Save(mp, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"[3] 菜单截图 {mp}  {mb.Width}x{mb.Height}");
            }
            menu.Close();
        }

        // 调试用：APEX_SHOT_TIP=文本 时把悬停气泡单独抓一张，验证圆角和配色。
        if (Environment.GetEnvironmentVariable("APEX_SHOT_TIP") is { Length: > 0 } tipText)
        {
            using var tip = new HoverTip();
            tip.ShowFor(form, tipText);
            Application.DoEvents();
            Thread.Sleep(60);
            Application.DoEvents();

            using var tb = new Bitmap(Math.Max(1, tip.Width), Math.Max(1, tip.Height));
            Native.PrintWindowTo(tip.Handle, tb);
            var tp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!,
                                  Path.GetFileNameWithoutExtension(file) + "_tip.png");
            tb.Save(tp, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"[5] 气泡截图 {tp}  {tb.Width}x{tb.Height}");
            tip.HideTip();
        }

        var sz = form.Size;
        using var bmp = new Bitmap(sz.Width, sz.Height);
        bool ok = Native.PrintWindowTo(form.Handle, bmp);
        if (!ok)
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, sz.Width, sz.Height));
            Console.WriteLine("[2] PrintWindow 失败，退回 DrawToBitmap");
        }

        var full = Path.GetFullPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        bmp.Save(full, System.Drawing.Imaging.ImageFormat.Png);

        var stat = UiTest.Stat(bmp);
        Console.WriteLine($"[2] 截图 {full}  {bmp.Width}x{bmp.Height}  标准差 {stat.Std:F1}  色数 {stat.Unique}");

        form.Close();
        Application.DoEvents();
        return ok ? 0 : 0;
    }

    /// <summary>只读元数据：验证 Mp4Probe 对已有文件的判断。</summary>
    private static int Probe(string path)
    {
        var m = Mp4Probe.Read(path);
        Console.WriteLine($"[1] {path}");
        Console.WriteLine($"    时长 {Mp4Probe.Fmt(m.Seconds)}（{m.Seconds:F3}s）  分辨率 {(m.Valid ? $"{m.Width}x{m.Height}" : "—")}  " +
                          $"音轨 {(m.HasAudio ? "有" : "无")}  编码 {m.Codec}");
        return m.Valid ? 0 : 5;
    }

    /// <summary>
    /// 托盘常驻模式：没有结束时间就一直录，退出靠托盘菜单。
    /// 双击 exe 时的默认路径就是这里。
    ///
    /// WinForms 要求 STA —— 但主线程已经被采集/COM 用掉了，所以单独起一个 STA 线程跑
    /// Application.Run，主线程 join 住。这样不用给 Main 加 [STAThread] 去动已有的 COM 路径。
    /// </summary>
    private static int RunTray(GameProfile prof, QuotaPolicy quota, DetectPolicy detect, CapturePolicy capture, string cfgPath, string title, int fps, bool ui, bool beep, int fakeHit = 0, int selfTest = 0, bool ocr = true)
    {
        // 单实例：托盘常驻意味着用户完全可能双击好几次（黑框一闪就没，很容易以为没起来）。
        // 多开会各自采一份、抢同一个 captures\ring 目录，几小时后配额和分片全乱。
        // 用 Local\ 前缀：按登录会话隔离，同一台机器的另一个用户互不影响。
        using var single = new Mutex(initiallyOwned: true, @"Local\ApexHighlights.Tray", out bool isFirst);
        if (!isFirst && selfTest == 0)
        {
            Console.WriteLine("[!] 已经有一个 ApexHighlights 在托盘里跑着了，这次不重复启动。");
            MessageBox.Show(
                "ApexHighlights 已经在运行了。\n\n" +
                "看任务栏右下角的托盘图标（可能要点开 ^ 展开）——\n" +
                "右键它就能打开回顾库、暂停录制或退出。",
                "ApexHighlights", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // 这些启动行也用 Log.Write：托盘模式控制台会被藏掉，只有写进日志才能事后核对
        Log.Write($"[1] 托盘常驻：目标 \"{title}\"，{fps}fps {capture.BitrateMbps}Mbps，音轨{(capture.Audio ? "开" : "关")}，提示音{(beep ? "开" : "关")}，横幅分类{(ocr ? "开" : "关")}");
        Log.Write($"[2] 配额 {quota.QuotaGb}GB / 保留 {(quota.RetainDays == 0 ? "永久" : quota.RetainDays + " 天")} / 水位 {quota.WatermarkGb}GB");
        var thShown = detect.Threshold > 0 ? detect.Threshold : prof.Templates[0].Threshold;
        Log.Write($"[2b] 命中阈值 {thShown:F2}（config.json 的 Threshold 字段，托盘菜单「检测灵敏度」里可调）");
        Log.Write($"[2c] 合并窗口 {(detect.MergeSec == 0 ? "关（每次命中各出一段）" : detect.MergeSec + "s")}" +
                  "（config.json 的 MergeSec 字段，托盘菜单「合并窗口」里可调）");
        Log.Write($"[2d] 画质码率 {capture.BitrateMbps}Mbps（config.json 的 BitrateMbps 字段，托盘菜单「画质码率」里可调）");
        Log.Write($"[2e] 音轨 {(capture.Audio ? "开（系统回环，不碰麦克风）" : "关（片段无声）")}（config.json 的 Audio 字段，托盘菜单「音轨」里可调）");
        Log.Write($"[2f] 成片目录 {Paths.Clips}{(capture.ClipsDir.Length == 0 ? "（默认）" : "（自定义）")}（config.json 的 ClipsDir 字段，托盘菜单「片段目录」里可改）");
        Log.Write("[3] 右键托盘图标可以打开回顾库；关掉这个窗口也不影响录制。");

        // 托盘模式下把控制台藏掉：双击 exe 时那个黑窗口没必要一直开着。
        // 自检模式（--tray-test）例外 —— 那些输出就是要给人看的。
        if (selfTest == 0) Native.HideConsole();

        int exit = 0;
        var t = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var app = new TrayApp(prof, quota, detect, capture, cfgPath, title, fps, ui, beep, fakeHit, selfTest, ocr);
                Application.Run(app);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[X] 托盘启动失败: " + ex.Message);
                exit = 1;
            }
        })
        { IsBackground = false, Name = "ui" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Console.WriteLine("[end] 已退出。");
        return exit;
    }

    private static int Live(BannerEngine engine, int dumpAtSec, string title, string? logPath, int fps, int bitrate, bool encode, int gop, int fakeHit, int durationSec, bool audio = true, bool beep = true, QuotaPolicy? quotaPolicy = null, bool tray = true, bool ui = false, int traySelfTest = 0, DetectPolicy? detectPolicy = null, string? cfgPath = null, bool ocr = true, int fakeRepeat = 1, int fakeGap = 700, CapturePolicy? capturePolicy = null)
    {
        var prof = engine.Profile;
        var quota = quotaPolicy ?? new QuotaPolicy();
        var detect = detectPolicy ?? new DetectPolicy { Threshold = 0 };
        var cfg = cfgPath ?? Paths.ConfigFile;
        // 托盘菜单要显示/落盘这些值，所以得把「最终生效值」原样带进去；
        // 别的调用方（测试入口）不传就当从 bitrate/audio 参数推出来。
        var capture = capturePolicy ?? new CapturePolicy { BitrateMbps = Math.Max(1, bitrate / 1_000_000), Audio = audio };

        // 日志必须在托盘分支之前开 —— 托盘模式会把控制台藏起来，
        // 要是跟以前一样放在分支后面，托盘模式就一个字节都不写，
        // 出事（比如「这一局一片都没录到」）事后完全无从查起。
        Log.Init(logPath);
        void Out(string s) => Log.Write(s);
        Out($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}  game={prof.Id}  target=\"{title}\"  mode={(tray && encode && durationSec == 0 ? "tray" : "console")}");

        // 托盘模式：没有控制台窗口可看，走 ApplicationContext 常驻
        if (tray && encode && durationSec == 0)
        {
            try { return RunTray(prof, quota, detect, capture, cfg, title, fps, ui, beep, fakeHit, traySelfTest, ocr); }
            finally { Log.Close(); }
        }

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
            cap.FakeHitRepeat = fakeRepeat > 0 ? fakeRepeat : 1;
            cap.FakeHitGapMs = fakeGap > 0 ? fakeGap : 700;
            cap.DumpAtSec = dumpAtSec;
            cap.AudioEnabled = audio;
            cap.BeepOnSave = beep;
            cap.OcrEnabled = ocr;
            cap.Quota = quota;
            cap.MergeMs = detect.MergeSec * 1000;
            cap.EnableEncoding(90);
            Out($"[2b] 编码 {cap.Width}x{cap.Height} @{fps}fps {bitrate / 1_000_000}Mbps，环形 90s，音轨{(audio ? "开" : "关")}，提示音{(beep ? "开" : "关")}，横幅分类{(ocr ? "开" : "关")}，合并窗口{(detect.MergeSec == 0 ? "关" : detect.MergeSec + "s")}");
            Out($"[2c] 配额 {quota.QuotaGb}GB / 保留 {(quota.RetainDays == 0 ? "永久" : quota.RetainDays + " 天")} / 水位 {quota.WatermarkGb}GB / 检查间隔 {quota.CheckMinutes} 分钟");

            // 启动时先清一次（spec §5 的清理触发点之一），顺带把水位状态摸清楚
            var rep = cap.RunQuotaCheck();
            if (rep is not null) Out($"[2d] 库 {cap.ClipDir}：{rep}");
        }
        Out($"[2] 采集已启动 {cap.Width}x{cap.Height}；条带 {prof.Band.W}x{prof.Band.H}@{prof.Band.X},{prof.Band.Y}；采集 {fps}fps / 检测 10fps");
        Out($"[3] 模板 {string.Join(",", prof.Templates.Select(t => $"{t.Id}({t.W}x{t.H}@{t.Threshold:F2})"))}；实际阈值 {cap.Threshold:F2}；命中存条带到 {cap.OutDir}");
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
