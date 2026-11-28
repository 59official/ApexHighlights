这版是社区 PR 的合并版——感谢 [@xiayuFrancis](https://github.com/xiayuFrancis)：画面比例、观战误录、助攻击倒分类、麦克风混音、缓存清理这几处都是他修的，我做了双轴评审、补了两处收尾，并修好了「缺文件就弹崩」这个体验问题。

## 下载哪个

| 文件 | 说明 |
|---|---|
| `ApexHighlights-0.8.24-setup.exe` | **推荐**。装到 `%LOCALAPPDATA%\Programs\ApexHighlights`，带开始菜单项、可选开机自启、可正常卸载 |
| `ApexHighlights-0.8.24-portable.zip` | 解压即用，配置写在 exe 旁边 |

安装包自带 .NET 运行时，不需要另外装东西。Windows 11 上安装时那一次 UAC 是为了注册「身份包」关掉录制黄框，点「否」也能装完（黄框会留着，但不会进录像）。安装界面与应用界面目前只有中文。

## 这版的重点

- **非 16:9 画面不再漏**：新增「画面配置」，默认读 Apex 的 `videoconfig.txt` 拿显示分辨率，按实际采集尺寸把横幅搜索区扩成「能同时框住居中适配与居中裁切」的矩形；4:3 这类窄画面按同一比例归一化，字形不会被撑扁。录像尺寸仍由窗口决定。
- **观战/赛后不再误录**：命中后会读画面顶部的 HUD 文字，认出「等待恢复中 / 排位 / 实况 / 总结 / 观战」就跳过这次命中。这条要靠 Windows OCR 能读 HUD 语言。
- **助攻击倒被记成击倒**：同一个横幅会在命中后 350ms / 650ms 再取两次样本补读，只允许升级成更具体的种类（不会把击倒降级）。
- **麦克风 24 位有效数据装 32 位容器会错位**：采样步长改用 `BlockAlign`，输出恒为 2 声道，兼容单声道设备。
- **回顾库配额菜单崩溃**：状态栏的配额/保留天数菜单改成窗口持有、`Opening` 时重算，`ObjectDisposedException` 没了。
- **环形缓存越积越多**：每次采集一个 `ring/session-*/` 目录并用 `owner.lock` 标记存活，退出即清，异常退出留下的下次启动回收。
- **不再弹「Unhandled exception」风暴**：以前任何没接住的异常都会弹一个模态框，而五秒一次的 tick 会让它无限弹。现在统一写 `crash.log`，同类异常只在第一次提醒一句，程序继续跑。

## 已经有的功能

- **音轨四档**：只录游戏（进程回环，默认）/ 系统 + 麦克风 / 仅系统 / 关
- **录制提示三档**：滑出浮层（默认，不会被录进画面）/ 提示音 / 关
- **录制参数面板**：码率 4–60、击杀前 0–30s、击杀后 0–30s、合并窗口 0–30s、H.264/HEVC，拖动实时生效
- **三语横幅识别**：英文 / 简体 / 繁体三张锚点模板 + HUD 缩放 80–120%
- **回顾库**：筛选、预览、批量勾选删除、改片段目录、状态栏点配额改保留策略

## 系统要求

| 能力 | 最低系统 |
|---|---|
| 录制 + 音频（含只录游戏的进程回环） | Windows 10 2004（10.0.19041） |
| 关掉录制黄框 | Windows 11 22H2+，用安装包装并同意注册身份包 |

## 0.8.24 的验证

- `--policy-test` 66 项、`--ocr-parse-test` 39 项、`--mic-test` 15 项、`--ui-test` 77 项，全部通过
- 安装包冒烟：静默装到临时目录 → 478 文件 / `0.8.24.0` / 四项自检全绿 / 卸载后 exe 与目录清空
- 合成条带回归：观战跳过、4:3 归一化、缓存回收都有离线用例

## 已知限制

- 独占全屏可能抓不到，请用「无边框窗口」。
- 击杀（KILL）不触发录制：Apex 的击杀只在右上角击杀栏显示，中间没有横幅。
- 相邻命中在合并窗口内视为同一场交火，片段在末次命中后约 8 秒才出现。
- 「跳过观战」没有开关，且依赖 Windows OCR 能读 HUD 文字。
- 换分辨率不用重标条带（`band` 在基准分辨率坐标系里）；自定义游戏可用 `--band-tool=<截图.png>` 拖框生成。

---

## English

This release merges a community pull request from [@xiayuFrancis](https://github.com/xiayuFrancis) — picture aspect, spectator false positives, assist-knock classification, microphone mixing and cache cleanup are their work. I reviewed it on two axes, added two follow-up fixes, and removed the crash-dialog storm described below.

### Which file to download

| File | Notes |
|---|---|
| `ApexHighlights-0.8.24-setup.exe` | **Recommended.** Installs to `%LOCALAPPDATA%\Programs\ApexHighlights`, adds a Start-menu entry, optional autostart, clean uninstall |
| `ApexHighlights-0.8.24-portable.zip` | Unzip and run; config lives next to the exe |

The build is self-contained (.NET included). On Windows 11 the single UAC prompt registers the "identity package" that removes the capture border; saying no still installs fine. The installer and the app UI are Chinese-only.

### Highlights of this release

- **Non-16:9 screens no longer miss hits**: "Picture settings" reads Apex's `videoconfig.txt` display size and expands the banner search area so both letterboxed and cropped layouts are covered; narrow layouts such as 4:3 are normalised uniformly so text keeps its proportions. Recording size still follows the captured window.
- **No more spectator/recap false positives**: after a hit, the top HUD region is OCR'd; respawn countdowns and ranked / live / summary / spectator tabs skip that hit. Requires Windows OCR for the HUD language.
- **Assist knockdowns are no longer filed as plain knockdowns**: the same banner is re-sampled at 350 ms and 650 ms and may only be upgraded to a more specific type.
- **24-bit audio in a 32-bit container was misaligned**: sample stepping now uses `BlockAlign`, output is always stereo, mono devices included.
- **Library quota menu crash**: the menu is owned by the window and rebuilt on `Opening`, so the `ObjectDisposedException` is gone.
- **Ring cache no longer accumulates**: each capture gets `ring/session-*/` with an `owner.lock`; cleaned on exit, leftovers on next start.
- **No more "Unhandled exception" storms**: unhandled exceptions are logged to `crash.log` and mentioned once, then the app keeps running.

### System requirements

| Capability | Minimum |
|---|---|
| Recording + audio (incl. game-only process loopback) | Windows 10 2004 (10.0.19041) |
| Removing the capture border | Windows 11 22H2+ with the installer's identity package registered |

### Verification of 0.8.24

- `--policy-test` 66, `--ocr-parse-test` 39, `--mic-test` 15, `--ui-test` 77 checks — all green
- Installer smoke: silent install → 478 files / `0.8.24.0` / all four self-tests green / uninstall leaves nothing behind
- Offline cases cover spectator skip, 4:3 normalisation and cache reclamation

### Known limitations

- Exclusive fullscreen may not be captured; use borderless windowed.
- Kills do not trigger recording (Apex only shows them in the top-right feed).
- Hits inside the merge window are treated as one fight, so a clip lands ~8 s after the last hit.
- The spectator check has no toggle and depends on Windows OCR reading the HUD.
- Resolution changes do not require re-calibration; custom games can generate a band with `--band-tool=<screenshot.png>`.
