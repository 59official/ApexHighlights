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

    // 「片段目录」子菜单的常驻对象：改完目录 / 展开菜单 / 自检刷新标题都要用
    private ToolStripMenuItem? _clipsRoot;
    private ToolStripMenuItem? _clipsWhere;
    private ToolStripMenuItem? _clipsReset;

    private int _fps;
    private string _title;
    private readonly bool _openUiFirst;
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
        _ocr = ocr;
        _fakeHit = fakeHit;
        _selfTest = selfTest;

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };
        _statusItem.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        _libraryItem.Click += (_, _) => ShowLibrary();
        menu.Items.Add(_libraryItem);
        menu.Items.Add(BuildClipsDirMenu());
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem.Click += (_, _) => TogglePause();
        menu.Items.Add(_pauseItem);
        var config = new ToolStripMenuItem("打开配置");
        config.Click += (_, _) => OpenConfig();
        menu.Items.Add(config);
        menu.Items.Add(BuildSensitivityMenu());
        menu.Items.Add(BuildParamsItem());
        menu.Items.Add(BuildAudioMenu());
        menu.Items.Add(BuildNoticeMenu());
        menu.Items.Add(BuildKindsMenu());
        menu.Items.Add(BuildQuotaMenu());
        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("退出");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(BuildLogItem());
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
                        // 录制参数：面板现在管码率/前摇/后摇/合并四个值，走的是面板回调那条路
                        say($"改前：码率 {EffectiveBitrateMbps}Mbps，前 {EffectivePreRollSec}s，后 {EffectivePostRollSec}s，合并 {EffectiveMergeSec}s");
                        ApplyParams(40, 12, 9, 6);
                        say($"拖完：码率 {EffectiveBitrateMbps}Mbps，前 {EffectivePreRollSec}s，后 {EffectivePostRollSec}s，合并 {EffectiveMergeSec}s，" +
                            $"采集器 Bitrate={(_cap?.Bitrate.ToString() ?? "无采集")} 前={(_cap?.PreRollSec.ToString() ?? "无采集")} 后={(_cap?.PostRollSec.ToString() ?? "无采集")} 合并={(_cap?.MergeMs.ToString() ?? "无采集")}ms");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        say($"音轨菜单 = {FindAudioRoot()?.Text ?? "找不到"}，改前 = {CapturePolicy.AudioLabel(_capture.Audio)}，实际在录 = {(_cap?.AudioOn.ToString() ?? "无采集")}");
                        ClickAudio(AudioMode.None);
                        say($"「不录声音」之后 = {FindAudioRoot()?.Text}，实际在录 = {(_cap?.AudioOn.ToString() ?? "无采集")}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        // 再开回来：验证 StopAudio/StartAudio 往返没问题（这是最容易踩坑的一条路径）
                        ClickAudio(AudioMode.GameOnly);
                        say($"「只录游戏」之后 = {FindAudioRoot()?.Text}，实际在录 = {(_cap?.AudioOn.ToString() ?? "无采集")}，" +
                            $"游戏源 = {(_cap?.GameOn.ToString() ?? "无采集")}，回退 = {(_cap?.GameFellBack.ToString() ?? "无采集")}");
                        ClickAudio(AudioMode.SystemOnly);
                        say($"「系统声音」之后 = {FindAudioRoot()?.Text}，系统源 = {(_cap?.SystemOn.ToString() ?? "无采集")}，游戏源 = {(_cap?.GameOn.ToString() ?? "无采集")}");
                        // 麦克风：这台机器上可能没有录音设备，所以这里验的是「菜单/配置/采集器状态一致」，
                        // 真开设备的能力由 --audio-test / --mic-test 负责
                        ClickAudio(AudioMode.SystemAndMic);
                        say($"「系统+麦克风」之后 = {FindAudioRoot()?.Text}，采集器里麦克风 = {(_cap?.MicOn.ToString() ?? "无采集")}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        ClickAudio(AudioMode.GameOnly);
                        say($"回到「只录游戏」之后 = {FindAudioRoot()?.Text}");
                        // 提示三档：只验菜单选中态和落盘，浮层本身由 --ui-test 的离屏用例管
                        say($"录制提示菜单 = {FindNoticeRoot()?.Text ?? "找不到"}");
                        ClickNotice(NoticeMode.Beep);
                        say($"点「只响一声」之后 = {FindNoticeRoot()?.Text}，配置 Notice = {_capture.Notice}");
                        ClickNotice(NoticeMode.None);
                        say($"点「不提示」之后 = {FindNoticeRoot()?.Text}，配置 Notice = {_capture.Notice}");
                        ClickNotice(NoticeMode.Toast);
                        say($"点回「滑出提示」之后 = {FindNoticeRoot()?.Text}，配置 Notice = {_capture.Notice}");
                        ApplyParams(28, 15, 15, 8);
                        say($"恢复默认之后：码率 {EffectiveBitrateMbps}Mbps，前 {EffectivePreRollSec}s，后 {EffectivePostRollSec}s，合并 {EffectiveMergeSec}s");
                        // 种类开关：关掉「助攻击倒」，看闸门真的不切片，再开回来
                        say($"种类菜单 = {FindKindsRoot()?.Text ?? "找不到"}");
                        ClickKind(ClipKind.AssistKnock, false);
                        say($"关掉助攻击倒之后 = {FindKindsRoot()?.Text}，闸门说 {(_cap?.WillRecord(ClipKind.AssistKnock).ToString() ?? "无采集")}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        ClickKind(ClipKind.AssistKnock, true);
                        say($"开回来之后 = {FindKindsRoot()?.Text}，闸门说 {(_cap?.WillRecord(ClipKind.AssistKnock).ToString() ?? "无采集")}");
                        // 片段目录：换到临时子目录 → 看采集器/菜单/配置 → 点「恢复默认位置」回来
                        say($"目录菜单 = {FindClipsRoot()?.Text ?? "找不到"}，当前 = {EffectiveClipsDir}");
                        var testDir = Path.Combine(Paths.DataRoot, "clips-test");
                        ApplyClipsDir(testDir);
                        say($"改成 {testDir} 之后：采集器 ClipDir = {(_cap?.ClipDir ?? "无采集")}，菜单 = {FindClipsRoot()?.Text}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        ClickClipsDefault();
                        say($"点了「恢复默认位置」之后 = {EffectiveClipsDir}，采集器 ClipDir = {(_cap?.ClipDir ?? "无采集")}，菜单 = {FindClipsRoot()?.Text}");
                        // 容量与保留：两个档位各点一次，验证「立刻生效 + 落盘 + 不冲掉别的字段」
                        say($"配额菜单 = {FindQuotaRoot()?.Text ?? "找不到"}");
                        ClickQuota(20);
                        say($"点 20GB 之后 = {FindQuotaRoot()?.Text}，policy.QuotaGb = {_policy.QuotaGb}，采集器里 Quota = {(_cap?.Quota.QuotaGb.ToString() ?? "无采集")}");
                        ClickRetain(7);
                        say($"点 7 天之后 = {FindQuotaRoot()?.Text}，policy.RetainDays = {_policy.RetainDays}，采集器里 = {(_cap?.Quota.RetainDays.ToString() ?? "无采集")}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        ClickQuota(40);
                        ClickRetain(30);
                        say($"点回 40GB/30 天之后 = {FindQuotaRoot()?.Text}");
                        break;
                    case 13:
                        // 库窗口里点「选择目录」：回调得改采集器 + 落盘，但**不能把用户正站着的窗口关掉**
                        if (_libForm is not { IsDisposed: false } libOpen || libOpen.OnClipsDirRequested is not { } req)
                        { say("库窗口没开，跳过「库窗口里改目录」这条"); break; }
                        var libDir = Path.Combine(Paths.DataRoot, "clips-lib");
                        var okReq = req(libDir);
                        say($"库窗口里改目录 → 回调返回 {okReq}，窗口还在 = {_libForm is { IsDisposed: false }}，" +
                            $"采集器 ClipDir = {(_cap?.ClipDir ?? "无采集")}，菜单 = {FindClipsRoot()?.Text}");
                        say("config.json 现在是 " +
                            (File.Exists(_cfgPath) ? string.Join(" ", File.ReadAllLines(_cfgPath)) : "(没写)"));
                        ClickClipsDefault();
                        // 托盘菜单改目录时库窗口会被关掉（它缓存的是旧库），这是设计如此；
                        // 只有从库窗口自己改才留窗，所以这一行对不上也别当成回归。
                        say($"从托盘恢复默认之后 = {EffectiveClipsDir}，库窗口还在 = {_libForm is { IsDisposed: false }}（托盘改的，关窗是对的）");
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
            _cap.Mode = _capture.Audio;
            _cap.OcrEnabled = _ocr;
            _cap.MergeMs = _detect.MergeSec * 1000;
            _cap.PreRollSec = _capture.PreRollSec;     // 面板改的「击杀前录多久」
            _cap.PostRollSec = _capture.PostRollSec;   // 面板改的「击杀后录多久」
            // 种类开关：闭包捕获的是同一个 CapturePolicy 实例，所以托盘里再关/开也立刻生效，
            // 不用回头动 _cap（Capture 的闸门每次切片都会问一次）
            _cap.KindFilter = k => _capture.Records(k);
            _cap.FakeHitAtSec = _fakeHit;
            _cap.Detected += OnDetected;
            _cap.ClipSaved += OnClipSaved;
            _cap.EnableEncoding(90);
            var rep = _cap.RunQuotaCheck();

            Log.Write($"[tray] 采集已启动 {_cap.Width}x{_cap.Height} @{_fps}fps {_capture.BitrateMbps}Mbps，" +
                      $"条带 {_profile.Band.W}x{_profile.Band.H}@{_profile.Band.X},{_profile.Band.Y}，" +
                      $"模板 {string.Join(",", _profile.Templates.Select(t => t.Id))}，阈值 {_cap.Threshold:F2}，库 {_cap.ClipDir}" +
                      (rep is null ? "" : $"（{rep}）"));
            // 捕获成功 = 控制台可以从这一刻起算 5 秒后藏掉（用户要的「长亮 5 秒」）
            if (_captureStartMs < 0) _captureStartMs = _sinceStart.ElapsedMilliseconds;
            _statusItem.Text = $"录制中 {_cap.Width}×{_cap.Height} @{_fps}fps";
            NoticeStart(_cap.Width, _cap.Height);
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
        _libForm = new LibraryForm(new ClipLibrary(dir), _policy)
        {
            Threshold = EffectiveThreshold,
            OnThresholdChanged = v => ApplyThreshold(v),
            // 库窗口状态栏里那行「配额 / 保留」点开就能改，和托盘菜单共用同一个出口
            OnQuotaChanged = (gb, days) => ApplyQuota(gb, days),
            OnParamsRequested = OpenParams,
        };
        // 库窗口里的「选择目录」：搬家询问 + 改采集器 + 落盘都由托盘这侧做（它有 _capture 和配置路径），
        // 但**别关窗口**——用户正站在里面点按钮，关掉等于把他甩出来。
        _libForm.OnClipsDirRequested = target =>
        {
            var from = EffectiveClipsDir;
            if (SameDir(from, target)) return false;
            if (!AskMoveClips(from, target)) return false;
            ApplyClipsDir(target, closeLibrary: false);
            return true;
        };
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

    /// <summary>当前合并窗口（秒）：运行中问采集器，没在录看配置。</summary>
    private int EffectiveMergeSec =>
        _cap is not null ? _cap.MergeMs / 1000 : _detect.MergeSec;

    /// <summary>当前码率（Mbps）：运行中问采集器，没在录看配置。</summary>
    private int EffectiveBitrateMbps =>
        _cap is not null ? Math.Max(1, _cap.Bitrate / 1_000_000) : _capture.BitrateMbps;

    private ParamsForm? _paramsForm;

    /// <summary>
    /// 托盘里的「录制参数…」。面板是独立的小窗（菜单里塞不下滑杆），
    /// 同一个时刻只留一个：再点一次就把已经开着的那个提到前面，别开出一摞。
    /// </summary>
    private void OpenParams()
    {
        if (_paramsForm is { IsDisposed: false })
        {
            _paramsForm.Activate();
            return;
        }

        var f = new ParamsForm(
            EffectiveBitrateMbps,
            EffectivePreRollSec,
            EffectivePostRollSec,
            EffectiveMergeSec)
        {
            // 拖动中只改采集器（save:false），松手才写盘 —— 一次拖动不该刷几百次配置文件
            Changed = (bitrate, pre, post, merge) => ApplyParams(bitrate, pre, post, merge, save: false),
            Committed = (bitrate, pre, post, merge) => ApplyParams(bitrate, pre, post, merge),
        };
        f.FormClosed += (_, _) => _paramsForm = null;

        // 放在屏幕中间偏上一点：滑杆面板不高，居中会显得悬空
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + (wa.Height - f.Height) / 2 - 40);
        _paramsForm = f;
        f.Show();
        f.Activate();
    }

    /// <summary>当前「击杀前」秒数：运行中问采集器（-1 表示还在用游戏档案的值），没在录看配置。</summary>
    private int EffectivePreRollSec =>
        _cap is not null && _cap.PreRollSec >= 0 ? _cap.PreRollSec : _capture.PreRollSec;

    /// <summary>当前「击杀后」秒数，同上。</summary>
    private int EffectivePostRollSec =>
        _cap is not null && _cap.PostRollSec >= 0 ? _cap.PostRollSec : _capture.PostRollSec;

    /// <summary>
    /// 面板拖完一根滑杆就走到这里：改内存配置 → 套到正在跑的采集器上 → 两份 config 落盘。
    /// 四个值互不依赖，所以一次全写，不搞增量。
    /// </summary>
    private void ApplyParams(int bitrate, int preRoll, int postRoll, int merge, bool save = true)
    {
        _capture.BitrateMbps = Math.Clamp(bitrate, CapturePolicy.BitrateMin, CapturePolicy.BitrateMax);
        _capture.PreRollSec = Math.Clamp(preRoll, 0, CapturePolicy.RollMax);
        _capture.PostRollSec = Math.Clamp(postRoll, 0, CapturePolicy.RollMax);
        _detect.MergeSec = Math.Clamp(merge, 0, CapturePolicy.MergeMax);

        try { _cap?.SetBitrate(_capture.BitrateBps); }
        catch (Exception ex) { Log.Write("[tray] 改码率失败: " + ex.Message); }

        if (_cap is not null)
        {
            _cap.PreRollSec = _capture.PreRollSec;
            _cap.PostRollSec = _capture.PostRollSec;
        }

        try { _cap?.SetMergeMs(_detect.MergeSec * 1000); }
        catch (Exception ex) { Log.Write("[tray] 改合并窗口失败: " + ex.Message); }

        if (save)
        {
            var ok1 = _capture.Save(_cfgPath);
            var ok2 = _detect.Save(_cfgPath);
            Log.Write($"[params] 码率 {_capture.BitrateMbps}Mbps，击杀前 {_capture.PreRollSec}s，" +
                      $"击杀后 {_capture.PostRollSec}s，合并 {_detect.MergeSec}s" +
                      $"（保存到 {_cfgPath}：{(ok1 && ok2 ? "成功" : "失败")}）");
        }
        RefreshStatus();
    }

    private ToolStripMenuItem BuildParamsItem()
    {
        var it = new ToolStripMenuItem("录制参数…");
        it.Click += (_, _) => OpenParams();
        return it;
    }

    /// <summary>「查看日志」：用默认程序打开 live.log（没有就打开数据目录，别弹个错误框）。</summary>
    private ToolStripMenuItem BuildLogItem()
    {
        var it = new ToolStripMenuItem("查看日志");
        it.Click += (_, _) =>
        {
            try
            {
                var log = Path.Combine(Paths.DataRoot, "live.log");
                var target = File.Exists(log) ? log : Paths.DataRoot;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Write("[tray] 打开日志失败: " + ex.Message); }
        };
        return it;
    }

    /// <summary>
    /// 「音轨」子菜单：四档单选（只录游戏 / 系统 + 麦克风 / 系统声音 / 不录声音）。
    ///
    /// 以前是两个独立勾选（系统声、麦克风），但「只录游戏进程」和「整机回环」是互斥的两种抓法，
    /// 勾选表达不出来，所以改成单选。档位顺序见 <see cref="CapturePolicy.AudioModes"/>。
    /// </summary>
    private ToolStripMenuItem BuildAudioMenu()
    {
        var root = new ToolStripMenuItem("音轨");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            var mode = _cap?.Mode ?? _capture.Audio;
            foreach (var it in items) it.Checked = (AudioMode)it.Tag! == mode;

            root.Text = "音轨（" + ShortAudio(mode) + "）";
            // 只录游戏、但进程回环没起来（已经退回整机抓了）：菜单上必须看得出来，
            // 否则用户会以为 Discord 的声音没被录进去，其实录进去了
            if (mode == AudioMode.GameOnly && _cap?.GameFellBack == true) root.Text = "音轨（只录游戏·已回退系统）";
            else if (mode != AudioMode.None && _cap is not null && !_cap.AudioOn) root.Text += "·异常";
        }

        foreach (var m in CapturePolicy.AudioModes)
        {
            var it = new ToolStripMenuItem(CapturePolicy.AudioLabel(m))
            {
                Tag = m,
                ToolTipText = CapturePolicy.AudioHint(m),
            };
            it.Click += (_, _) => ApplyAudio(m, Refresh);
            root.DropDownItems.Add(it);
            items.Add(it);
        }

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    /// <summary>根菜单里只写短名字——括号已经占了一层，全称塞进去太长。</summary>
    private static string ShortAudio(AudioMode m) => m switch
    {
        AudioMode.GameOnly => "只录游戏",
        AudioMode.SystemAndMic => "系统+麦克风",
        AudioMode.SystemOnly => "系统声音",
        _ => "关",
    };

    /// <summary>改音轨档位：内存配置 → 就地换采集源的档 → 落盘。</summary>
    private void ApplyAudio(AudioMode mode, Action? after = null)
    {
        _capture.Audio = mode;
        try { _cap?.SetAudioMode(mode); }
        catch (Exception ex) { Log.Write("[tray] 切音轨失败: " + ex.Message); }

        var ok = _capture.Save(_cfgPath);
        Log.Write($"[audio] 音轨设为 {CapturePolicy.AudioLabel(mode)}（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>
    /// 「录制提示」子菜单：三档单选（滑出浮层 / 只响一声 / 不提示）。
    /// 提示音和浮层同时来会打架，所以是单选而不是两个开关。
    /// </summary>
    private ToolStripMenuItem BuildNoticeMenu()
    {
        var root = new ToolStripMenuItem("录制提示");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            foreach (var it in items) it.Checked = (NoticeMode)it.Tag! == _capture.Notice;
            root.Text = "录制提示（" + ShortNotice(_capture.Notice) + "）";
        }

        foreach (var n in CapturePolicy.NoticeModes)
        {
            var it = new ToolStripMenuItem(CapturePolicy.NoticeLabel(n))
            {
                Tag = n,
                ToolTipText = CapturePolicy.NoticeHint(n),
            };
            it.Click += (_, _) => ApplyNotice(n, Refresh);
            root.DropDownItems.Add(it);
            items.Add(it);
        }

        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    private static string ShortNotice(NoticeMode n) => n switch
    {
        NoticeMode.Toast => "滑出浮层",
        NoticeMode.Beep => "提示音",
        _ => "关",
    };

    /// <summary>改提示方式。不需要动采集器——提示是宿主的事，录制器只负责报事件。</summary>
    private void ApplyNotice(NoticeMode mode, Action? after = null)
    {
        _capture.Notice = mode;
        var ok = _capture.Save(_cfgPath);
        Log.Write($"[notice] 录制提示设为 {CapturePolicy.NoticeLabel(mode)}（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>
    /// 开录提示。只有「滑出浮层」这一档会在开录时说话 —— 另外两档不该在开录时打扰，
    /// 用户选提示音要的是「存好了告诉我」，不是「开机式的一串响动」。
    /// </summary>
    private void NoticeStart(int w, int h)
    {
        if (_capture.Notice != NoticeMode.Toast) return;
        Toast.Show($"{h}p · {_fps}fps", "ApexHighlights正在录制");
    }

    /// <summary>
    /// 存好一段的通知。录制线程调过来，得切回 UI 线程再弹窗/响铃。
    /// 盯的是 _capture.Notice 而不是保存那一刻的快照：用户在存盘途中改了档位，以新档为准。
    /// </summary>
    private void OnClipSaved(ClipKind kind, double seconds)
    {
        if (_capture.Notice == NoticeMode.None) return;
        _marshal.BeginInvoke(() =>
        {
            if (_capture.Notice == NoticeMode.Beep) { Beep.Saved(); return; }
            if (_capture.Notice == NoticeMode.Toast)
                Toast.Show($"{seconds:F0} 秒 · {ClipKinds.Label(kind)}", "ApexHighlights已录制");
        });
    }

    /// <summary>
    /// 「容量与保留」子菜单：磁盘配额 + 保留天数，两排档位。
    /// 这两项管的是同一件事（什么时候删旧片段），拆成两个顶层菜单反而让人不知道先动哪个。
    /// </summary>
    private ToolStripMenuItem BuildQuotaMenu()
    {
        var root = new ToolStripMenuItem("容量与保留");
        var capRoot = new ToolStripMenuItem("磁盘配额");
        var keepRoot = new ToolStripMenuItem("保留天数");
        var capItems = new List<ToolStripMenuItem>();
        var keepItems = new List<ToolStripMenuItem>();

        void Refresh()
        {
            foreach (var it in capItems) it.Checked = (int)it.Tag! == _policy.QuotaGb;
            foreach (var it in keepItems) it.Checked = (int)it.Tag! == _policy.RetainDays;
            capRoot.Text = "磁盘配额：" + QuotaPolicy.QuotaLabel(_policy.QuotaGb);
            keepRoot.Text = "保留天数：" + QuotaPolicy.RetainLabel(_policy.RetainDays);
            root.Text = $"容量与保留（{QuotaPolicy.QuotaLabel(_policy.QuotaGb)} / " +
                        $"{QuotaPolicy.RetainLabel(_policy.RetainDays)}）";
        }

        foreach (var gb in QuotaPolicy.QuotaPresets)
        {
            var it = new ToolStripMenuItem(QuotaPolicy.QuotaLabel(gb)) { Tag = gb };
            it.Click += (_, _) => ApplyQuota(gb, _policy.RetainDays, Refresh);
            capRoot.DropDownItems.Add(it);
            capItems.Add(it);
        }
        foreach (var days in QuotaPolicy.RetainPresets)
        {
            var it = new ToolStripMenuItem(QuotaPolicy.RetainLabel(days)) { Tag = days };
            it.Click += (_, _) => ApplyQuota(_policy.QuotaGb, days, Refresh);
            keepRoot.DropDownItems.Add(it);
            keepItems.Add(it);
        }

        root.DropDownItems.Add(capRoot);
        root.DropDownItems.Add(keepRoot);
        root.DropDownOpening += (_, _) => Refresh();
        Refresh();
        return root;
    }

    /// <summary>
    /// 改配额或保留天数：先空跑算「会删几片」，删得动东西就先问一句，再落盘 + 立刻清理。
    /// 不问就直接删太狠了——点错一格 40GB→10GB 能把整库清空。
    /// </summary>
    private void ApplyQuota(long quotaGb, int retainDays, Action? after = null)
    {
        quotaGb = Math.Clamp(quotaGb, 0L, 2000L);
        retainDays = Math.Clamp(retainDays, 0, 3650);
        if (quotaGb == _policy.QuotaGb && retainDays == _policy.RetainDays) return;

        var probe = new QuotaPolicy
        {
            QuotaGb = quotaGb,
            RetainDays = retainDays,
            WatermarkGb = _policy.WatermarkGb,
            CheckMinutes = _policy.CheckMinutes,
        };

        // 空跑：只算出会删哪些，文件一个都不动
        QuotaReport? dry = null;
        try
        {
            if (_cap is not null) dry = QuotaEnforcer.Run(new ClipLibrary(_cap.ClipDir), probe, null, dryRun: true);
        }
        catch (Exception ex) { Log.Write("[quota] 预估失败: " + ex.Message); }

        if (dry is { DeletedTotal: > 0 })
        {
            var ans = MessageBox.Show(
                $"按新设置会立刻删掉 {dry.DeletedTotal} 片（{QuotaReport.Mb(dry.BytesFreed)}）：\n\n" +
                $"  超期 {dry.DeletedByAge.Count} 片、超配额 {dry.DeletedByQuota.Count} 片\n\n" +
                "超出部分按「最不值钱」的顺序挑（小队淘汰 → 助攻 → 队友击倒 → 自己被击倒 → 自己击倒）。\n" +
                "确定要改吗？",
                "ApexHighlights",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (ans != DialogResult.Yes) return;
        }

        var oldText = $"{QuotaPolicy.QuotaLabel(_policy.QuotaGb)}/{QuotaPolicy.RetainLabel(_policy.RetainDays)}";
        _policy.QuotaGb = quotaGb;
        _policy.RetainDays = retainDays;
        if (_cap is not null) _cap.Quota = _policy;

        _policy.Save(_cfgPath);      // QuotaPolicy.Save 内部吞异常，不返回成败
        QuotaReport? rep = null;
        try { rep = _cap?.RunQuotaCheck(); } catch (Exception ex) { Log.Write("[quota] 清理失败: " + ex.Message); }

        var nowText = $"{QuotaPolicy.QuotaLabel(quotaGb)}/{QuotaPolicy.RetainLabel(retainDays)}";
        Log.Write($"[quota] 容量与保留 {oldText} → {nowText}（写入 {_cfgPath}）" +
                  (rep is null ? "" : $"  → {rep}"));
        if (rep is { DeletedTotal: > 0 })
            _icon.ShowBalloonTip(4000, "ApexHighlights",
                $"按新配额清理了 {rep.DeletedTotal} 片，释放 {QuotaReport.Mb(rep.BytesFreed)}。",
                ToolTipIcon.Info);

        after?.Invoke();
        RefreshStatus();
        // 库窗口开着的话，它得重新数一遍（可能刚被删掉几片）
        if (_libForm is { IsDisposed: false } lf)
        {
            lf.SetPolicy(_policy);
            lf.Reload();
        }
    }

    /// <summary>
    /// 「录制种类」子菜单：每种一条勾选项，勾着 = 录。
    /// 以前种类只能全录，而回顾库的种类下拉里还写着「击杀/被击倒」两种根本判不出来的，
    /// 用户自然以为「明明没做的动作也录了」。现在能逐种关掉，关掉的种类连文件都不生成。
    /// </summary>
    private ToolStripMenuItem BuildKindsMenu()
    {
        var root = new ToolStripMenuItem("录制种类");
        var items = new List<ToolStripMenuItem>();

        void Refresh()
        {
            foreach (var it in items)
                it.Checked = _capture.Records((ClipKind)it.Tag!);

            var off = ClipKinds.All.Where(k => !_capture.Records(k)).ToArray();
            root.Text = off.Length == 0
                ? "录制种类（全都录）"
                : off.Length == ClipKinds.All.Length
                    ? "录制种类（全关了：不录任何片段）"
                    : $"录制种类（已关：{string.Join("、", off.Select(ClipKinds.Label))}）";
        }

        foreach (var k in ClipKinds.All)
        {
            var kind = k;
            var it = new ToolStripMenuItem(ClipKinds.Label(k)) { Tag = k, CheckOnClick = true };
            it.Click += (_, _) => ApplyKind(kind, !_capture.Records(kind), Refresh);
            root.DropDownItems.Add(it);
            items.Add(it);
        }

        root.DropDownItems.Add(new ToolStripSeparator());
        var allOn = new ToolStripMenuItem("全部打开（恢复全录）");
        allOn.Click += (_, _) =>
        {
            foreach (var k in ClipKinds.All) _capture.SetRecords(k, true);
            _capture.Save(_cfgPath);
            Log.Write("[kind] 全部种类恢复录制");
            Refresh();
            RefreshStatus();
        };
        root.DropDownItems.Add(allOn);

        root.DropDownOpening += (_, _) =>
        {
            Refresh();
            // 已经全开时这条没意义，灰掉但仍留着（菜单项忽隐忽现更烦人）
            allOn.Enabled = ClipKinds.All.Any(k => !_capture.Records(k));
        };
        Refresh();
        return root;
    }

    /// <summary>开/关某个种类的录制：改内存配置（Capture 的闸门立刻跟着变）→ 落盘。</summary>
    private void ApplyKind(ClipKind k, bool on, Action? after = null)
    {
        _capture.SetRecords(k, on);
        var ok = _capture.Save(_cfgPath);
        Log.Write($"[kind]「{ClipKinds.Label(k)}」录制={(on ? "开" : "关")}" +
                  $"（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>成片放在哪：运行中问采集器，还没开始录就看配置。</summary>
    private string EffectiveClipsDir => _cap is not null ? _cap.ClipDir : Paths.Clips;

    /// <summary>
    /// 「片段目录」子菜单：回顾库的成片存在哪个文件夹，用户自己挑。
    ///
    /// 起因是 2026-10-29 的提问「没有开关能选回顾目录放哪个文件夹吗」。之前唯一的办法是
    /// --data= 把整个数据目录搬走，可那会把 config.json、环形分片一起搬，太重；
    /// 而且 1440p60 一小时七八个 GB，系统盘很容易被它塞满，用户需要的正是「只让成片去别的盘」。
    /// </summary>
    private ToolStripMenuItem BuildClipsDirMenu()
    {
        var root = new ToolStripMenuItem("片段目录");
        var where = new ToolStripMenuItem { Enabled = false };
        var pick = new ToolStripMenuItem("选择文件夹…");
        var open = new ToolStripMenuItem("打开这个文件夹");
        var reset = new ToolStripMenuItem("恢复默认位置");

        _clipsRoot = root;
        _clipsWhere = where;
        _clipsReset = reset;

        pick.Click += (_, _) => PickClipsDir();
        open.Click += (_, _) => Reveal(null);
        reset.Click += (_, _) =>
        {
            if (_capture.ClipsDir.Length == 0) return;
            // 回到默认位置时也问一句：默认目录里可能已经有老片段，搬回去容易撞名
            if (!AskMoveClips(EffectiveClipsDir, Paths.DefaultClips)) return;
            ApplyClipsDir("");
        };

        root.DropDownItems.Add(where);
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(pick);
        root.DropDownItems.Add(open);
        root.DropDownItems.Add(reset);

        // 托盘菜单是常驻对象，用户可能在录制中改过目录，展开时重算一次
        root.DropDownOpening += (_, _) => RefreshClipsDirMenu();
        RefreshClipsDirMenu();
        return root;
    }

    /// <summary>刷新「片段目录」的标题与内部状态行（改完目录、展开菜单、自检都走这里）。</summary>
    private void RefreshClipsDirMenu()
    {
        if (_clipsRoot is null) return;
        var dir = EffectiveClipsDir;
        var custom = _capture.ClipsDir.Length > 0;
        if (_clipsWhere is not null) _clipsWhere.Text = (custom ? "当前：" : "默认：") + dir;
        if (_clipsReset is not null) _clipsReset.Enabled = custom;
        // 托盘菜单宽度有限，标题只放叶子名，完整路径在展开后的第一行
        var leaf = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
        _clipsRoot.Text = custom && leaf.Length > 0 ? $"片段目录（{leaf}）" : "片段目录（默认）";
    }

    /// <summary>点「选择文件夹…」：挑目录 → 问要不要把现有片段搬过去 → 改目录并落盘。</summary>
    private void PickClipsDir()
    {
        var old = EffectiveClipsDir;
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择回顾库文件夹（以后录的片段存在这里）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(old) ? old : Paths.DataRoot,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var target = Paths.NormalizeDir(dlg.SelectedPath);
        if (target.Length == 0) return;
        if (SameDir(old, target))
        {
            Log.Write("[dir] 选的是当前目录，没改");
            return;
        }
        if (!AskMoveClips(old, target)) return;   // 用户点了取消
        ApplyClipsDir(target);
    }

    /// <summary>
    /// 换目录前问一句「现有片段要不要一起搬过去」，答「是」就真搬。
    /// 返回 false = 用户取消，别改目录。
    ///
    /// 为什么问：回顾库只显示当前目录，默不作声换走会让用户以为 30 多片被删了。
    /// 为什么是移动不是复制：同一块盘上 rename 是瞬时的，跨盘也就一次读写，不会翻倍占空间。
    /// </summary>
    private bool AskMoveClips(string from, string to)
    {
        var files = ClipFiles(from);
        if (files.Count == 0) return true;
        if (SameDir(from, to)) return true;

        long bytes = 0;
        foreach (var f in files) { try { bytes += new FileInfo(f).Length; } catch { } }

        var ask = MessageBox.Show(
            $"当前目录有 {files.Count} 个片段（{QuotaReport.Mb(bytes)}）：\n{from}\n\n" +
            $"要把它们一起搬到新目录吗？\n{to}\n\n" +
            "是：搬过去（移动，不复制）\n" +
            "否：只改以后存哪，旧片段留在原目录\n" +
            "取消：什么都不改",
            "回顾库目录", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (ask == DialogResult.Cancel) return false;
        if (ask == DialogResult.No) return true;

        var (ok, fail) = MoveClips(from, to);
        Log.Write($"[dir] 迁移片段：成功 {ok}，失败 {fail}（{from} → {to}）");
        if (fail > 0)
            MessageBox.Show($"搬过去 {ok} 个，{fail} 个没搬成（同名或正被占用）。\n明细在 live.log 里。",
                "回顾库目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    /// <summary>
    /// 把 <paramref name="from"/> 下的成片移到 <paramref name="to"/>，同名的不覆盖。
    /// 单独拆出来是为了能在自检里直接验（对话框没法自动点）。
    /// </summary>
    internal static (int Ok, int Fail) MoveClips(string from, string to)
    {
        int ok = 0, fail = 0;
        try { Directory.CreateDirectory(to); } catch { }
        foreach (var f in ClipFiles(from))
        {
            try
            {
                var dst = System.IO.Path.Combine(to, System.IO.Path.GetFileName(f));
                if (File.Exists(dst)) { fail++; continue; }   // 同名不覆盖
                File.Move(f, dst);
                ok++;
            }
            catch { fail++; }
        }
        return (ok, fail);
    }

    /// <summary>改目录：内存配置 → Paths → 就地换采集器的成片目录 → 落盘 → 回顾库跟着换。</summary>
    private void ApplyClipsDir(string? dir, Action? after = null, bool closeLibrary = true)
    {
        var full = Paths.NormalizeDir(dir);
        _capture.ClipsDir = full;
        Paths.SetClipsRoot(full);
        try { _cap?.SetClipDir(Paths.Clips); }
        catch (Exception ex) { Log.Write("[tray] 换成片目录失败: " + ex.Message); }

        var ok = _capture.Save(_cfgPath);
        Log.Write($"[dir] 成片目录设为 {Paths.Clips}{(full.Length == 0 ? "（默认位置）" : "")}" +
                  $"（保存到 {_cfgPath}：{(ok ? "成功" : "失败")}）");

        // 回顾库窗口缓存着自己那份 ClipLibrary，换完目录它就指向旧库了。
        // 从托盘改的：直接关掉，下次打开读新目录；从库窗口自己改的：让它就地换库（关掉等于把用户甩出去）。
        if (_libForm is { IsDisposed: false })
        {
            if (closeLibrary)
            {
                try { _libForm.Close(); } catch { }
                _libForm = null;
            }
            else
            {
                try { _libForm.SetClipsDir(Paths.Clips); } catch { }
            }
        }

        RefreshClipsDirMenu();
        after?.Invoke();
        RefreshStatus();
    }

    /// <summary>目录里现有的成片（只看顶层，回顾库也只扫顶层）。</summary>
    private static List<string> ClipFiles(string dir)
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(dir)) list.AddRange(Directory.GetFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly));
        }
        catch { }
        return list;
    }

    private static bool SameDir(string a, string b)
        => string.Equals(Paths.NormalizeDir(a), Paths.NormalizeDir(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>自检用：找到「片段目录」根菜单项。</summary>
    private ToolStripMenuItem? FindClipsRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("片段目录", StringComparison.Ordinal));

    /// <summary>自检用：等价于点了一下「恢复默认位置」。</summary>
    private void ClickClipsDefault()
    {
        var root = FindClipsRoot();
        var hit = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("恢复默认位置", StringComparison.Ordinal));
        if (hit is null) { Log.Write("[t] 找不到「恢复默认位置」"); return; }
        if (hit.Enabled) hit.PerformClick();
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

    /// <summary>自检用：找到「音轨」根菜单项。</summary>
    private ToolStripMenuItem? FindAudioRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("音轨", StringComparison.Ordinal));

    /// <summary>自检用：点到某个音轨档位（等价于用户在菜单里点那一项）。</summary>
    private void ClickAudio(AudioMode mode)
    {
        var root = FindAudioRoot();
        if (root is null) { Log.Write("[t] 找不到音轨菜单"); return; }
        var hit = root.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is AudioMode m && m == mode);
        if (hit is null) { Log.Write($"[t] 音轨菜单缺档位 {mode}"); return; }
        if (!hit.Checked) hit.PerformClick();
    }

    /// <summary>自检用：点到某个提示档位。</summary>
    private void ClickNotice(NoticeMode mode)
    {
        var root = FindNoticeRoot();
        if (root is null) { Log.Write("[t] 找不到录制提示菜单"); return; }
        var hit = root.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is NoticeMode n && n == mode);
        if (hit is null) { Log.Write($"[t] 录制提示菜单缺档位 {mode}"); return; }
        if (!hit.Checked) hit.PerformClick();
    }

    /// <summary>自检用：找到「录制提示」根菜单项。</summary>
    private ToolStripMenuItem? FindNoticeRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("录制提示", StringComparison.Ordinal));

    /// <summary>自检用：找到「容量与保留」根菜单项。</summary>
    private ToolStripMenuItem? FindQuotaRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("容量与保留", StringComparison.Ordinal));

    /// <summary>自检用：等价于点某个配额档位（GB）。</summary>
    private void ClickQuota(long gb)
    {
        var root = FindQuotaRoot();
        var sub = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("磁盘配额", StringComparison.Ordinal));
        var hit = sub?.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag is long g && g == gb)
                  ?? sub?.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag is int g2 && g2 == gb);
        if (hit is null) { Log.Write($"[t] 找不到配额档位 {gb}GB"); return; }
        hit.PerformClick();
    }

    /// <summary>自检用：等价于点某个保留天数档位。</summary>
    private void ClickRetain(int days)
    {
        var root = FindQuotaRoot();
        var sub = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("保留天数", StringComparison.Ordinal));
        var hit = sub?.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag is int g && g == days)
                  ?? sub?.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag is long g2 && g2 == days);
        if (hit is null) { Log.Write($"[t] 找不到保留档位 {days} 天"); return; }
        hit.PerformClick();
    }

    /// <summary>自检用：找到「录制种类」根菜单项。</summary>
    private ToolStripMenuItem? FindKindsRoot() =>
        _icon.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Text.StartsWith("录制种类", StringComparison.Ordinal));

    /// <summary>自检用：等价于在种类子菜单里勾/去掉某个种类。</summary>
    private void ClickKind(ClipKind k, bool on)
    {
        var root = FindKindsRoot();
        var hit = root?.DropDownItems.OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Tag is ClipKind kk && kk == k);
        if (hit is null) { Log.Write($"[t] 找不到种类项 {ClipKinds.Label(k)}"); return; }
        if (hit.Checked != on) hit.PerformClick();   // CheckOnClick 的项，点一下就是翻转
    }

    private void Quit()
    {        _tick.Stop();
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
