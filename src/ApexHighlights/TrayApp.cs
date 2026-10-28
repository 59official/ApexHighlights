using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ApexHighlights;

/// <summary>
/// 托盘常驻。录制在后台线程跑，托盘图标就是它的唯一界面：
/// 状态看 tooltip，操作走右键菜单，一次都没弹过窗口（用户要的：不弹窗）。
///
/// 线程约定：Capture 的 Detected 回调和定时器都可能不在 UI 线程，
/// 所有碰 NotifyIcon / 菜单的代码都经 _marshal.BeginInvoke 绕回 UI 线程。
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly GameProfile _profile;
    private readonly QuotaPolicy _policy;
    private readonly DetectPolicy _detect;
    private readonly string _cfgPath;
    private readonly NotifyIcon _icon;
    private readonly Form _marshal = new();      // 只用来做线程封送，从没显示过
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 5000 };

    private Capture? _cap;
    private LibraryForm? _libForm;
    private readonly ToolStripMenuItem _pauseItem = new("暂停录制");
    private readonly ToolStripMenuItem _statusItem = new("启动中…") { Enabled = false };
    private readonly ToolStripMenuItem _libraryItem = new("打开回顾库");

    private int _fps;
    private int _bitrate;
    private string _title;
    private readonly bool _openUiFirst;
    private readonly bool _audio;
    private readonly bool _beep;
    private readonly int _fakeHit;
    private readonly int _selfTest;
    private System.Windows.Forms.Timer? _auto;
    private System.Windows.Forms.Timer? _wait;

    /// <summary>连续多少次状态刷新都找不到游戏窗口。用于区分「切走了」和「真退出了」。</summary>
    private int _missingTicks;

    public TrayApp(GameProfile profile, QuotaPolicy policy, DetectPolicy detect, string cfgPath, string title, int fps, int bitrate, bool openUiFirst, bool audio, bool beep, int fakeHit = 0, int selfTest = 0)
    {
        _profile = profile;
        _policy = policy;
        _detect = detect;
        _cfgPath = cfgPath;
        _title = title;
        _fps = fps;
        _bitrate = bitrate;
        _openUiFirst = openUiFirst;
        _audio = audio;
        _beep = beep;
        _fakeHit = fakeHit;
        _selfTest = selfTest;

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };
        _statusItem.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        _libraryItem.Click += (_, _) => ShowLibrary();
        menu.Items.Add(_libraryItem);
        var reveal = new ToolStripMenuItem("打开片段目录");
        reveal.Click += (_, _) => Reveal(null);
        menu.Items.Add(reveal);
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem.Click += (_, _) => TogglePause();
        menu.Items.Add(_pauseItem);
        var config = new ToolStripMenuItem("打开配置");
        config.Click += (_, _) => OpenConfig();
        menu.Items.Add(config);
        menu.Items.Add(BuildSensitivityMenu());
        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("退出");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Shared,
            Text = "ApexHighlights 启动中…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowLibrary();

        // 强制建句柄：CreateControl() 对没显示的 Form 是空操作（它先检查 Visible），
        // 句柄不存在时 BeginInvoke 会抛 "cannot be called until the window handle has been created"，
        // 结果就是命中时气球提示静默失败。读一次 Handle 才真的建出来。
        _ = _marshal.Handle;

        _tick.Tick += (_, _) => RefreshStatus();
        _tick.Start();

        StartCapture();
        RefreshStatus();
        if (_openUiFirst) ShowLibrary();
        if (_selfTest > 0) ArmSelfTest();
    }

    /// <summary>
    /// 托盘自检：按时间轴真实地点一遍菜单项（暂停/继续、开库、刷新状态），
    /// 最后自己退出。写它是为了不靠人手在托盘上点——那段代码路径跟人手点完全一样。
    /// </summary>
    private void ArmSelfTest()
    {
        var say = new Action<string>(s => Log.Write("[t] " + s));
        var step = 0;
        _auto = new System.Windows.Forms.Timer { Interval = 1000 };
        _auto.Tick += (_, _) =>
        {
            step++;
            try
            {
                switch (step)
                {
                    case 2:
                        say($"状态行 = \"{_statusItem.Text}\"");
                        say($"tooltip = \"{_icon.Text.Replace("\n", " | ")}\"");
                        say($"菜单项 = {string.Join(" / ", _icon.ContextMenuStrip!.Items.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "—" : i.Text))}");
                        break;
                    case 3:
                        // 检测灵敏度：直接走菜单点击那条路径，验证「改了立刻生效 + 落盘记住」
                        say($"阈值菜单 = {FindSensitivityRoot()?.Text ?? "找不到"}");
                        var before = EffectiveThreshold;
                        say($"改前阈值 = {before:F2}，配置文件 {(File.Exists(_cfgPath) ? "已存在" : "还没有")}");
                        var pick = DetectPolicy.Presets.First(v => Math.Abs(v - 0.85f) < 0.001f);
                        ClickSensitivity(pick);
                        say($"点了 {pick:F2} 之后 = {EffectiveThreshold:F2}");
                        var shown = File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)";
                        say("config.json 内容 = " + shown);
                        var back = DetectPolicy.Presets.First(v => Math.Abs(v - 0.70f) < 0.001f);
                        ClickSensitivity(back);
                        say($"点回 {back:F2} 之后 = {EffectiveThreshold:F2}");
                        break;
                    case 4:
                        say($"点「暂停录制」→ {_pauseItem.Text}");
                        TogglePause();
                        say($"暂停后 = \"{_statusItem.Text}\"，菜单项 = \"{_pauseItem.Text}\"");
                        break;
                    case 6:
                        say($"再点一次 → {_pauseItem.Text}");
                        TogglePause();
                        say($"恢复后 = \"{_statusItem.Text}\"，菜单项 = \"{_pauseItem.Text}\"");
                        break;
                    case 8:
                        say("点「打开回顾库」");
                        ShowLibrary();
                        say($"库窗口 = {(_libForm is null ? "null" : "已开 " + _libForm.Text)}");
                        break;
                    case 10:
                        say($"录制统计 = {_cap?.Stats() ?? "cap 为空"}");
                        break;
                    default:
                        // 走完固定步骤后，剩下来的时间按 _selfTest 秒数收尾。
                        // 硬编码 12 秒就没法测「等游戏窗口出现」那种需要等一会儿的场景。
                        if (step >= 12 && step % 2 == 0)
                            say($"[{step}s] 状态行 = \"{_statusItem.Text}\"，cap = {(_cap is null ? "空" : _cap.Stats())}");
                        if (step >= Math.Max(12, _selfTest))
                        {
                            say("关库窗口后退出");
                            _libForm?.Close();
                            _auto!.Stop();
                            Quit();
                        }
                        break;
                }
            }
            catch (Exception ex) { say("步骤 " + step + " 抛异常: " + ex.Message); }
        };
        _auto.Start();
        say($"自检已启动，约 {_selfTest} 秒后自动退出");
    }

    private void StartCapture()
    {
        var hwnd = Native.ResolveWindow(_title);
        if (hwnd == IntPtr.Zero)
        {
            // 先开软件再开游戏是常见顺序，不能就此摆烂。
            // 转成「等待游戏」状态，由 _wait 定时器每 3 秒重试一次。
            Log.Write($"[tray] 没找到「{_title}」窗口，转入等待");
            _statusItem.Text = "等待游戏启动…";
            _icon.Text = $"ApexHighlights — 等待「{_title}」窗口";
            _icon.ShowBalloonTip(6000, "ApexHighlights 已在托盘运行",
                $"正在等待「{_title}」窗口出现，游戏起来会自动开始录制，不用重开。\n" +
                "那个黑框是我故意藏起来的，不是崩溃；要退出请右键这个托盘图标 → 退出。",
                ToolTipIcon.Info);
            ArmWaitForGame();
            return;
        }

        try
        {
            Log.Write($"[tray] 找到窗口 \"{Native.TitleOf(hwnd)}\" 0x{hwnd:X}");
            // 引擎是新建的，得把用户自调的阈值套上去（Program 那次 Apply 是给控制台模式用的）
            var eng = new BannerEngine(_profile);
            eng.Apply(_detect);
            _cap = new Capture(hwnd, eng)
            {
                DiagPath = null,
                TargetFps = _fps,
                EncodedFps = _fps,
                Bitrate = _bitrate,
            };
            _cap.GopFrames = _fps;
            _cap.Quota = _policy;
            _cap.AudioEnabled = _audio;
            _cap.BeepOnSave = _beep;
            _cap.FakeHitAtSec = _fakeHit;
            _cap.Detected += OnDetected;
            _cap.EnableEncoding(90);
            var rep = _cap.RunQuotaCheck();

            Log.Write($"[tray] 采集已启动 {_cap.Width}x{_cap.Height} @{_fps}fps {_bitrate / 1_000_000}Mbps，" +
                      $"条带 {_profile.Band.W}x{_profile.Band.H}@{_profile.Band.X},{_profile.Band.Y}，" +
                      $"模板 {string.Join(",", _profile.Templates.Select(t => t.Id))}，阈值 {_cap.Threshold:F2}，库 {_cap.ClipDir}" +
                      (rep is null ? "" : $"（{rep}）"));
            _statusItem.Text = $"录制中 {_cap.Width}×{_cap.Height} @{_fps}fps";
            _icon.ShowBalloonTip(3000, "ApexHighlights",
                $"开始录制 {Native.TitleOf(hwnd)}\n命中击倒横幅会自动存一段。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"[tray] 采集启动失败: {ex}");
            _statusItem.Text = "启动失败";
            _icon.Text = "ApexHighlights — 启动失败";
            _icon.ShowBalloonTip(6000, "ApexHighlights", "采集启动失败：" + ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>每 3 秒找一次窗口，找到就真的开始录。找到之后定时器自己停。</summary>
    private void ArmWaitForGame()
    {
        if (_wait is not null) return;
        _wait = new System.Windows.Forms.Timer { Interval = 3000 };
        _wait.Tick += (_, _) =>
        {
            if (Native.ResolveWindow(_title) == IntPtr.Zero) return;
            _wait!.Stop();
            _wait.Dispose();
            _wait = null;
            StartCapture();
            RefreshStatus();
        };
        _wait.Start();
    }

    /// <summary>命中回调（非 UI 线程）：报一声，然后把片段路径塞给库窗口刷新。</summary>
    private void OnDetected(Capture.Detection d)
    {
        // 回调来自编码线程；封送失败也不能让异常冒进录制循环（那会拖垮整个采集）
        try
        {
            _marshal.BeginInvoke(() => Announce(d));
        }
        catch (Exception ex) { Log.Write("[tray] 封送失败: " + ex.Message); }
    }

    private void Announce(Capture.Detection d)
    {
        Log.Write($"[HIT] {d.WallClock:HH:mm:ss} +{d.Ms / 1000.0:F1}s [{d.Label}] score={d.Score:F2} at({d.X},{d.Y})" +
                  (d.ClipPath is null ? "  （还没产出片段）" : $"  → {Path.GetFileName(d.ClipPath)} {QuotaReport.Mb(d.Bytes)}"));
        if (d.ClipPath is null) return;
        if (!File.Exists(d.ClipPath)) { Log.Write($"[!] 片段文件不见了: {d.ClipPath}"); return; }
        _icon.ShowBalloonTip(5000, "已保存一段",
            $"{Path.GetFileName(d.ClipPath)}\n{QuotaReport.Mb(d.Bytes)}", ToolTipIcon.None);
        _libForm?.Reload();
        RefreshStatus();
    }

    private void TogglePause()
    {
        if (_cap is null) return;
        _cap.UserPaused = !_cap.UserPaused;
        _pauseItem.Text = _cap.UserPaused ? "继续录制" : "暂停录制";
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        // 游戏退出后 WGC 会话就成了死会话：环形不再前进、也不再有命中。
        // 重开一局是很常见的动作，所以这里发现窗口真的没了就收摊、回到「等待游戏」，
        // 而不是一直挂着一个假的「录制中」。
        //
        // 注意用 ResolveWindowAny（包含最小化的窗口）：单纯 alt-tab 到最小化不等于退出，
        // 那种情况下要继续保持采集，用户切回去就接着录。
        if (_cap is not null && Native.ResolveWindowAny(_title) == IntPtr.Zero)
        {
            if (++_missingTicks >= 2)   // 连续两次（约 10 秒）都找不到才判定退出
            {
                Log.Write($"[tray] 「{_title}」窗口连续 {_missingTicks} 次找不到，判定游戏已退出，收采集");
                StopCapture();
                _statusItem.Text = "等待游戏启动…";
                _icon.Text = $"ApexHighlights — 等待「{_title}」窗口";
                ArmWaitForGame();
                return;
            }
        }
        else _missingTicks = 0;

        if (_cap is null) return;
        var s = _cap.Stats();
        _statusItem.Text = _cap.UserPaused ? "已暂停" :
                           _cap.RecordingPaused ? "已暂停（磁盘空间不足）" :
                           $"录制中 {_cap.Width}×{_cap.Height} @{_fps}fps";

        // NotifyIcon.Text 上限 127 字符，超了会抛异常
        var tip = "ApexHighlights\n" + s;
        _icon.Text = tip.Length <= 127 ? tip : tip[..124] + "…";

        // 统计只进文件不刷控制台：5 秒一行，一天下来上千行，控制台里看着累
        Log.FileOnly("[stat] " + s);
    }

    /// <summary>收掉当前的采集（游戏退出 / 退出程序时用）。失败也只记一笔，不让托盘崩。</summary>
    private void StopCapture()
    {
        var cap = _cap;
        _cap = null;
        _missingTicks = 0;
        _pauseItem.Text = "暂停录制";
        if (cap is null) return;
        try { cap.Detected -= OnDetected; } catch { }
        try { cap.Dispose(); } catch (Exception ex) { Log.Write("[tray] 收采集失败: " + ex.Message); }
    }

    private void ShowLibrary()
    {
        if (_libForm is { IsDisposed: false })
        {
            _libForm.Show();
            if (_libForm.WindowState == FormWindowState.Minimized) _libForm.WindowState = FormWindowState.Normal;
            _libForm.Activate();
            return;
        }
        var dir = _cap?.ClipDir ?? Paths.Clips;
        _libForm = new LibraryForm(new ClipLibrary(dir), _policy) { Threshold = EffectiveThreshold, OnThresholdChanged = v => ApplyThreshold(v) };
        _libForm.FormClosed += (_, _) => _libForm = null;
        _libForm.Show();
    }

    private void Reveal(string? path)
    {
        var dir = _cap?.ClipDir ?? Paths.Clips;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe",
                path is null ? $"\"{dir}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenConfig()
    {
        var path = _cfgPath;
        try
        {
            if (!File.Exists(path)) _policy.Save(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// 「检测灵敏度」子菜单：把阈值做成可以直接点的档位，而不是让用户去手改 JSON。
    /// 用户 2026-10-28 的原话是「能不能加一个我可以自己调阈值的功能」——
    /// 就在这儿：调完立刻生效（不重启、不重开录制），并写回 config.json 记住。
    /// </summary>
    private ToolStripMenuItem BuildSensitivityMenu()
    {
        var root = new ToolStripMenuItem("检测灵敏度");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            var cur = EffectiveThreshold;
            foreach (var it in items)
                it.Checked = Math.Abs((float)it.Tag! - cur) < 0.001f;
            root.Text = cur > 0 ? $"检测灵敏度（{cur:F2}）" : "检测灵敏度（按模板）";
        }

        // 0 = 按 games/<id>.json 里模板自带的阈值，不覆盖
        var auto = new ToolStripMenuItem("按模板自带（不覆盖）") { Tag = 0f };
        auto.Click += (_, _) => ApplyThreshold(0f, Refresh);
        root.DropDownItems.Add(auto);
        items.Add(auto);
        root.DropDownItems.Add(new ToolStripSeparator());

        foreach (var v in DetectPolicy.Presets)
        {
            var it = new ToolStripMenuItem($"{v:F2}   {DetectPolicy.HintFor(v)}") { Tag = v };
            var val = v;
            it.Click += (_, _) => ApplyThreshold(val, Refresh);
            root.DropDownItems.Add(it);
            items.Add(it);
        }

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    /// <summary>当前生效阈值：运行中优先问采集器（它握着引擎），没在录就看配置。</summary>
    private float EffectiveThreshold =>
        _cap?.Threshold ?? (_detect.Threshold > 0 ? _detect.Threshold : _profile.Templates[0].Threshold);

    /// <summary>改阈值：写进内存配置 → 立刻套到正在跑的采集上 → 落盘记住。</summary>
    private void ApplyThreshold(float v, Action? after = null)
    {
        _detect.Threshold = DetectPolicy.Clamp(new DetectPolicy { Threshold = v }).Threshold;
        try { _cap?.SetThreshold(_detect.Threshold); }
        catch (Exception ex) { Log.Write("[tray] 改阈值失败: " + ex.Message); }

        var ok = _detect.Save(_cfgPath);
        Log.Write($"[threshold] 阈值设为 {(_detect.Threshold <= 0 ? "按模板自带" : _detect.Threshold.ToString("F2"))}" +
                  $"（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>自检用：找到「检测灵敏度」根菜单项。</summary>
    private ToolStripMenuItem? FindSensitivityRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("检测灵敏度", StringComparison.Ordinal));

    /// <summary>自检用：等价于点了一下某个阈值档位（走的是和真人点击完全相同的回调）。</summary>
    private void ClickSensitivity(float v)
    {
        var root = FindSensitivityRoot();
        var hit = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is float f && Math.Abs(f - v) < 0.001f);
        if (hit is null) { Log.Write($"[t] 找不到阈值档位 {v:F2}"); return; }
        hit.PerformClick();
    }

    private void Quit()
    {
        _tick.Stop();
        _icon.Visible = false;
        try { _cap?.Dispose(); } catch { }
        _libForm?.Close();
        _marshal.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _icon.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
