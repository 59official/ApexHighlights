# ApexHighlights

**English** | [简体中文](README.md)

> Synced from [README.md](README.md). If the two disagree, the Chinese one wins.

Apex Legends automatic highlight recorder: it keeps a ring buffer running at all times, and when it detects the knockdown banner in the middle of the screen it pulls the **previous 15 seconds** back out and writes them to a single mp4 with audio.

No injection, no memory reading, no drivers, no simulated input, no network. One exe in a folder, no account, no server.

> Status: capture / encoding / audio / clipping / quota cleanup / library UI / tray / installer have all been tested end to end (2560×1440 at 60fps, two-track clips, both HEVC and H.264). Technical details in [docs/apex-v1-spec.md](docs/apex-v1-spec.md) and [docs/DECISIONS.md](docs/DECISIONS.md).

## Why not OBS

OBS can do this too, at a cost: GPL is viral (a plugin is a derivative work), you first have to install 300MB of OBS, and two knockdowns in the same fight produce two overlapping clips. Our own ring buffer merges them into one file, and the whole path stays on the GPU without touching the CPU.

## How it works

```
WGC window capture → GPU convert to NV12 → NVENC/MFT encode → 1s fMP4 segment ring (memory/disk)
                                  ↘ 10fps band sampling → high-pass + NCC template match → hit
WASAPI (process loopback / whole-device loopback / microphone) → mix → AAC 160kbps → in-memory audio ring
                                                        ↓
                              pull back by pre-roll → align A/V → single mp4, two tracks
```

- **GPU the whole way, never lands on the CPU**: captured frames are D3D11 textures fed straight to the hardware encoder (`MFCreateSample` + `MFCreateDXGISurfaceBuffer`); the CPU only reads back the band at 10fps for detection.
- **Detection is cheap**: a 1280×120 band, high-pass + normalized cross-correlation, 1–2ms per run; OCR only runs after a hit.
- **Audio and video share one `Stopwatch` timeline**; when clipping, the two tracks are aligned to the absolute time of the first video frame.
- **No ffmpeg**: the mp4 muxing is written from scratch (`Mp4.cs` / `Mp4Writer.cs`), emitting an `hvc1`/`avc1` track plus an `mp4a` track directly.

## Download and install

Both work; the features are the same:

- **Installer** (recommended): `ApexHighlights-<version>-setup.exe`. Installs to `%LOCALAPPDATA%\Programs\ApexHighlights`, with a Start Menu entry, optional autostart and a clean uninstall. It asks for UAC once — that is to register the "identity package" that turns off Windows 11's capture border; clicking "No" still completes the install, you just keep the border.
- **Portable**: `ApexHighlights-<version>-portable.zip`, unzip and run, the config lives next to the exe.

Self-contained .NET runtime, the target machine needs nothing installed. First launch creates `captures/`, `games/` and `config.json` in the data folder (the embedded default config is extracted there and can then be edited in place). Delete the whole folder to reset.

If the exe sits somewhere read-only such as Program Files it automatically switches to `%LOCALAPPDATA%\ApexHighlights`; you can also point it anywhere with `--data=<dir>`.

**Note: the installer and the app UI are Chinese-only.** The CLI flags and the `config.json` keys are in English.

### System requirements

| Capability | Minimum system |
|---|---|
| Recording + audio (including "game only" process loopback) | Windows 10 2004 (10.0.19041) |
| Turning off the capture border (`IsBorderRequired`) | Windows 11 22H2+, installed via the installer, and the identity package agreed to |
| Hardware encoding | Any GPU with NVENC / QuickSync / VCE; falls back to H.264 when there is no HEVC encoder |

The border is Windows' own capture indicator (not drawn by this program). There is no way to turn it off on Windows 10, but it **never appears in the recording** (WGC only captures the window contents).

## Running it

```
# Tray mode (double-click the exe, or run-live.bat)
ApexHighlights.exe

# Foreground mode with an explicit window title (use any animated window while developing)
ApexHighlights.exe --title="BenchWnd" --fps=60 --bitrate=30 --duration=30 --fake-hit=12
```

| Flag | Meaning |
|---|---|
| `--data=<dir>` | Data folder (captures / games / config.json all live under it) |
| `--title=<substring>` | Window title substring; `self` means this program's own console window |
| `--fps=<n>` | Target frame rate, default 60 (30/60/120) |
| `--bitrate=<n>` | Bitrate in Mbps; if omitted, uses `BitrateMbps` from `config.json` (default 30) |
| `--gop=<n>` | GOP length in frames, defaults to fps |
| `--duration=<n>` | Exit after n seconds (runs forever if omitted) |
| `--fake-hit=<n>` | Fake a hit at second n, for end-to-end testing |
| `--no-audio` | Turn the audio track off entirely (same as setting the source to "off") |
| `--no-beep` | Turn recording notifications off (same as setting "notifications" to "off") |
| `--no-tray` | Console only, no tray icon |
| `--ui` | Open the library window at startup |
| `--config=<path>` | Config file, defaults to `config.json` in the data folder |
| `--log=<path>` | Diagnostic log (the previous one is archived); in tray mode it defaults to `live.log` in the data folder |

## Capture settings (录制参数 — tray → "Capture settings…", or the library toolbar)

| Item | Range | Default | What it does |
|---|---|---|---|
| Bitrate | 4–60 Mbps | 30 | Higher is clearer and bigger |
| Pre-roll (击杀前) | 0–30 s | 15 | How far back to pull before a hit (`0s` = nothing before the event) |
| Post-roll (击杀后) | 0–30 s | 15 | How long to keep recording after a hit (`0s` = ends immediately after the event) |
| Merge window (合并窗口) | 0–30 s | 8 | How close two hits must be to count as one fight (`0s` = one clip per hit) |
| Video codec | H.264 / HEVC | **HEVC** | See below |

Dragging takes effect immediately; releasing writes it to disk, there is no "Save" button. "Restore defaults" at the bottom = 30 / 15 / 15 / 8 / HEVC.

**About HEVC**: at the same bitrate it is cleaner than H.264, and at the same quality 30–50% smaller; the cost is that Windows' built-in player needs the "HEVC Video Extensions" (or use VLC / PotPlayer / mpv, which decode it out of the box). If the machine has no HEVC encoder it falls back to H.264 and says so in the log; switching codec restarts the recording once (a segment ring cannot be half one codec and half the other).

## Audio source (音轨 — tray → "Audio")

| Option | What is recorded | Notes |
|---|---|---|
| Game only (recommended, default) | Only the game process' own audio | Process loopback, so party chat and music never bleed in |
| System audio + microphone | Whole-device mix + your microphone | Pick this if you want your own shot-calling in the clip |
| System audio | Whole-device mix | Everything playing on the desktop |
| Off | Nothing | For when you want picture-only material |

The microphone is resampled and mixed into the same track (it never becomes a separate second track).

## Recording notifications (录制提示 — tray → "Notifications")

- **Slide-out card** (default): a 320×72 card slides out in the top-right corner, 1s in, 1.5s held, 0.5s out; once when recording starts and once when a clip is written.
- **Sound**: a single "ding", only when a clip is written.
- **Off**: nothing at all.

The card is its own window and never appears in the recording; the sound goes to the playback device, so in process-loopback mode it is never captured into the clip either.

## Library (回顾库 — tray → "Open library")

Left is the list, right is the detail pane (thumbnail + metadata); the top bar filters by kind / time / filename.

- **Play / Open folder / Choose folder / Delete / Capture settings** — `Choose folder` moves the clips to another drive (it asks whether to move the existing ones over).
- **Batch delete**: click `Delete` (删除) to enter batch mode, a rounded checkbox appears on every row, the `Select all` (全选) header ticks everything at once, and `Delete selected (N)` (删除选中) removes them in one go.
- **Quota and retention**: click "Quota 40GB · Keep 30 days" in the bottom-right corner to change the tiers directly.
- **F5** refresh, **Enter** play, **Delete** delete.

## Quota and retention

Defaults: **40GB / keep 30 days / 2GB disk watermark / check hourly**, stored in `config.json`:

```json
{ "QuotaGb": 40, "RetainDays": 30, "WatermarkGb": 2, "CheckMinutes": 60 }
```

- `QuotaGb: 0` = no size limit; `RetainDays: 0` = keep forever; `WatermarkGb: 0` = do not check the disk.
- Anything past the retention period is deleted outright; if the quota is still exceeded, clips are evicted by **knocked down > knocked > killed**, oldest first within the same priority.
- When free disk space drops below the watermark, writing new data into the ring pauses; clips already recorded still land normally.
- When it checks: at startup, after every clip is written, and once an hour.

A clip *is* its filename, there is no database: `apex_1028-130333_0.99.mp4`. To archive something by hand, just move the file.

## Banner detection

Hits are found by normalized cross-correlation against a "banner anchor image". Three templates ship built in — English / Simplified Chinese / Traditional Chinese (threshold 0.60) — and all three are scored together, so **whatever language the game is set to, it is recognised**.

Classification (knockdown / re-knock / assist knock / squad wipe / assist wipe) reads the banner text with Windows' built-in OCR, which covers Chinese and English; if the matching language pack is missing it degrades to "knockdown" but still records as usual.

Two common questions:

- **Resolution does not matter**: the band is scaled back into the 2560×1440 reference space while capturing; 900p / 1080p / 1440p / 2160p all pass the threshold in testing.
- **In-game HUD scaling does matter**: cross-correlation is very scale-sensitive (±10% drops it from 0.99 to 0.4). "HUD scale" (HUD 缩放) in the tray lets you pick 80/90/100/110/120% by hand; the default is "auto" — extra tiers are only scanned when a score lands in the grey zone, so it costs nothing the rest of the time.

## Self-check commands

None of these need the real game; each can be run on its own (prints `ok` / `NG`, a non-zero exit code means something failed):

| Command | What it verifies |
|---|---|
| `--gpu-test [--codec=hevc]` | The whole GPU passthrough encode path (NV12 conversion, bitrate, segments, MF readback) |
| `--av-test` | Two-track muxing (synthetic sine + gradient frames → an mp4 with one video and one audio track) |
| `--mp4-check <mp4> [--decode]` | MF reads back both streams' sample counts and last timestamps; `--decode` additionally decodes one frame for real |
| `--audio-check <mp4>` | Decodes the clip's audio track back to PCM and measures the peak (two tracks existing ≠ there being sound) |
| `--audio-test --sec=3` | WASAPI capture (device, mix format, effective sample rate, peak) |
| `--audio-probe[=ms]` / `--audio-probe-sta` | Whether process loopback actually gets packets (once on MTA, once on STA) |
| `--mic-test` | Microphone resampling + mixing math (synthetic waveforms) |
| `--ocr-parse-test` | Banner text classification (19 assertions across Chinese / English / Traditional) |
| `--ocr-langs` | Lists the OCR language packs installed on the system |
| `--band-check --dir=<dir> [--hud=110]` | Threshold regression: re-runs the detector over historical `hit_*.bmp` / `near_*.bmp` |
| `--band-tool=<screenshot.png> [--game=apex]` | Band calibrator: drag a box on the screenshot to get the `band` JSON, and save that crop as a template PNG |
| `--make-cover=<cover.png> [--cover-size=1920x1080]` | Renders the video cover (app icon ≈ knockdown skull) purely in code; change the colours/size and run it again |
| `--ocr-check --dir=<dir>` | Banner classification regression: re-runs OCR and lists what it recognised |
| `--policy-test` | `config.json` round-trips: defaults, migrating old configs, only writing its own fields |
| `--quota-test` | Quota / retention logic (fakes clips in a temp folder, never touches the real library) |
| `--ui-test [--dir=<clips folder>]` | The UI layer: list, filters, batch ticking, quota menu, settings panel |
| `--tray-test=N` | Tray self-test: menus, status line, pause, open library, change threshold, quit |
| `--lib-ui --shot=<png>` / `--shot-params=<png>` | Screenshot the UI (useful when changing the look) |

## Building

Needs the .NET 8 SDK and Windows 10 19041+:

```
dotnet build -c Release src/ApexHighlights/ApexHighlights.csproj
```

Depends on `Vortice.Direct3D11` / `Vortice.MediaFoundation` (restored automatically by NuGet).

To produce a distributable self-contained build (about a 73MB zip, runtime included):

```
dotnet publish src/ApexHighlights/ApexHighlights.csproj -c Release \
  -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o dist
```

The output is `dist/ApexHighlights.exe` plus a bunch of dlls; copy the whole `dist/` folder anywhere.

> **Why not `PublishSingleFile`**: a single-file build embeds the native libraries and unpacks them to `%TEMP%\.net\...` on every launch. On some machines that step is denied (`Failed to create directory ... for extracting bundled files`) and the exe simply will not start. The folder layout has no such trap.

One command for both the installer and the portable zip: `packaging/pack-release.sh` (needs `.tools/dotnet` and `ISCC.exe` from `.tools/inno setup`, see the header of the script).

## Known limitations

- Exclusive fullscreen may not be captured (WGC depends on DWM composition) — in fullscreen, use "borderless window".
- Adjacent hits within the merge window count as one fight and produce a single clip, so a clip appears about **8 seconds after the last hit**.
- If recording starts in the lobby and the window changes size once you are in a match, capture restarts automatically and that short stretch of frames cannot be recovered.
- For a custom game you have to fill in the band position yourself in `games/<id>.json`: drag a box with `--band-tool=<screenshot.png>` to generate it (see below). The resolution does not need re-calibrating — `band` lives in the "reference resolution" coordinate space and is scaled proportionally to the actual frame size while capturing.

## License

MIT, see [LICENSE](LICENSE).
