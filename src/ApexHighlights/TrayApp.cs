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
    private readonly CapturePolicy _capture;
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
    private string _title;
    private readonly bool _openUiFirst;
    private readonly bool _beep;
    private readonly bool _ocr;
    private readonly int _fakeHit;
    private readonly int _selfTest;
    private System.Windows.Forms.Timer? _auto;
    private System.Windows.Forms.Timer? _wait;

    /// <summary>连续多少次状态刷新都找不到游戏窗口。用于区分「切走了」和「真退出了」。</summary>
    private int _missingTicks;

    // 控制台窗口的延迟隐藏。
    // 用户要的是「启动并捕获到 Apex 后长亮 5 秒再藏」——不是立刻藏。
    // 立刻藏（以前的做法）的后果是双击后黑框一闪就没，看着像崩溃；
    // 而完全不藏又会让一个黑窗口一直杵在任务栏里。折中：起了采集再等 5 秒。
    // 另外还有个兜底：25 秒内一直没捕获到游戏也藏掉，否则用户没开游戏时黑窗口永远留着。
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private long _captureStartMs = -1;
    private bool _consoleHidden;
    private System.Windows.Forms.Timer? _hideTick;

    /// <summary>启动后多久还没捕获到游戏就无条件藏控制台（毫秒）。</summary>
    private const int HideAnywayMs = 25_000;

    /// <summary>捕获成功后控制台再保持可见多久（毫秒）。</summary>
    private const int KeepVisibleAfterCaptureMs = 5_000;

    public TrayApp(GameProfile profile, QuotaPolicy policy, DetectPolicy detect, CapturePolicy capture, string cfgPath, string title, int fps, bool openUiFirst, bool beep, int fakeHit = 0, int selfTest = 0, bool ocr = true)
    {
        _profile = profile;
        _policy = policy;
        _detect = detect;
        _capture = capture;
        _cfgPath = cfgPath;
        _title = title;
        _fps = fps;
        _openUiFirst = openUiFirst;
        _beep = beep;
        _ocr = ocr;
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
        menu.Items.Add(BuildMergeMenu());
        menu.Items.Add(BuildBitrateMenu());
        menu.Items.Add(BuildAudioMenu());
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
        ArmConsoleHide();
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
                        // 合并窗口：同样走菜单点击，验证「立刻生效 + 落盘记住 + 不影响阈值」
                        say($"合并菜单 = {FindMergeRoot()?.Text ?? "找不到"}");
                        say($"改前合并窗口 = {EffectiveMergeSec}s");
                        ClickMerge(3);
                        say($"点 3s 之后 = {EffectiveMergeSec}s，采集器里 MergeMs = {(_cap?.MergeMs.ToString() ?? "无采集")}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        ClickMerge(8);
                        say($"点回 8s 之后 = {EffectiveMergeSec}s");
                        // 画质码率 + 音轨：走同一条点击路径，验证「立刻生效 + 落盘 + 不冲掉阈值/合并窗口」
                        say($"码率菜单 = {FindBitrateRoot()?.Text ?? "找不到"}");
                        say($"改前码率 = {EffectiveBitrateMbps}Mbps");
                        ClickBitrate(40);
                        say($"点 40Mbps 之后 = {EffectiveBitrateMbps}Mbps，采集器里 Bitrate = {(_cap?.Bitrate.ToString() ?? "无采集")}");
                        say($"音轨菜单 = {FindAudioRoot()?.Text ?? "找不到"}，改前 = {(EffectiveAudio ? "开" : "关")}，实际在录 = {(_cap?.AudioOn.ToString() ?? "无采集")}");
                        ClickAudio(false);
                        say($"点「关」之后 = {(EffectiveAudio ? "开" : "关")}，实际在录 = {(_cap?.AudioOn.ToString() ?? "无采集")}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        // 再开回来：验证 StopAudio/StartAudio 往返没问题（这是最容易踩坑的一条路径）
                        ClickAudio(true);
                        say($"点回「开」之后 = {(EffectiveAudio ? "开" : "关")}，实际在录 = {(_cap?.AudioOn.ToString() ?? "无采集")}");
                        ClickBitrate(20);
                        say($"点回 20Mbps 之后 = {EffectiveBitrateMbps}Mbps");
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
                Bitrate = _capture.BitrateBps,
            };
            _cap.GopFrames = _fps;
            _cap.Quota = _policy;
            _cap.AudioEnabled = _capture.Audio;
            _cap.BeepOnSave = _beep;
            _cap.OcrEnabled = _ocr;
            _cap.MergeMs = _detect.MergeSec * 1000;
            _cap.FakeHitAtSec = _fakeHit;
            _cap.Detected += OnDetected;
            _cap.EnableEncoding(90);
            var rep = _cap.RunQuotaCheck();

            Log.Write($"[tray] 采集已启动 {_cap.Width}x{_cap.Height} @{_fps}fps {_capture.BitrateMbps}Mbps，" +
                      $"条带 {_profile.Band.W}x{_profile.Band.H}@{_profile.Band.X},{_profile.Band.Y}，" +
                      $"模板 {string.Join(",", _profile.Templates.Select(t => t.Id))}，阈值 {_cap.Threshold:F2}，库 {_cap.ClipDir}" +
                      (rep is null ? "" : $"（{rep}）"));
            // 捕获成功 = 控制台可以从这一刻起算 5 秒后藏掉（用户要的「长亮 5 秒」）
            if (_captureStartMs < 0) _captureStartMs = _sinceStart.ElapsedMilliseconds;
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

    /// <summary>
    /// 启动后盯住控制台窗口，到点就藏：
    /// 捕获成功后再等 <see cref="KeepVisibleAfterCaptureMs"/>，或者一直没捕获到则等
    /// <see cref="HideAnywayMs"/>。停在这里之前不碰窗口，用户能看见启动那几行输出。
    /// </summary>
    private void ArmConsoleHide()
    {
        if (_selfTest > 0) return;                 // 自检要读 stdout，不能藏
        if (_consoleHidden || _hideTick is not null) return;
        _hideTick = new System.Windows.Forms.Timer { Interval = 500 };
        _hideTick.Tick += (_, _) =>
        {
            var now = _sinceStart.ElapsedMilliseconds;
            var target = _captureStartMs >= 0
                ? _captureStartMs + KeepVisibleAfterCaptureMs
                : HideAnywayMs;
            if (now < target) return;
            _hideTick!.Stop();
            _hideTick.Dispose();
            _hideTick = null;
            Native.SetConsoleVisible(false);
            _consoleHidden = true;
            Log.FileOnly($"[console] 启动 {now}ms 后隐藏控制台" +
                         (_captureStartMs >= 0 ? $"（捕获于 {_captureStartMs}ms）" : "（始终没捕获到游戏）"));
        };
        _hideTick.Start();
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

    /// <summary>合并窗口的候选秒数。0 = 不合并。</summary>
    private static readonly int[] MergePresets = { 0, 3, 5, 8, 12, 20 };

    /// <summary>
    /// 「合并窗口」子菜单：决定相邻多近的命中算同一场交火。
    /// 横幅停留 2–3 秒会反复触发，窗口太小一次击倒就出好几条视频；太大会把两次独立击倒并成一条。
    /// </summary>
    private ToolStripMenuItem BuildMergeMenu()
    {
        var root = new ToolStripMenuItem("合并窗口");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            var cur = EffectiveMergeSec;
            foreach (var it in items)
                it.Checked = (int)it.Tag! == cur;
            root.Text = cur == 0 ? "合并窗口（关）" : $"合并窗口（{cur}s）";
        }

        foreach (var sec in MergePresets)
        {
            var it = new ToolStripMenuItem(sec == 0 ? "关（每次命中各出一段）" : $"{sec} 秒   {HintForMerge(sec)}")
            { Tag = sec };
            var v = sec;
            it.Click += (_, _) => ApplyMerge(v, Refresh);
            root.DropDownItems.Add(it);
            items.Add(it);
        }

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    private static string HintForMerge(int sec) => sec switch
    {
        0 => "一条横幅可能出好几段",
        <= 5 => "出片快，连击倒可能拆成两段",
        <= 12 => "推荐：一场交火一段",
        _ => "很宽容，独立击倒也可能并在一起",
    };

    /// <summary>当前合并窗口（秒）：运行中问采集器，没在录看配置。</summary>
    private int EffectiveMergeSec =>
        _cap is not null ? _cap.MergeMs / 1000 : _detect.MergeSec;

    /// <summary>改合并窗口：内存配置 → 套到正在跑的采集上 → 落盘。</summary>
    private void ApplyMerge(int seconds, Action? after = null)
    {
        var p = DetectPolicy.Clamp(new DetectPolicy { MergeSec = seconds });
        _detect.MergeSec = p.MergeSec;
        try { _cap?.SetMergeMs(_detect.MergeSec * 1000); }
        catch (Exception ex) { Log.Write("[tray] 改合并窗口失败: " + ex.Message); }

        var ok = _detect.Save(_cfgPath);
        Log.Write($"[merge] 合并窗口设为 {(_detect.MergeSec == 0 ? "关" : _detect.MergeSec + "s")}" +
                  $"（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>
    /// 「画质码率」子菜单：用户反馈 20Mbps 快速转视角时糊，所以把档位铺开到 10–100。
    /// 码率不是越小越省事——掉到 15 以下 Apex 的草和树叶会糊成一片，剪出来没法看；
    /// 但调太高硬盘也撑不住，所以每档都写清楚代价。
    /// </summary>
    private ToolStripMenuItem BuildBitrateMenu()
    {
        var root = new ToolStripMenuItem("画质码率");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            var cur = EffectiveBitrateMbps;
            foreach (var it in items)
                it.Checked = (int)it.Tag! == cur;
            // 命令行给的 -b 25 这种不在档位上的值也要显示出来，否则菜单看起来像没生效
            var offGrid = !CapturePolicy.BitratePresets.Contains(cur);
            root.Text = offGrid ? $"画质码率（{cur}Mbps 自定义）" : $"画质码率（{cur}Mbps）";
        }

        foreach (var mbps in CapturePolicy.BitratePresets)
        {
            var it = new ToolStripMenuItem($"{mbps} Mbps   {CapturePolicy.HintForBitrate(mbps)}")
            { Tag = mbps };
            var v = mbps;
            it.Click += (_, _) => ApplyBitrate(v, Refresh);
            root.DropDownItems.Add(it);
            items.Add(it);
        }

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    /// <summary>当前码率（Mbps）：运行中问采集器，没在录看配置。</summary>
    private int EffectiveBitrateMbps =>
        _cap is not null ? Math.Max(1, _cap.Bitrate / 1_000_000) : _capture.BitrateMbps;

    /// <summary>改码率：内存配置 → 换掉正在跑的编码器 → 落盘。</summary>
    private void ApplyBitrate(int mbps, Action? after = null)
    {
        var p = CapturePolicy.Clamp(new CapturePolicy { BitrateMbps = mbps, Audio = _capture.Audio });
        _capture.BitrateMbps = p.BitrateMbps;
        try { _cap?.SetBitrate(_capture.BitrateBps); }
        catch (Exception ex) { Log.Write("[tray] 改码率失败: " + ex.Message); }

        var ok = _capture.Save(_cfgPath);
        Log.Write($"[bitrate] 码率设为 {_capture.BitrateMbps}Mbps" +
                  $"（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>
    /// 「音轨」子菜单：录制前忘了加 --no-audio 就只能重开，所以给个能随时拨的开关。
    /// 关掉之后已经在录的环形里那些带声音的片段不受影响，只有之后的片段变无声。
    /// </summary>
    private ToolStripMenuItem BuildAudioMenu()
    {
        var root = new ToolStripMenuItem("音轨");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            var on = EffectiveAudio;
            foreach (var it in items)
                it.Checked = (bool)it.Tag! == on;
            // 想开但没开成（没有默认播放设备之类）时得让用户看得出来
            var broken = on && _cap is not null && !_cap.AudioOn;
            root.Text = on ? $"音轨（开{(broken ? "·异常" : "")}）" : "音轨（关）";
        }

        var onItem = new ToolStripMenuItem("开  录游戏声音（系统回环，不碰麦克风）") { Tag = true };
        onItem.Click += (_, _) => ApplyAudio(true, Refresh);
        var offItem = new ToolStripMenuItem("关  片段为无声 mp4，省一点体积") { Tag = false };
        offItem.Click += (_, _) => ApplyAudio(false, Refresh);
        root.DropDownItems.Add(onItem);
        root.DropDownItems.Add(offItem);
        items.Add(onItem);
        items.Add(offItem);

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    /// <summary>音轨该不该开：运行中问采集器，没在录看配置。</summary>
    private bool EffectiveAudio => _cap is not null ? _cap.AudioEnabled : _capture.Audio;

    /// <summary>改音轨开关：内存配置 → 就地起停采集器里的音轨 → 落盘。</summary>
    private void ApplyAudio(bool on, Action? after = null)
    {
        _capture.Audio = on;
        try { _cap?.SetAudioEnabled(on); }
        catch (Exception ex) { Log.Write("[tray] 切音轨失败: " + ex.Message); }

        var ok = _capture.Save(_cfgPath);
        Log.Write($"[audio] 音轨设为{(on ? "开" : "关")}" +
                  $"（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

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

    /// <summary>自检用：找到「合并窗口」根菜单项。</summary>
    private ToolStripMenuItem? FindMergeRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("合并窗口", StringComparison.Ordinal));

    /// <summary>自检用：等价于点了一下某个合并窗口档位。</summary>
    private void ClickMerge(int sec)
    {
        var root = FindMergeRoot();
        var hit = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is int s && s == sec);
        if (hit is null) { Log.Write($"[t] 找不到合并窗口档位 {sec}s"); return; }
        hit.PerformClick();
    }

    /// <summary>自检用：找到「画质码率」根菜单项。</summary>
    private ToolStripMenuItem? FindBitrateRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("画质码率", StringComparison.Ordinal));

    /// <summary>自检用：等价于点了一下某个码率档位。</summary>
    private void ClickBitrate(int mbps)
    {
        var root = FindBitrateRoot();
        var hit = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is int v && v == mbps);
        if (hit is null) { Log.Write($"[t] 找不到码率档位 {mbps}Mbps"); return; }
        hit.PerformClick();
    }

    /// <summary>自检用：找到「音轨」根菜单项。</summary>
    private ToolStripMenuItem? FindAudioRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("音轨", StringComparison.Ordinal));

    /// <summary>自检用：等价于点了一下音轨开关。</summary>
    private void ClickAudio(bool on)
    {
        var root = FindAudioRoot();
        var hit = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is bool b && b == on);
        if (hit is null) { Log.Write($"[t] 找不到音轨项 {(on ? "开" : "关")}"); return; }
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
