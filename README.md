# ApexHighlights

Apex Legends 自动击倒录制器：常驻环形缓冲，检测到屏幕中间的「KNOCKED DOWN」横幅后，自动把**前 30 秒**回取出来落盘成带音频的 mp4。

不注入、不读内存、不装驱动、不模拟输入、不联网。单 exe 目录、无账号、无服务器。

> 状态：采集/编码/音频/切片/配额清理/回顾库 UI/托盘常驻已跑通并实测（1440p 60fps 零丢帧，双轨成片，后摇完整），自包含发布已验证。详见 [docs/apex-v1-spec.md](docs/apex-v1-spec.md)。

## 为什么不用 OBS

OBS 能做这些事，但代价是：GPL 传染（做插件 = 衍生作品）和「先装 300MB 的 OBS」的首次体验。自研环形缓冲还能把同一场交火的两次击倒合并成一条文件，而不是产出两份重叠视频。

## 它是怎么工作的

```
WGC 抓窗口 → GPU 转 NV12 → NVENC 编码 → 1s fMP4 分片环（内存/磁盘）
                              ↘ 10fps 采条带 → 高通 + NCC 匹配横幅 → 命中
WASAPI loopback → AAC 160kbps → 内存音频环 ────────────────────────┘
                                                    ↓
                              按前摇回取 → 音视频对齐 → 单文件双轨 mp4
```

关键点：

- **GPU 全程不落 CPU**：采集帧是 D3D11 纹理，直接喂给 NVENC MFT（`MFCreateSample` + `MFCreateDXGISurfaceBuffer`），CPU 只做 10fps 的条带读回用于检测。
- **检测在 CPU 上很便宜**：条带只有 1280×120，高通滤波 + 归一化互相关，~12ms/次。
- **音频和视频共用同一个 `Stopwatch` 时间轴**，切片时按第一帧视频的绝对时间对齐两条轨。
- **不用 ffmpeg**：mp4 封装是自己写的（`Mp4.cs` / `Mp4Writer.cs`），单文件直接装 `avc1` + `mp4a` 两轨。

## 下载即用

从 [Releases](../../releases) 下载解压后双击 `ApexHighlights.exe`——自带 .NET 运行时，不需要安装任何东西。

首次启动会在 exe 旁边建出 `captures/`、`games/`、`config.json`（内嵌的默认配置会释放出来，之后可以就地改）。想清空就删掉整个目录。

如果 exe 放在 Program Files 之类不可写的位置，会自动改用 `%LOCALAPPDATA%\ApexHighlights`。也可以用 `--data=<目录>` 显式指定。

## 构建

需要 .NET 8 SDK 和 Windows 10 19041+：

```
cd src/ApexHighlights
dotnet build -c Release
```

依赖 `Vortice.Direct3D11` / `Vortice.MediaFoundation`（NuGet 自动拉取）。

出可分发的自包含版本（约 190MB，自带运行时，不需要目标机装 .NET）：

```
dotnet publish src/ApexHighlights/ApexHighlights.csproj -c Release \
  -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o dist
```

产物是 `dist/ApexHighlights.exe` 加上一批 dll，整个 `dist/` 目录一起拷走即可。

> **为什么不用 `PublishSingleFile`**：单文件包把原生库（Vortice / WinRT）嵌在 exe 里，每次启动要先解压到 `%TEMP%\.net\<app>\<hash>`。在部分机器上这一步会被拒（宿主返回 `error 5`，`Failed to create directory ... for extracting bundled files`），exe 直接启动不了。文件夹版没有解压步骤，实测在这台机器上正常，也就不留这个坑。`Properties/PublishProfiles/single-file.pubxml` 仍然保留，需要单文件时可以用，但不要作为默认分发形态。

推 `v*` tag 时 GitHub Actions 会自动跑发布并建 Release（见 `.github/workflows/release.yml`）。

## 运行

```
# 托盘常驻（双击 run-live.bat 或直接跑 exe）
ApexHighlights.exe

# 前台跑，指定窗口标题（开发时用任意动画窗口测试）
ApexHighlights.exe --title="BenchWnd" --fps=60 --bitrate=20 --duration=30 --fake-hit=12
```

| 参数 | 说明 |
|---|---|
| `--data=<目录>` | 数据目录（captures/games/config.json 都在这下面） |
| `--title=<片段>` | 窗口标题子串；`self` 表示自己的控制台窗口 |
| `--fps=<n>` | 目标帧率，默认 60（30/60/120） |
| `--bitrate=<n>` | 码率 Mbps，默认 20 |
| `--gop=<n>` | GOP 帧数，默认等于 fps |
| `--duration=<n>` | 跑 n 秒后退出（不填则一直跑） |
| `--fake-hit=<n>` | 第 n 秒伪造一次命中，用于端到端验证 |
| `--no-audio` / `--no-beep` | 关闭音轨 / 关闭落盘提示音 |
| `--no-tray` | 不常驻托盘，纯控制台 |
| `--ui` | 启动时先把回顾库窗口打开 |
| `--config=<路径>` | 配额配置文件，默认数据目录下 `config.json` |
| `--log=<路径>` | 诊断日志（会归档上一份） |

## 配额与保留期

默认 **40GB 配额 / 保留 30 天 / 2GB 磁盘水位**，写在数据目录下的 `config.json`：

```json
{ "QuotaGb": 40, "RetainDays": 30, "WatermarkGb": 2, "CheckMinutes": 60 }
```

- `QuotaGb: 0` = 不限体积；`RetainDays: 0` = 永久保留；`WatermarkGb: 0` = 不检查磁盘。
- 超过保留期的直接删；还超配额时按**被击倒 > 击倒 > 击杀**淘汰，同优先级内从旧到新。
- 磁盘跌破水位时暂停往环形写新数据，但已录好的片段照常落盘——不丢已有的。
- 检查时机：启动时、每片落盘后、每小时。

片段就是文件名本身，没有数据库：`apex_1028-130333_0.99.mp4`。想手动归档直接挪文件即可。

## 自检命令

不依赖真实游戏，任何一条都能单独跑：

| 命令 | 验证什么 |
|---|---|
| `--gpu-test` | GPU 直通编码全链路（NV12 转换、码率、分片、MF 回读） |
| `--av-test` | 双轨封装（合成正弦 + 渐变画面 → 音视频各一轨的 mp4） |
| `--audio-test --sec=3` | WASAPI loopback 采音（设备、混音格式、有效采样率、峰值） |
| `--audio-check <mp4>` | 把成片音轨解回 PCM，量峰值/RMS（**双轨存在不等于有声音**） |
| `--mp4-check <mp4>` | MF 分别回读视频/音频样本数与末时间戳（看音画是否对齐） |
| `--aac-probe` | 枚举系统可用的 AAC 编码器及输出类型 |
| `--beep-test <wav>` | 落盘提示音波形自检 |
| `--quota-test` | 配额/保留期逻辑自检（临时目录伪造片段，不碰真实库） |
| `--lib-list` | 列出现有片段（旧→新，含种类/分数/体积） |
| `--quota-run` | 真跑一次配额清理 |
| `--lib-ui` | 只开回顾库窗口，不启动录制 |
| `--probe <mp4>` | 读片段元数据（时长/分辨率/是否有音轨），纯 box 树不开 MF |
| `--tray-test=N` | 托盘自检：依次演练状态行/暂停/开库/退出 |
| `--offline <bands.bin>` | 离线回放预导出的条带，验证检测器与 Python 探针一致 |

## 已知限制

- 独占全屏下的黑屏风险（WGC 依赖 DWM 合成，未覆盖测试）。
- Apex 的击倒横幅在 1440p 下位置固定；换分辨率需重新标定 `games/apex.json` 的 `band`。
- 只做「我击倒别人」，不做被击倒（计划 v2）。
- 后摇需要等环形录满 5s 才落盘，所以命中后约 5s 才出现画面文件（提示音也在那一刻响）。

## 配置

`games/<id>.json` 描述一个游戏：窗口标题、参考分辨率、条带位置、模板图与阈值、前摇/后摇。新增游戏 = 加一个 json + 一张横幅截图，不用改代码。

内置配置以资源形式编进 exe，首次启动释放到数据目录的 `games/`；之后的改动以磁盘上的为准（不会被覆盖）。

## 许可

MIT，见 [LICENSE](LICENSE)。
