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

        // ---- 灵敏度下拉（WinForms，单独 STA 线程）----
        fail += ThresholdCheck(lib);

        // ---- 胶囊下拉：点一下开、再点一下收（用户报过收不回去）----
        fail += PillToggleCheck();

        // ---- 成片目录：能改、能记住、不冲掉别的配置 ----
        fail += ClipsDirCheck();

        // ---- 批量删除：勾选框、全选、按钮跟着勾选数走 ----
        fail += BatchCheck(lib);

        Console.WriteLine(fail == 0 ? "[ok] 界面层自检全过" : $"[!] {fail} 项失败");
        return fail == 0 ? 0 : 9;
    }

    private static int FilterChecks(List<ClipInfo> all)
    {
        int fail = 0;
        var now = DateTime.Now;

        // 判定只有一份（ClipQuery，窗口也用这一份）。这里不抄公式，只验下拉索引
        // 跑出来的数字和按字段直数的一致 —— 抄一遍公式的话两边一起错就查不出来。
        int Filtered(int kindIndex, int timeIndex, string? text)
            => ClipQuery.Apply(all, ClipQuery.FromUi(kindIndex, timeIndex, text, now)).Count;

        // 下拉现在是 ClipKinds.All 的投影，所以照着这张表数一遍；顺手验越界还有没有种类。
        for (int ix = 1; ix <= ClipKinds.All.Length; ix++)
        {
            var kind = ClipQuery.KindByIndex(ix)!.Value;
            var label = ClipKinds.Label(kind);
            int n = Filtered(ix, 0, null);
            Log(label, n);
            if (n != all.Count(c => c.Kind == kind))
            {
                Console.WriteLine($"    BAD 筛选「{label}」与按字段计数不一致");
                fail++;
            }
        }
        if (ClipQuery.KindByIndex(ClipKinds.All.Length + 1) is not null)
        { Console.WriteLine("    BAD 越界下标居然还能取到种类（列表和索引对不上）"); fail++; }
        else Console.WriteLine($"    ok  种类下拉 {ClipKinds.All.Length} 项（击杀/被击倒已从列表移除，BannerKind 判不出来）");
        var today = Filtered(0, 1, null);
        var w7 = Filtered(0, 2, null);
        var w30 = Filtered(0, 3, null);
        Console.WriteLine($"    今天 {today} 片，最近 7 天 {w7} 片，最近 30 天 {w30} 片");
        if (!(w30 >= w7 && w7 >= today)) { Console.WriteLine("    BAD 时间筛选不单调"); fail++; }

        var s = "apex";
        var byName = Filtered(0, 0, s);
        if (byName != all.Count) { Console.WriteLine($"    BAD 搜索 {s} 只命中 {byName}/{all.Count}"); fail++; }
        else Console.WriteLine($"    搜索 \"{s}\" 命中 {byName}/{all.Count}");

        if (Filtered(0, 0, "zzz_nothing") != 0) { Console.WriteLine("    BAD 无关搜索居然命中"); fail++; }
        if (Filtered(0, 0, "APEX") != all.Count) { Console.WriteLine("    BAD 搜索大小写不敏感没生效"); fail++; }

        void Log(string label, int n) => Console.WriteLine($"    {label} {n} 片");
        return fail;
    }

    /// <summary>
    /// 灵敏度的三种入口必须一致：外部改了 → 下拉跟着变；用户在下拉里选 → 回调发出去。
    /// 这个窗口平时只能靠手点，所以把「项数、选中项与数值的映射、回调次数」都用代码验一遍。
    /// WinForms 控件必须在 STA 线程上建，这里单开一个线程跑完再 join。
    /// </summary>
    private static int ThresholdCheck(ClipLibrary lib)
    {
        int fail = 0;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new LibraryForm(lib, new QuotaPolicy());
                var combo = (PillSelect)Field(form, "_thCombo")!;
                var label = (Label)Field(form, "_thLabel")!;
                _ = form.Handle;            // 建句柄，子控件才会真的初始化（不用 Show 出来闪一下）

                int want = DetectPolicy.Presets.Length + 1;
                if (combo.Items.Count != want) { Console.WriteLine($"    BAD 下拉 {combo.Items.Count} 项，应为 {want}"); fail++; }
                else Console.WriteLine($"    ok  下拉 {combo.Items.Count} 项（1 个「按模板自带」+ {DetectPolicy.Presets.Length} 个档位）");
                if (combo.Items.Count > 0 && (combo.Items[0] as string) != "按模板自带")
                { Console.WriteLine("    BAD 第一项不是「按模板自带」"); fail++; }

                var fired = new List<float>();
                form.OnThresholdChanged = v => fired.Add(v);

                form.SetThreshold(0.75f);
                int expectIdx = Array.FindIndex(DetectPolicy.Presets, v => Math.Abs(v - 0.75f) < 0.001f) + 1;
                if (combo.SelectedIndex != expectIdx)
                { Console.WriteLine($"    BAD 设 0.75 后选中 {combo.SelectedIndex}，应为 {expectIdx}"); fail++; }
                else Console.WriteLine($"    ok  设 0.75 → 选中第 {expectIdx} 项「{combo.Items[expectIdx]}」");
                // 标签只说“灵敏度”，数值由胶囊自己显示（不然同一行会出现两个 0.75）
                if (label.Text != "灵敏度")
                { Console.WriteLine($"    BAD 标签文字不对: {label.Text}"); fail++; }
                else if (combo.SelectedItem?.ToString() is not { } shown || !shown.Contains("0.75"))
                { Console.WriteLine($"    BAD 胶囊没显示新值: {combo.SelectedItem}"); fail++; }
                if (fired.Count != 0)
                { Console.WriteLine($"    BAD 外部 SetThreshold 不该回调，实际 {fired.Count} 次"); fail++; }
                else Console.WriteLine("    ok  外部设定不触发回调（不会把配置反写一遍）");

                int i60 = Array.FindIndex(DetectPolicy.Presets, v => Math.Abs(v - 0.60f) < 0.001f) + 1;
                combo.SelectedIndex = i60;
                if (fired.Count != 1 || Math.Abs(fired[0] - 0.60f) > 0.001f)
                { Console.WriteLine($"    BAD 选 0.60 回调是 [{string.Join(",", fired)}]"); fail++; }
                else Console.WriteLine($"    ok  选 0.60 → 回调发出 {fired[0]:F2}，form.Threshold={form.Threshold:F2}");

                combo.SelectedIndex = 0;
                if (fired.Count != 2 || Math.Abs(fired[1]) > 0.001f)
                { Console.WriteLine($"    BAD 选「按模板自带」回调是 [{string.Join(",", fired)}]"); fail++; }
                else Console.WriteLine("    ok  选「按模板自带」→ 回调发出 0.00");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    BAD 阈值下拉抛出 {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return fail;
    }

    /// <summary>
    /// 成片目录（回顾库文件夹）：config.json 的 ClipsDir 读得出来、写得回去、还能改 Paths。
    ///
    /// 用户 2026-10-29 要的是「自己选回顾目录放哪个文件夹」——这条链路最脆的一步是「保存」：
    /// CapturePolicy.Save 是局部改 JsonObject，万一写成整份序列化就会把阈值/配额顺手清掉。
    /// 所以这里专门盯 config.json 里别的键还在不在，并顺手验非法路径会退回默认。
    /// </summary>
    private static int ClipsDirCheck()
    {
        int fail = 0;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apex-clipsdir-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(tmp);
            var cfg = System.IO.Path.Combine(tmp, "config.json");
            File.WriteAllText(cfg, "{ \"Threshold\": 0.72, \"MergeSec\": 5, \"QuotaGb\": 33, \"RetainDays\": 11 }");

            var p = CapturePolicy.Load(cfg);
            if (p.ClipsDir.Length == 0) Console.WriteLine("    ok  老配置没有 ClipsDir → 默认位置（行为不变）");
            else { Console.WriteLine($"    BAD 老配置读出的 ClipsDir = {p.ClipsDir}"); fail++; }

            var want = System.IO.Path.Combine(tmp, "my-clips");
            p.ClipsDir = want + "\\";                    // 用户从资源管理器复制粘贴常带个尾巴
            var saved = p.Save(cfg);
            var back = CapturePolicy.Load(cfg);
            var text = File.ReadAllText(cfg);
            if (saved && string.Equals(back.ClipsDir, want, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"    ok  ClipsDir 存得住（读回 {back.ClipsDir}）");
            else { Console.WriteLine($"    BAD ClipsDir 读回 {back.ClipsDir}，应为 {want}"); fail++; }
            if (text.Contains("\"Threshold\"") && text.Contains("0.72") && text.Contains("\"QuotaGb\"") && text.Contains("33"))
                Console.WriteLine("    ok  改目录没冲掉阈值/配额");
            else { Console.WriteLine("    BAD 改目录把阈值/配额写丢了: " + text.Replace("\n", " ")); fail++; }

            Paths.SetClipsRoot(back.ClipsDir);
            if (string.Equals(Paths.Clips, want, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"    ok  Paths.Clips 跟着变 → {Paths.Clips}");
            else { Console.WriteLine($"    BAD Paths.Clips = {Paths.Clips}"); fail++; }
            Paths.SetClipsRoot(null);
            if (Paths.Clips == Paths.DefaultClips) Console.WriteLine("    ok  清空后回到默认位置");
            else { Console.WriteLine($"    BAD 清空后仍是 {Paths.Clips}"); fail++; }

            var broken = CapturePolicy.Clamp(new CapturePolicy { ClipsDir = "::::" });
            if (broken.ClipsDir.Length == 0) Console.WriteLine("    ok  写坏的路径退回默认（不让录制跟着崩）");
            else { Console.WriteLine($"    BAD 非法路径没被清掉: {broken.ClipsDir}"); fail++; }

            // 换目录时「把现有片段搬过去」那段：真搬两个假文件，同名的不许覆盖
            var src = System.IO.Path.Combine(tmp, "old");
            var dst = System.IO.Path.Combine(tmp, "new");
            Directory.CreateDirectory(src);
            File.WriteAllBytes(System.IO.Path.Combine(src, "a.mp4"), new byte[10]);
            File.WriteAllBytes(System.IO.Path.Combine(src, "b.mp4"), new byte[20]);
            Directory.CreateDirectory(dst);
            File.WriteAllBytes(System.IO.Path.Combine(dst, "b.mp4"), new byte[1]);   // 同名占位
            var (moved, skipped) = TrayApp.MoveClips(src, dst);
            var aThere = File.Exists(System.IO.Path.Combine(dst, "a.mp4"));
            var bKept = new FileInfo(System.IO.Path.Combine(dst, "b.mp4")).Length == 1;
            var bStillOld = File.Exists(System.IO.Path.Combine(src, "b.mp4"));
            if (moved == 1 && skipped == 1 && aThere && bKept && bStillOld)
                Console.WriteLine("    ok  迁移片段：搬走 1 个、同名 1 个留着不覆盖");
            else
            {
                Console.WriteLine($"    BAD 迁移片段 搬走={moved} 跳过={skipped} a存在={aThere} b未覆盖={bKept} b留原地={bStillOld}");
                fail++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    BAD 成片目录自检抛异常 {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        finally
        {
            Paths.SetClipsRoot(null);                    // 别把全局状态留给后面的检查
            try { Directory.Delete(tmp, true); } catch { }
        }
        return fail;
    }

    internal static object? Field(object o, string name) =>
        o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(o);

    /// <summary>
    /// 批量删除的勾选态：进批量态才有方框、勾了几片按钮就写几片、全选能一反一正切回零。
    ///
    /// 为什么必须代码验：勾选框是自绘的、点它的命中区也是手算的（x &lt; CheckSpan），
    /// 这东西没有控件实体，靠手点只会验「点中了」，验不出「勾选数和按钮文字对不对」。
    /// 另外勾选是按路径存的 —— 重建行之后下标全变，所以这里连 SetClips 之后的重建也一并盯着。
    /// </summary>
    private static int BatchCheck(ClipLibrary lib)
    {
        int fail = 0;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new LibraryForm(lib, new QuotaPolicy());
                // 必须真 Show 一次：Control.Visible 在没显示过的窗口上一律是 false（连子控件都算在内），
                // 只用 Handle 验不出「取消按钮有没有冒出来」。挪到屏幕外，别在桌面上闪一下。
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();
                var list = (ClipListView)Field(form, "_list")!;
                var del = (RoundButton)Field(form, "_btnDel")!;
                var cancel = (RoundButton)Field(form, "_btnCancel")!;
                form.Reload();

                if (list.SelectMode || del.Text != "删除" || cancel.Visible)
                { Console.WriteLine($"    BAD 平时不该有勾选框（mode={list.SelectMode} 按钮={del.Text} 取消={cancel.Visible}）"); fail++; }
                else Console.WriteLine("    ok  平时没有勾选框，按钮就是「删除」");

                list.SetSelectMode(true);
                Application.DoEvents();
                if (!list.SelectMode || del.Text != "删除选中" || del.Enabled || !cancel.Visible)
                { Console.WriteLine($"    BAD 进批量态不对：按钮={del.Text} 可用={del.Enabled} 取消={cancel.Visible}"); fail++; }
                else Console.WriteLine("    ok  点删除 → 进批量态，按钮变「删除选中」（一片没勾时不可用）");

                list.ToggleChecked(0);
                list.ToggleChecked(1);
                Application.DoEvents();
                if (list.CheckedCount != 2 || list.CheckedClips.Count != 2)
                { Console.WriteLine($"    BAD 勾两片后 Count={list.CheckedCount} Clips={list.CheckedClips.Count}"); fail++; }
                else if (del.Text != "删除选中（2）" || !del.Enabled)
                { Console.WriteLine($"    BAD 勾两片后按钮文字={del.Text} 可用={del.Enabled}"); fail++; }
                else Console.WriteLine($"    ok  勾两片 → 按钮「{del.Text}」可用，CheckedClips 命中 {list.CheckedClips[0].Name} / {list.CheckedClips[1].Name}");

                list.ToggleChecked(1);   // 同一行再点一次要取消
                if (list.CheckedCount != 1)
                { Console.WriteLine($"    BAD 再点同一行没取消，Count={list.CheckedCount}"); fail++; }
                else Console.WriteLine("    ok  同一行再点一次 → 取消勾选");

                list.ToggleAll();
                if (list.CheckedCount != list.RowCount)
                { Console.WriteLine($"    BAD 全选后 {list.CheckedCount}/{list.RowCount}"); fail++; }
                else Console.WriteLine($"    ok  全选 → {list.CheckedCount}/{list.RowCount} 片全勾上");

                list.ToggleAll();
                if (list.CheckedCount != 0 || del.Enabled)
                { Console.WriteLine($"    BAD 再点全选没清空，Count={list.CheckedCount} 可用={del.Enabled}"); fail++; }
                else Console.WriteLine("    ok  再点全选 → 全清空、按钮回灰");

                // 勾选按路径存：重建行（SetClips）之后旧的勾选必须清掉，不能按旧下标串到别的片上
                list.ToggleChecked(0);
                list.SetClips(lib.Scan());
                list.SetClips(lib.Scan());
                if (list.CheckedCount != 0)
                { Console.WriteLine($"    BAD 重建行后勾选没清掉，Count={list.CheckedCount}"); fail++; }
                else Console.WriteLine("    ok  重建行 → 勾选清空（按路径存，不会串到别的片）");

                list.SetSelectMode(false);
                Application.DoEvents();
                if (list.SelectMode || del.Text != "删除" || cancel.Visible)
                { Console.WriteLine($"    BAD 退出批量态没复原：按钮={del.Text} 取消={cancel.Visible}"); fail++; }
                else Console.WriteLine("    ok  退出批量态 → 按钮复原、取消消失");

                // 换库：SetClipsDir 之后列表要读新目录（空目录 → 空态文案）
                var empty = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apex-batch-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(empty);
                form.SetClipsDir(empty);
                var countLabel = (Label)Field(form, "_count")!;
                if (list.RowCount != 0 || !countLabel.Text.Contains("还没有片段"))
                { Console.WriteLine($"    BAD 换到空目录后 RowCount={list.RowCount} 计数=\"{countLabel.Text}\""); fail++; }
                else Console.WriteLine("    ok  换库 → 列表读新目录、空目录显示「还没有片段」");
                try { Directory.Delete(empty, true); } catch { }
                form.Hide();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    BAD 批量勾选抛出 {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return fail;
    }

    /// <summary>
    /// 胶囊下拉的开关：点一下弹出，弹出期间再点一下只收起、不再弹第二个。
    ///
    /// 这条是用户直接报的（「弹出来以后点那个胶囊第二下就该收起来」）。原来的写法是
    /// ShowMenu 里 `if (_open) return null`，第二下什么都不做；而且菜单 AutoClose 会
    /// 先把那一下点击吞掉，收起动画根本没机会跑。现在把菜单的关闭权收回到自己手里，
    /// 所以必须有自检盯着这条路径 —— 靠手点是验不出来的。
    /// </summary>
    private static int PillToggleCheck()
    {
        int fail = 0;
        var t = new Thread(() =>
        {
            ContextMenuStrip? first = null;
            try
            {
                // 窗口挪到屏幕外：自检不该在用户桌面闪一个对话框
                using var form = new Form
                {
                    Size = new Size(420, 220),
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-4000, -4000),
                    ShowInTaskbar = false,
                };
                var pill = new PillSelect { Location = new Point(20, 20), Width = 180 };
                pill.Items.AddRange(new object[] { "0.65 · 默认推荐", "0.70 · 默认推荐" });
                pill.SelectedIndex = 0;
                form.Controls.Add(pill);
                form.Show();
                Application.DoEvents();

                first = pill.ShowMenu();
                if (first is null || !pill.IsOpen)
                { Console.WriteLine("    BAD 第一次点击没弹出菜单"); fail++; }
                else Console.WriteLine($"    ok  点一下 → 菜单弹出 {first.Width}x{first.Height}");

                var second = pill.ShowMenu();
                if (second is not null) { Console.WriteLine("    BAD 第二下又弹了一个菜单"); fail++; }
                else Console.WriteLine($"    ok  再点一下 → 没有新菜单（收起中 IsOpen={pill.IsOpen}）");

                // 动画靠 Timer，得自己泵消息；2 秒还没关就是没收掉
                var until = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < until && first is { IsDisposed: false, Visible: true })
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }
                if (first is { IsDisposed: false, Visible: true })
                { Console.WriteLine("    BAD 收起动画跑完菜单还开着（第二下没关掉）"); fail++; }
                else if (pill.IsOpen) { Console.WriteLine("    BAD 菜单关了但 IsOpen 还是 true"); fail++; }
                else Console.WriteLine("    ok  收起动画跑完 → 菜单已关、胶囊回到关闭态");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    BAD 胶囊下拉自检抛出 {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
            finally
            {
                try { if (first is { IsDisposed: false }) first.Close(); } catch { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(10_000);
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

    internal readonly record struct Px(double Mean, double Std, int Unique);

    /// <summary>缩略图的像素统计：全黑/全灰的图标准差接近 0，能一眼看出没取到画面。</summary>
    internal static Px Stat(Bitmap bmp)
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
