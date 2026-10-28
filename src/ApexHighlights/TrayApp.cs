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
    private int _tickSec;

    public TrayApp(GameProfile profile, QuotaPolicy policy, string title, int fps, int bitrate, bool openUiFirst, bool audio, bool beep, int fakeHit = 0, int selfTest = 0)
    {
        _profile = profile;
        _policy = policy;
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
        var say = new Action<string>(s => Console.WriteLine("[t] " + s));
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
                    case 12:
                        say("关库窗口后退出");
                        _libForm?.Close();
                        _auto!.Stop();
                        Quit();
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
            _statusItem.Text = "没找到游戏窗口";
            _icon.Text = "ApexHighlights — 没找到游戏窗口（右键可退出）";
            _icon.ShowBalloonTip(4000, "ApexHighlights",
                $"没找到标题含「{_title}」的窗口。\n游戏开了之后右键退出再启动，或者直接双击 exe 重新试。",
                ToolTipIcon.Warning);
            return;
        }

        try
        {
            _cap = new Capture(hwnd, new BannerEngine(_profile))
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
            _cap.RunQuotaCheck();

            _statusItem.Text = $"录制中 {_cap.Width}×{_cap.Height} @{_fps}fps";
            _icon.ShowBalloonTip(3000, "ApexHighlights",
                $"开始录制 {Native.TitleOf(hwnd)}\n命中击倒横幅会自动存一段。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _statusItem.Text = "启动失败";
            _icon.Text = "ApexHighlights — 启动失败";
            _icon.ShowBalloonTip(6000, "ApexHighlights", "采集启动失败：" + ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>命中回调（非 UI 线程）：报一声，然后把片段路径塞给库窗口刷新。</summary>
    private void OnDetected(Capture.Detection d)
    {
        // 回调来自编码线程；封送失败也不能让异常冒进录制循环（那会拖垮整个采集）
        try
        {
            _marshal.BeginInvoke(() => Announce(d));
        }
        catch (Exception ex) { Console.WriteLine("[tray] 封送失败: " + ex.Message); }
    }

    private void Announce(Capture.Detection d)
    {
        if (d.ClipPath is null) return;
        if (!File.Exists(d.ClipPath)) return;
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
        if (_cap is null) return;
        var s = _cap.Stats();
        _statusItem.Text = _cap.UserPaused ? "已暂停" :
                           _cap.RecordingPaused ? "已暂停（磁盘空间不足）" :
                           $"录制中 {_cap.Width}×{_cap.Height} @{_fps}fps";

        // NotifyIcon.Text 上限 127 字符，超了会抛异常
        var tip = "ApexHighlights\n" + s;
        _icon.Text = tip.Length <= 127 ? tip : tip[..124] + "…";
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
        _libForm = new LibraryForm(new ClipLibrary(dir), _policy);
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
        var path = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
        try
        {
            if (!File.Exists(path)) _policy.Save(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
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
