namespace ApexHighlights;

/// <summary>
/// 配额/保留期自检：在临时目录里伪造一批片段文件，验证
///   ① 过期删除  ② 超配按「被击倒 &gt; 击倒 &gt; 击杀」淘汰  ③ 文件名解析  ④ 水位判定
/// 不碰真实片段目录。
/// </summary>
internal static class QuotaTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ahq_" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, "clips");
        Directory.CreateDirectory(dir);
        int fail = 0;

        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
            if (!ok) fail++;
        }

        try
        {
            var now = DateTime.Now;
            // 文件名里只有 MMdd-HHmmss，所以「过期」只能用跨年样本来造：把基准日推到明年
            // 这样今年的片段全都早于 cutoff，而手工造的老日期也必然过期。
            void Make(string name, int kb)
            {
                File.WriteAllBytes(Path.Combine(dir, name), new byte[kb * 1024]);
            }

            // 5 个新片段（不该被删）+ 3 个超期片段
            Make("apex_0101-120000_0.90.mp4", 100);
            Make("apex_0101-120100_0.91.mp4", 100);
            Make("apex_0101-120200_0.92.mp4", 100);
            Make("apex_0101-120300_0.93.mp4", 100);

            var lib = new ClipLibrary(dir);
            var all = lib.Scan();

            Console.WriteLine($"[1] 解析 {all.Count} 片");
            Check(all.Count == 4, "四个文件都能识别");
            Check(all[0].GameId == "apex", "gameId=apex");
            Check(Math.Abs(all[0].Score - 0.90f) < 0.001f, "score 解析正确");
            Check(all[0].Bytes == 100 * 1024, "体积正确");
            Check(all.All(c => c.Kind == ClipKind.Knock), "缺省种类=击倒");

            // 种类后缀
            Make("apex_k_0101-130000_0.95.mp4", 100);
            var k = ClipLibrary.Parse(Path.Combine(dir, "apex_k_0101-130000_0.95.mp4"));
            Check(k is not null && k.Kind == ClipKind.Kill, "apex_k_... 识别为击杀");
            Make("apex_kd_0101-130100_0.80.mp4", 100);
            var kd = ClipLibrary.Parse(Path.Combine(dir, "apex_kd_0101-130100_0.80.mp4"));
            Check(kd is not null && kd.Kind == ClipKind.Knocked, "apex_kd_... 识别为被击倒");

            // OCR 分类新增的四个种类后缀
            foreach (var (tok, want, name) in new (string, ClipKind, string)[]
                     {
                         ("rk", ClipKind.ReKnock, "再次击倒"),
                         ("ak", ClipKind.AssistKnock, "助攻击倒"),
                         ("el", ClipKind.Elim, "小队淘汰"),
                         ("ae", ClipKind.AssistElim, "助攻淘汰"),
                     })
            {
                var fn = $"apex_{tok}_0101-131000_0.88.mp4";
                Make(fn, 100);
                var c = ClipLibrary.Parse(Path.Combine(dir, fn));
                Check(c is not null && c.Kind == want, $"apex_{tok}_... 识别为{name}");
            }

            // ② 超配淘汰：配额压到 200KB，只剩击杀/被击倒该优先被删
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Make("apex_0101-120000_0.90.mp4", 100);   // 击倒
            Make("apex_k_0101-120100_0.91.mp4", 100); // 击杀
            Make("apex_kd_0101-120200_0.92.mp4", 100);// 被击倒
            Make("apex_0101-120300_0.93.mp4", 100);   // 击倒

            // QuotaGb 是整数 GB，小配额不现实，所以「不限」用 0 表示，超配用一个 1GB 配额 + 稀疏大文件触发。
            var p = new QuotaPolicy { QuotaGb = 0, RetainDays = 0, WatermarkGb = 0 }; // 0 = 不限
            var noLimit = QuotaEnforcer.Run(new ClipLibrary(dir), p, now);
            Check(noLimit.DeletedTotal == 0, "配额=0 表示不限，一个都不删");

            // 用 1GB 配额 + 1.2GB 假文件触发超配；稀疏文件不真占盘
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            void Sparse(string name, long bytes)
            {
                using var fs = File.Create(Path.Combine(dir, name));
                fs.SetLength(bytes);
            }
            Sparse("apex_kd_0101-120000_0.90.mp4", 300L << 20);  // 被击倒，最先牺牲
            Sparse("apex_0101-120100_0.91.mp4", 300L << 20);     // 击倒
            Sparse("apex_k_0101-120200_0.92.mp4", 600L << 20);   // 击杀，最后才删

            var p1 = new QuotaPolicy { QuotaGb = 1, RetainDays = 0, WatermarkGb = 0 };
            var r1 = QuotaEnforcer.Run(new ClipLibrary(dir), p1, now);
            Console.WriteLine($"[2] 超配: {r1}");
            Check(r1.BytesFreed > 0, "确实删了东西");
            Check(r1.AfterBytes <= p1.QuotaBytes, $"删到配额内（{QuotaReport.Mb(r1.AfterBytes)} ≤ 1GB）");
            Check(r1.DeletedByQuota.Count > 0 && r1.DeletedByQuota[0].Contains("_kd_"),
                "第一个被删的是 _kd_（被击倒），符合淘汰优先级");
            Check(!Directory.EnumerateFiles(dir).Any(f => f.Contains("_kd_")), "_kd_ 文件已不存在");

            // ① 过期删除
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Make("apex_0101-120000_0.90.mp4", 10);
            Make("apex_0101-120100_0.91.mp4", 10);
            // 把文件时间改成 40 天前，同时构造「明年」的基准时间 → 全过期
            var p2 = new QuotaPolicy { QuotaGb = 0, RetainDays = 30, WatermarkGb = 0 };
            var future = now.AddDays(40);
            var r2 = QuotaEnforcer.Run(new ClipLibrary(dir), p2, future);
            Console.WriteLine($"[3] 过期: {r2}");
            Check(r2.DeletedByAge.Count == 2, $"40 天后回顾 30 天保留期，2 片全过期（实删 {r2.DeletedByAge.Count}）");
            Check(Directory.EnumerateFiles(dir).Count() == 0, "目录已空");

            // ③ 保留期 0 = 永久
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Make("apex_0101-120000_0.90.mp4", 10);
            var p3 = new QuotaPolicy { QuotaGb = 0, RetainDays = 0, WatermarkGb = 0 };
            var r3 = QuotaEnforcer.Run(new ClipLibrary(dir), p3, now.AddYears(5));
            Check(r3.DeletedTotal == 0, "RetainDays=0 永不按时间删");

            // ④ 水位
            var free = QuotaEnforcer.FreeBytes(dir);
            Console.WriteLine($"[4] 临时盘可用 {QuotaReport.Mb(free)}");
            Check(free > 0, "能读到可用空间");
            Check(!QuotaEnforcer.BelowWatermark(dir, new QuotaPolicy { WatermarkGb = 0 }), "水位=0 永不触发");
            Check(!QuotaEnforcer.BelowWatermark(dir, new QuotaPolicy { WatermarkGb = 1 }), "1GB 水位当前不触发");

            // ⑤ 配置往返
            var cfg = Path.Combine(root, "config.json");
            var p4 = new QuotaPolicy { QuotaGb = 12, RetainDays = 7, WatermarkGb = 3, CheckMinutes = 30 };
            p4.Save(cfg);
            var back = QuotaPolicy.Load(cfg);
            Check(back.QuotaGb == 12 && back.RetainDays == 7 && back.WatermarkGb == 3 && back.CheckMinutes == 30,
                "配置存读往返一致");
            var bad = Path.Combine(root, "bad.json");
            File.WriteAllText(bad, "{ this is not json");
            var def = QuotaPolicy.Load(bad);
            Check(def.QuotaGb == 40 && def.RetainDays == 30 && def.WatermarkGb == 2, "坏配置退回默认 40GB/30天/2GB");

            // ⑥ config.json 里不止配额：别的字段（灵敏度/码率/合并窗口）不能被配额的 Save 抹掉。
            // 托盘里改配额走的就是这条路，以前整份覆盖会把这些全清回默认。
            var shared = Path.Combine(root, "shared.json");
            File.WriteAllText(shared, "{\n  \"Threshold\": 0.66,\n  \"BitrateMbps\": 40,\n  \"MergeSec\": 8\n}");
            new QuotaPolicy { QuotaGb = 15, RetainDays = 5 }.Save(shared);
            var json = File.ReadAllText(shared);
            Check(json.Contains("0.66") && json.Contains("BitrateMbps") && json.Contains("MergeSec"),
                "配额 Save 只写自己四个键，灵敏度/码率/合并窗口都留着");
            Check(json.Contains("\"QuotaGb\": 15") && json.Contains("\"RetainDays\": 5"), "新配额与保留天数已写入");
            var merged = QuotaPolicy.Load(shared);
            Check(merged.QuotaGb == 15 && merged.RetainDays == 5, "合并写回后仍能读回来");

            // ⑦ 空跑（dryRun）只算账不动文件：托盘改配额前要能告诉用户「删几片」再问
            var dryDir = Path.Combine(root, "dry");
            Directory.CreateDirectory(dryDir);
            File.WriteAllBytes(Path.Combine(dryDir, "apex_0101-120000_0.90.mp4"), new byte[20 * 1024]);
            File.WriteAllBytes(Path.Combine(dryDir, "apex_0101-130000_0.80.mp4"), new byte[20 * 1024]);
            var dry = QuotaEnforcer.Run(new ClipLibrary(dryDir), new QuotaPolicy { QuotaGb = 1, RetainDays = 30, WatermarkGb = 0 },
                                        now.AddDays(60), dryRun: true);
            Check(dry.DeletedByAge.Count == 2, $"空跑算出该删 2 片（算出 {dry.DeletedByAge.Count}）");
            Check(Directory.EnumerateFiles(dryDir).Count() == 2, "空跑一个文件都没删");
            Check(dry.Kept == 0 && dry.AfterBytes == 0, "空跑也给出「删完的账」（保留 0 片）");
            var real = QuotaEnforcer.Run(new ClipLibrary(dryDir), new QuotaPolicy { QuotaGb = 1, RetainDays = 30, WatermarkGb = 0 },
                                         now.AddDays(60));
            Check(real.DeletedTotal == 2 && Directory.EnumerateFiles(dryDir).Count() == 0, "真跑才删（2 片已清空）");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "[done] 配额自检全部通过" : $"[done] {fail} 项失败");
        return fail == 0 ? 0 : 9;
    }
}
