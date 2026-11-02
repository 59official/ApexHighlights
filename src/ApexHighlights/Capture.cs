using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using D3D11Device = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace ApexHighlights;

/// <summary>
/// WGC 窗口采集 → 条带检测 + 全帧编码。
///
/// 线程模型（GPU 直通）：
///   FrameArrived 线程：整帧不落 CPU —— BGRA 在 GPU 上经 VideoProcessor 转 NV12，
///     直喂 NVENC；检测条带单独 CopySubresourceRegion 小区域回读（10fps）。
///   编码线程：SubmitSurface（NV12 纹理 → NVENC），拿包写 1 秒分片环形。
/// 检测降到 10fps、编码跟满目标帧率：banner 持续约 2 秒，10fps 足够。
/// </summary>
internal sealed class Capture : IDisposable
{
    private readonly BannerEngine _eng;
    private readonly GameProfile _prof;
    private readonly byte[] _band;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();

    private ID3D11Device? _dev;
    private ID3D11DeviceContext? _ctx;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _bandStaging;   // 条带小回读（检测用，10fps）
    private ID3D11Texture2D? _dumpStaging;   // 整帧 dump 用（懒建，只 --dump 时用一次）
    private GpuConverter? _converter;

    // 条带几何（构造时按实际窗口尺寸定死，不随帧变）
    private readonly int _bw, _bh, _rx, _ry;

    // NV12 输出纹理池：GPU 转换结果，编码线程用完归还。
    // 8 个 in-flight 给 NVENC 的异步读取留足缓冲（避免它还没读完就被下一帧覆盖），
    // 也吸收编码线程的瞬时抖动（之前 3 个 → 30s 丢 39 帧）。
    // 池的容量上限 = MaxInFlight：归还时满了就 Dispose，否则空闲纹理会越攒越多。
    private readonly ConcurrentBag<ID3D11Texture2D> _texPool = new();
    private int _texInFlight;
    private const int MaxInFlight = 8;

    private EncoderChain? _enc;
    /// <summary>换编码器（运行期改码率）时的互斥：编码线程持锁提交，换引用的那一方也持锁，避免提交到已释放的实例。</summary>
    private readonly object _encSwap = new();
    private SegmentRing? _ring;
    private Thread? _encThread;
    private readonly BlockingCollection<(ID3D11Texture2D tex, long pts100)> _queue = new(new ConcurrentQueue<(ID3D11Texture2D, long)>(), 4);
    private volatile bool _stop;

    // 音轨（系统回环 + 可选麦克风 → 混音 → AAC → 内存环形）
    private AacEncoder? _aac;
    private AudioRing? _aRing;
    private readonly IntPtr _hwnd;         // 被录的那个窗口：进程回环要拿它的 PID
    private LoopbackCapture? _wcap;
    private LoopbackCapture? _game;        // 进程回环（只录游戏进程树）
    private Thread? _gameWatch;            // 进程回环的静默看门狗
    private LoopbackCapture? _mic;         // 麦克风采集（用户选了「整机 + 麦克风」才有）
    private MicMixer? _micMixer;           // 麦克风队列（由主时钟按帧取走）
    private Thread? _audioFill;
    /// <summary>音频喂数据的串行化：真声音来自 wasapi 线程，补的静音来自 fill 线程，编码器不是线程安全的。</summary>
    private readonly object _audioFeedGate = new();
    private long _audioFedFrames;          // 已喂给编码器的样点数（累计）
    private long _audioStart100 = long.MinValue;
    private long _silenceFilled;           // 补了多少样点的静音（诊断用）
    private byte[]? _zeroBuf;              // 静音补齐用的全零 PCM
    private string _audioName = "";
    private string _audioErr = "";
    private long _audioBytes;
    /// <summary>静音补齐线程的开关。不能复用 _stop —— 那个一关就再也开不起来了。</summary>
    private volatile bool _audioFillRun;
    /// <summary>环形容量（秒）：运行期重开音轨时要按同样的容量重建 AudioRing。</summary>
    private int _ringCapacitySec = 90;
    /// <summary>灰区下界：低于这个分数连「像横幅」都算不上，不值得补扫其它尺度。</summary>
    private const float GrayFloor = 0.40f;      // 灰区下限：再低就是噪声，补扫纯浪费
    /// <summary>
    /// 音轨档位，和 <see cref="CapturePolicy.Audio"/> 同义。外面只改这个 + <see cref="SetAudioMode"/>，
    /// 不要直接摸那几个采集源。
    /// </summary>
    public AudioMode Mode = AudioMode.GameOnly;
    /// <summary>命中前录多少秒。-1 = 用游戏档案里的值（老行为）。</summary>
    public int PreRollSec = -1;
    /// <summary>命中后录多少秒。-1 = 用游戏档案里的值。</summary>
    public int PostRollSec = -1;

    private long PreRollUs => (PreRollSec >= 0 ? PreRollSec : _prof.PreRollSec) * 1_000_000L;
    private long PostRollUs => (PostRollSec >= 0 ? PostRollSec : _prof.PostRollSec) * 1_000_000L;

    public int AudioRate = 48000;
    public int AudioChannels = 2;
    public int AudioBitrate = 160000;
    /// <summary>录完一段是否响一声（用户要的：有提示音、不弹窗）。</summary>
    /// <summary>
    /// 落盘完成的通知（种类 + 时长秒）。提示音/浮层由宿主决定 ——
    /// 录制器不该知道用户选的是「叮一声」还是「滑出一个小窗」。
    /// </summary>
    public event Action<ClipKind, double>? ClipSaved;

    private long _frames, _samples, _hits, _encoded, _droppedFrames, _totalMs;
    private long _limiterDrops, _capDrops, _queueDrops, _poolCreated;
    private long _lastAcceptedMs = long.MinValue;   // 帧限流用（见 OnFrame）
    public bool HasFiredFake { get; private set; }
    /// <summary>假命中连发几次、间隔多少毫秒 —— 专门复现「一次击倒连出 3 段」的 bug。</summary>
    public int FakeHitRepeat = 1;
    public int FakeHitGapMs = 700;
    private int _fakeFired;
    private long _lastDetectTicks, _lastHitMs = -10000, _lastNearMs = -10000;

    // 待切片段队列：命中时刻只登记，等环形真的录满后摇才落盘。
    // 为什么不能命中即切：命中那一瞬间环形只写到命中时刻，coverTo == hitMs，
    // 于是 stopUs 被夹成 hitMs，后摇 5 秒整段丢失（v1 实测 24s 命中出片只有 23.5s）。
    private readonly object _pendingGate = new();
    private readonly List<Detection> _pending = new();
    private Thread? _clipThread;

    // OCR 分类：只在 NCC 命中后才跑，且由 "clip" 线程独占（OcrEngine 非线程安全）
    private readonly BannerOcr _ocr = new();
    /// <summary>关掉横幅 OCR 分类（--no-ocr）。关掉后种类一律记为 Unknown，仍照常录制。</summary>
    public bool OcrEnabled = true;
    /// <summary>
    /// 合并窗口（spec §4）：相邻两次命中间隔 ≤ 这么多毫秒，视为同一场交火，切成一段。
    /// 0 = 关闭合并，每次命中各出一段。
    /// </summary>
    public int MergeMs = 8000;
    /// <summary>
    /// 这个种类要不要切片？null = 全录（老行为）。
    /// 托盘里给每个种类配了开关，用户明确说「不想录击倒/助攻击倒」时就靠它挡在落盘前——
    /// 拦在 WriteClip 之前而不是事后删文件：环形里那段画面还在，只是不生成 mp4。
    /// </summary>
    public Func<ClipKind, bool>? KindFilter { get; set; }
    /// <summary>这一种类是否允许切片。没装过滤器就算允许。</summary>
    private bool Records(ClipKind k) => KindFilter?.Invoke(k) ?? true;
    /// <summary>外面（自检/状态显示）想知道某个种类现在录不录。</summary>
    public bool WillRecord(ClipKind k) => Records(k);
    private long _merged, _ocrMs, _ocrCount;
    private bool _ocrHinted;

    // 配额/保留期（QuotaPolicy 默认 40GB / 30 天 / 2GB 水位）
    private ClipLibrary? _lib;
    private QuotaPolicy _policy = new();
    private long _lastQuotaMs = -1, _lastQuotaClipCount;
    private string _quotaMsg = "";
    /// <summary>磁盘跌破水位后置位：已完成的片段照常落盘，但不再往环形里堆新数据。</summary>
    public bool RecordingPaused { get; private set; }
    /// <summary>用户从托盘手动暂停（和磁盘水位那条互不干扰）。</summary>
    public bool UserPaused { get; set; }
    /// <summary>真的不往环形里写了吗（两条暂停原因合一，给 Stats/UI 用）。</summary>
    private bool Halted => RecordingPaused || UserPaused;
    private bool _wasAbove;
    private int _nearCount;
    private int _grayScans;        // 灰区补扫实际跑了几轮（诊断用：跑太多说明灰区门槛太低）
    private int _grayStreak;
    private long _lastGrayMs = -1000;
    private string _err = "";
    private string _encErr = "";

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int TargetFps { get; set; } = 60;
    public int EncodedFps { get; set; } = 60;
    /// <summary>每隔多少帧换一次编码器（=强制 IDR）。0 = 不换，全程一个会话。</summary>
    public int GopFrames { get; set; } = 60;
    public int DumpAtSec { get; set; }
    private bool _dumped;
    /// <summary>调试：启动 N 秒后伪造一次命中（验证切片链路，不依赖游戏）。</summary>
    public int FakeHitAtSec { get; set; }
    public int Bitrate { get; set; } = 20_000_000;
    /// <summary>视频编码。改它要重开采集（编码器和环形分片都是按编码建的）。</summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>这一场因为机器不支持 HEVC 而退回了 H.264（界面和日志要如实说明）。</summary>
    public bool CodecFellBack { get; private set; }
    public bool Encoding => _enc is not null;
    public int HotLoops { get; set; }

    public event Action<Detection>? Detected;

    public readonly record struct Detection(long Ms, float Score, int X, int Y, string? BandPath)
    {
        public DateTime WallClock { get; init; }
        public string Label { get; init; }
        public string? ClipPath { get; init; }
        public long Bytes { get; init; }
        /// <summary>OCR 读出的横幅种类（NCC 命中后才识别；识别失败时为 Unknown）。</summary>
        public BannerKind Kind { get; init; }
        /// <summary>OCR 读出的原始文案，仅供日志诊断。</summary>
        public string OcrText { get; init; }
    }

    public string OutDir { get; set; } = Paths.Captures;
    public string ClipDir { get; set; } = Paths.Clips;
    public string? RingDir { get; set; }

    public Capture(IntPtr hwnd, BannerEngine engine)
    {
        _hwnd = hwnd;
        _eng = engine;
        _prof = engine.Profile;
        _band = new byte[_prof.Band.W * _prof.Band.H];
        var item = Native.CreateItem(hwnd);
        // 4:2:0 要求偶数宽高；WGC 允许按我们给的尺寸缩放，所以直接取偶
        Width = item.Size.Width & ~1;
        Height = item.Size.Height & ~1;
        if (Width == 0) throw new InvalidOperationException("窗口尺寸为 0（最小化了？）");

        var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _dev!, out _ctx!);
        if (r.Failure) throw new InvalidOperationException($"D3D11CreateDevice 失败 0x{(int)r:X8}");

        // 条带几何按实际尺寸定死（与旧版一致：按 ref 尺寸等比缩放）
        _bw = (int)(_prof.Band.W * (double)Width / _prof.RefWidth);
        _bh = (int)(_prof.Band.H * (double)Height / _prof.RefHeight);
        _rx = (int)(_prof.Band.X * (double)Width / _prof.RefWidth);
        _ry = (int)(_prof.Band.Y * (double)Height / _prof.RefHeight);

        _bandStaging = _dev!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, _bw),
            Height = (uint)Math.Max(1, _bh),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        var winrtDevice = Native.ToWinRTDevice(_dev!);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
            new Windows.Graphics.SizeInt32 { Width = Width, Height = Height });
        _session = _pool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        // 能关就把那条黄框关掉（要包身份 + 用户授权过；关了不影响录到的画面）
        BorderlessAccess.TryEnable(_session);
        _pool.FrameArrived += (_, _) => OnFrame();
        _session.StartCapture();
        _clock.Start();
    }

    /// <summary>NV12 输出纹理：VideoProcessor 的 OutputView 要求 BindFlags=RenderTarget（Decoder 会 0x80070057）。</summary>
    private ID3D11Texture2D CreateNv12() => _dev!.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.NV12,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
    });

    /// <summary>挂上编码链路：采集的每一帧都会进 1 秒分片环形（GPU 直通）。</summary>
    public void EnableEncoding(int capacitySec)
    {
        var dir = RingDir ?? Path.Combine(OutDir, "ring");
        _ringCapacitySec = capacitySec;
        _converter = new GpuConverter(_dev!, _ctx!, Width, Height, EncodedFps);
        try
        {
            _enc = new EncoderChain(Width, Height, Bitrate, GopFrames, EncodedFps, _dev, Codec);
        }
        catch (Exception ex) when (Codec == VideoCodec.Hevc)
        {
            // 用户默认是 HEVC，但老显卡（或者只做了 H.264 的 MFT）可能根本没有 HEVC 编码器。
            // 这种情况下宁可整场退回 H.264 继续录，也不能让「录制」按钮直接哑掉。
            CodecFellBack = true;
            Codec = VideoCodec.H264;
            Log.Write($"[enc] HEVC 编码器起不来（{ex.Message}），本场改用 H.264");
            _enc = new EncoderChain(Width, Height, Bitrate, GopFrames, EncodedFps, _dev, Codec);
        }
        _ring = new SegmentRing(dir, Width, Height, capacitySec, Codec);
        _lib = new ClipLibrary(ClipDir);

        // 音轨：按用户选的那一档起（只录游戏 / 整机 / 整机+麦 / 不录）。
        // 编码器构造失败不该让视频也停掉，所以整段兜住（StartAudio 内部已经兜了）。
        if (Mode != AudioMode.None) StartAudio();

        _encThread = new Thread(EncodeLoop) { IsBackground = true, Name = "encode", Priority = ThreadPriority.AboveNormal };
        _encThread.Start();
    }

    /// <summary>
    /// 起音轨：AAC 编码器 + 音频环形 + 静音补齐线程，再按 <see cref="Mode"/> 起对应的采集源。
    /// 启动时和「用户换档位」时都走这里，两条路径不能有第二份实现。
    /// </summary>
    private void StartAudio()
    {
        if (_aac is not null) return;
        try
        {
            _aac = new AacEncoder(AudioRate, AudioChannels, AudioBitrate);
            _aRing = new AudioRing(_aac.SampleRate, _aac.Channels, _ringCapacitySec) { Asc = _aac.Asc };

            // 重开音轨必须把时间轴归零：Pump 用 _audioStart100 推算「壁钟上该有多少样本」，
            // 不重置的话第一帧就会一次性补进整场录制时长的静音（几百兆 PCM）。
            lock (_audioFeedGate)
            {
                _audioStart100 = long.MinValue;
                _audioFedFrames = 0;
                _silenceFilled = 0;
            }

            _audioFillRun = true;
            _audioFill = new Thread(AudioFillLoop) { IsBackground = true, Name = "audiofill" };
            _audioFill.Start();
            _audioErr = "";
        }
        catch (Exception ex)
        {
            _audioErr = "音频启动失败: " + ex.Message;
            _aac = null;
            _aRing = null;
            Log.Write("[audio] " + _audioErr);
            return;
        }

        switch (Mode)
        {
            case AudioMode.GameOnly:
                if (!StartGameSource()) StartSystemSource();     // 进程回环起不来就直接退整机
                break;
            case AudioMode.SystemAndMic:
                StartSystemSource();
                StartMicSource();
                break;
            case AudioMode.SystemOnly:
                StartSystemSource();
                break;
        }

        Log.Write($"[audio] 音轨已开启（{_aac!.SampleRate}Hz/{_aac.Channels}ch，{ModeName()}，" +
                  $"系统声={(SystemOn ? "开" : "关")}，游戏={(GameOn ? "开" : "关")}，" +
                  $"麦克风={(MicOn ? "开" : "关")}{(_audioErr.Length > 0 ? "，" + _audioErr : "")}）");
    }

    /// <summary>音轨档位的人话名字，日志和菜单共用。</summary>
    private string ModeName() =>
        CapturePolicy.AudioLabel(Mode) + (GameFellBack ? "（已回退系统声音）" : "");

    /// <summary>起系统回环采集（整机）。失败只记一笔：别的源还可能是好的。</summary>
    private void StartSystemSource()
    {
        if (_wcap is not null) return;
        try
        {
            var cap = new LoopbackCapture(AudioSourceKind.System);
            cap.Start(OnPcm);
            _wcap = cap;
            _audioName = cap.DeviceName;
            var dev = string.IsNullOrWhiteSpace(_audioName) || _audioName.StartsWith("{0.0.0.", StringComparison.Ordinal)
                ? "默认播放设备" : _audioName;
            Log.Write($"[audio] 系统声音开始采集（{dev}）");
        }
        catch (Exception ex)
        {
            _audioErr = "系统声音启动失败: " + ex.Message;
            Log.Write("[audio] " + _audioErr);
        }
    }

    private void StopSystemSource()
    {
        var wcap = _wcap;
        _wcap = null;
        try { wcap?.Dispose(); } catch { }
    }

    /// <summary>
    /// 起进程回环：只抓游戏进程（含子进程树）出声，桌面上的 Discord / 浏览器进不来。
    /// 返回 false 表示这条链路没建起来，调用方负责退回整机回环。
    /// </summary>
    private bool StartGameSource()
    {
        if (_game is not null) return true;
        try
        {
            int pid = ProcessIdOf(_hwnd);
            if (pid <= 0) throw new InvalidOperationException("拿不到窗口所属进程号");
            var cap = new LoopbackCapture(AudioSourceKind.GameProcess, pid);
            cap.Start(OnPcm);
            _game = cap;
            _audioName = "进程 " + pid;
            Log.Write($"[audio] 只录游戏：进程回环已开（pid {pid}，含子进程）");
            _gameWatch = new Thread(() => GameWatchdog(pid)) { IsBackground = true, Name = "audio-gamewatch" };
            _gameWatch.Start();
            return true;
        }
        catch (Exception ex)
        {
            _audioErr = "进程回环启动失败: " + ex.Message;
            Log.Write("[audio] " + _audioErr + "，改抓整机回环");
            return false;
        }
    }

    private void StopGameSource()
    {
        var g = _game;
        _game = null;
        try { g?.Dispose(); } catch { }
    }

    /// <summary>
    /// 进程回环的看门狗：3 秒一个包都没来，就退回整机回环。
    ///
    /// 进程回环坏起来有两种：激活时直接报错（好办，上面那条 catch 就兜住了），
    /// 或者激活成功但系统打死不给包——不报错、不抛异常，最后录出来一条无声片段，
    /// 那是最坏的失败方式（用户以为录到了）。所以只能等 3 秒看有没有数据。
    /// 误判的代价是混进了桌面的声音，比录出无声片段小得多。
    /// </summary>
    private void GameWatchdog(int pid)
    {
        Thread.Sleep(3000);
        var cap = _game;
        if (cap is null || !ReferenceEquals(_game, cap)) return;   // 这期间用户换档了
        if (cap.Bytes > 0 || cap.Packets > 0) return;
        Log.Write($"[audio] 进程回环 3 秒没有任何数据（pid {pid}），退回整机回环");
        try
        {
            StopGameSource();
            GameFellBack = true;
            StartSystemSource();
        }
        catch (Exception ex) { _audioErr = "回退整机回环失败: " + ex.Message; }
    }

    /// <summary>
    /// 起麦克风采集。设备没有/被独占/被系统禁麦都会失败——那只记一笔，不能把别的源也拖下水。
    /// </summary>
    private void StartMicSource()
    {
        if (_mic is not null || _aac is null) return;
        try
        {
            var mixer = new MicMixer(_aac.SampleRate, _aac.Channels);
            var cap = new LoopbackCapture(AudioSourceKind.Microphone);
            cap.Start((pcm, bytes) => mixer.Append(pcm, bytes, cap.SampleRate, cap.Channels));
            _micMixer = mixer;
            _mic = cap;
            Log.Write($"[audio] 麦克风开始采集（{cap.SampleRate}Hz/{cap.Channels}ch → 混进 {_aac.SampleRate}Hz/{_aac.Channels}ch）");
        }
        catch (Exception ex)
        {
            _mic = null;
            _micMixer = null;
            _audioErr = "麦克风启动失败: " + ex.Message;
            Log.Write("[audio] " + _audioErr);
        }
    }

    private void StopMicSource()
    {
        var mic = _mic;
        _mic = null;
        try { mic?.Dispose(); } catch { }
        _micMixer = null;
    }

    /// <summary>
    /// 关音轨：先停所有源（Dispose 内部会 Join 采集线程并 Stop 客户端），再放编码器和环形。
    /// 之后 WriteClip 传 null 的 AudioRing，Ring.Prepare 会自动退回「只有视频轨」的封装。
    /// </summary>
    private void StopAudio()
    {
        _audioFillRun = false;

        StopSystemSource();
        StopGameSource();
        StopMicSource();

        lock (_audioFeedGate)
        {
            try { _aac?.Dispose(); } catch { }
            _aac = null;
            _aRing = null;
        }
        _audioName = "";
        Log.Write("[audio] 音轨已关闭（后续片段为无声 mp4）");
    }

    /// <summary>音轨当前是不是真的在录（关掉或启动失败都为 false）。</summary>
    public bool AudioOn => _aac is not null;

    /// <summary>整机回环这个源在不在录。</summary>
    public bool SystemOn => _wcap is not null;

    /// <summary>进程回环（只录游戏）这个源在不在录。</summary>
    public bool GameOn => _game is not null;

    /// <summary>麦克风这个源在不在录。</summary>
    public bool MicOn => _mic is not null;

    /// <summary>「只录游戏」是不是已经因为进程回环起不来而退回整机了（托盘菜单要标出来）。</summary>
    public bool GameFellBack { get; private set; }

    /// <summary>
    /// 运行中换音轨档位：不重开录制、不重建视频链路，正在写的环形照常。
    /// 「不录声音」= 整条音轨关掉；其它档就是换源，编码器和时间轴都不动。
    /// </summary>
    public void SetAudioMode(AudioMode mode)
    {
        Mode = mode;
        GameFellBack = false;
        try
        {
            if (mode == AudioMode.None)
            {
                if (AudioOn) StopAudio();
                return;
            }

            if (!AudioOn)
            {
                StartAudio();            // 内部会按 Mode 起对应的源
                return;
            }

            bool wantSystem = mode is AudioMode.SystemOnly or AudioMode.SystemAndMic;
            bool wantGame = mode == AudioMode.GameOnly;
            bool wantMic = mode == AudioMode.SystemAndMic;

            if (wantGame && !GameOn)
            {
                StopSystemSource();
                if (!StartGameSource()) StartSystemSource();
            }
            else if (wantSystem && !SystemOn)
            {
                StopGameSource();
                StartSystemSource();
            }

            if (wantMic && !MicOn) StartMicSource();
            else if (!wantMic && MicOn) StopMicSource();

            Log.Write($"[audio] 音轨来源改为：{ModeName()}" +
                      $"（系统声={(SystemOn ? "开" : "关")}，游戏={(GameOn ? "开" : "关")}，麦克风={(MicOn ? "开" : "关")}）");
        }
        catch (Exception ex) { _audioErr = "切换音源失败: " + ex.Message; }
    }

    /// <summary>窗口所属进程号（进程回环要它）。拿不到就返回 0。</summary>
    private static int ProcessIdOf(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid > int.MaxValue ? 0 : (int)pid;
        }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);


    /// <summary>
    /// 运行中改码率：EncoderChain 的码率是 readonly（NVENC 的初始化参数），
    /// 所以只能换一台新的 —— 先建好再原子换引用，编码线程的每一次提交都在这把锁里，
    /// 换完才释放旧的，绝不会有帧提交到已 Dispose 的编码器上。
    ///
    /// 注意：新编码器吐出的 SPS/PPS 会被 SegmentRing 丢掉（它只记第一份），
    /// 因为分辨率/profile 没变，两份参数集完全一样，所以无害；
    /// 哪天允许改分辨率了，这里就是第一个要动的地方。
    /// </summary>
    public void SetBitrate(int bps)
    {
        Bitrate = Math.Clamp(bps, 2_000_000, 200_000_000);
        var old = _enc;
        if (old is null) return;          // 还没起编码：EnableEncoding 时会用新 Bitrate

        EncoderChain fresh;
        try { fresh = new EncoderChain(Width, Height, Bitrate, GopFrames, EncodedFps, _dev, Codec); }
        catch (Exception ex)
        {
            _encErr = "改码率失败: " + ex.Message;
            Log.Write("[bitrate] " + _encErr);
            return;
        }

        lock (_encSwap)
        {
            old = _enc;
            _enc = fresh;
        }
        try { old?.Dispose(); } catch { }
        Log.Write($"[bitrate] 码率改为 {Bitrate / 1_000_000}Mbps（已重建编码器，环形继续写）");
    }


    /// <summary>
    /// 运行中换成片目录（托盘「片段目录」里选完立刻生效）。
    ///
    /// 只动成片目录：<see cref="OutDir"/>（命中条带）和环形分片仍留在数据目录里，
    /// 它们只是临时/调试产物。库对象得跟着换一个，否则配额清理还在扫旧目录。
    /// 旧目录里的片段不会被搬走 —— 那是界面层问过用户之后的事。
    /// </summary>
    public void SetClipDir(string dir)
    {
        var full = Paths.NormalizeDir(dir);
        if (full.Length == 0) return;
        ClipDir = full;
        _lib = new ClipLibrary(full);
        try { Directory.CreateDirectory(full); } catch { }
        Log.Write($"[clip] 成片目录改为 {full}");
    }

    /// <summary>配额策略（外部可改，线程安全地换引用即可）。</summary>
    public QuotaPolicy Quota
    {
        get => _policy;
        set { _policy = value; _lastQuotaMs = -1; }
    }

    /// <summary>最近一次配额检查的说明，给日志/UI 用。</summary>
    public string QuotaMessage => _quotaMsg;

    /// <summary>累计到达的帧数。托盘用它判断采集是不是卡住了（窗口还在但不再出帧）。</summary>
    public long FrameCount => _frames;

    /// <summary>实际生效的命中阈值（用户覆盖优先，否则取模板自带的）。</summary>
    public float Threshold => _eng.EffectiveThreshold;

    /// <summary>
    /// 运行中改阈值：立刻对下一次检测生效，不需要重开录制，也不重建引擎。
    /// 这就是用户要的「自己调阈值」——托盘菜单和回顾库里都挂了入口。
    /// </summary>
    public void SetThreshold(float v)
    {
        var p = DetectPolicy.Clamp(new DetectPolicy { Threshold = v });
        _eng.ThresholdOverride = p.Threshold;
        // 阈值一变就清掉「上一帧是否超阈」的状态：否则从 0.9 调到 0.55 后，
        // 持续存在的画面会因为 _wasAbove 还是 true 而永不触发下降沿。
        _wasAbove = false;
        Log.Write($"[threshold] 命中阈值改为 {_eng.EffectiveThreshold:F2}（{DetectPolicy.HintFor(p.Threshold)}）");
    }

    /// <summary>
    /// 合并窗口：相邻命中间隔不超过这个时长就算同一场交火，只切一段。
    /// 横幅会在屏幕上停留 2–3 秒、每 minGapMs 再触发一次，单靠「上升沿」并不够。
    /// </summary>
    public void SetMergeMs(int ms)
    {
        MergeMs = Math.Clamp(ms, 0, 60_000);
        Log.Write($"[merge] 合并窗口改为 {(MergeMs == 0 ? "关（每次命中各出一段）" : MergeMs / 1000.0 + "s")}");
    }

    /// <summary>
    /// 「近失」采样阈值：留证据用的，永远低于命中阈值。
    /// 用户把命中阈值调到 0.9 时，如果近失还按 0.45 采样，会写一堆对调参毫无用处的图；
    /// 反过来阈值调到 0.55 时，0.45 又太低。所以按「命中阈值 - 0.15」跟着走，并夹在合理区间。
    /// </summary>
    private static float NearFloor(float hitThreshold) => Math.Clamp(hitThreshold - 0.15f, 0.40f, 0.80f);

    /// <summary>
    /// 跑一次配额/保留期清理并更新水位状态。返回报告（库没起来时返回 null）。
    /// 触发点：启动时、每片落盘后、每小时。
    /// </summary>
    public QuotaReport? RunQuotaCheck()
    {
        var lib = _lib;
        if (lib is null) return null;
        try
        {
            var rep = QuotaEnforcer.Run(lib, _policy);
            _lastQuotaMs = _clock.ElapsedMilliseconds;
            _lastQuotaClipCount = rep.Kept;
            _quotaMsg = rep.ToString();
            if (rep.DeletedTotal > 0)
                Console.WriteLine($"[quota] {rep}");

            // 水位：跌破就暂停环形写入（已完成的片段照常落盘，不丢已录好的）
            bool low = QuotaEnforcer.BelowWatermark(ClipDir, _policy);
            if (low && !RecordingPaused)
            {
                RecordingPaused = true;
                _quotaMsg += "  磁盘低于水位，已暂停录制";
                Console.WriteLine($"[quota] 可用 {QuotaReport.Mb(QuotaEnforcer.FreeBytes(ClipDir))} 低于水位 " +
                                  $"{_policy.WatermarkGb}GB —— 暂停录制");
            }
            else if (!low && RecordingPaused)
            {
                RecordingPaused = false;
                Console.WriteLine("[quota] 磁盘水位恢复，继续录制");
            }
            return rep;
        }
        catch (Exception ex) { _quotaMsg = "配额检查失败: " + ex.Message; return null; }
    }

    /// <summary>每小时（以及每片落盘后）检查一次，别在主循环里频繁扫目录。</summary>
    private void MaybeQuotaCheck(bool force)
    {
        if (_lib is null) return;
        long now = _clock.ElapsedMilliseconds;
        if (!force && _lastQuotaMs >= 0 && now - _lastQuotaMs < _policy.CheckMinutes * 60_000L) return;
        RunQuotaCheck();
    }

    /// <summary>WASAPI 回调（在 "wasapi" 线程上）。时间戳直接取采集用的同一个 Stopwatch，和视频同轴。</summary>
    private void OnPcm(byte[] pcm, int bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _audioBytes, bytes);
        Pump(pcm, bytes, _clock.ElapsedMilliseconds * 10_000L);
    }

    /// <summary>
    /// 往音频编码器喂数据，顺便补齐静音。
    ///
    /// 为什么需要补：这台机器的 WASAPI 回环在**完全安静**时一个 packet 都不送（`--audio-test` 静音时 0 包）。
    /// 于是安静时段 AAC 一帧都不出，音频环只覆盖零星几百毫秒，片段里就干脆没有音轨
    /// （`BuildClip` 见音频帧为空会退回无音轨封装）。补静音让音频时间轴始终连续覆盖整段录制。
    ///
    /// 时间轴由 AacEncoder 按累计样点数推算，所以这里只要保证「喂进去的样本数 ≈ 壁钟走过的样本数」。
    /// </summary>
    private void Pump(byte[]? pcm, int bytes, long now100)
    {
        var aac = _aac;
        var ring = _aRing;
        if (aac is null || ring is null) return;

        try
        {
            lock (_audioFeedGate)
            {
                if (_audioStart100 == long.MinValue) _audioStart100 = now100;

                // 静音补齐：只补到「壁钟应有的样本数」，不留未来
                long expected = (now100 - _audioStart100) * aac.SampleRate / 10_000_000L;
                long shortfall = expected - _audioFedFrames;
                if (shortfall >= 1024)
                {
                    long fill = shortfall / 1024 * 1024;      // 整帧补，避免制造半个 AAC 帧
                    _zeroBuf ??= new byte[1024 * 4 * 8];
                    int per = _zeroBuf.Length / 4;            // 每次喂多少样点
                    var mixer = _micMixer;
                    while (fill > 0)
                    {
                        int take = (int)Math.Min(per, fill);
                        // 补静音的位置如果麦克风有货，就用麦克风顶上：
                        // 游戏安静、只有用户在说话时，这段不能是纯静音（否则只开麦克风等于没录）。
                        if (mixer is not null) mixer.TakeInto(_zeroBuf, take * 4);
                        aac.Feed(_zeroBuf.AsSpan(0, take * 4), now100, (d, p) => ring.Append(p / 10, d));
                        _audioFedFrames += take;
                        _silenceFilled += take;
                        fill -= take;
                    }
                }

                if (pcm is not null && bytes > 0)
                {
                    // 就地混：pcm 是采集线程的 _stage，本次回调用完就覆盖，不会有人再看。
                    _micMixer?.MixInto(pcm, bytes);
                    aac.Feed(pcm.AsSpan(0, bytes), now100, (d, p) => ring.Append(p / 10, d));
                    _audioFedFrames += bytes / 4;
                }
            }
        }
        catch (Exception ex) { if (_audioErr.Length == 0) _audioErr = ex.Message; }
    }

    /// <summary>静音补齐线程：WASAPI 不送包的时候也得让音频时间轴往前走。</summary>
    private void AudioFillLoop()
    {
        while (!_stop && _audioFillRun)
        {
            Thread.Sleep(20);
            if (!_audioFillRun) break;
            Pump(null, 0, _clock.ElapsedMilliseconds * 10_000L);
        }
    }

    /// <summary>把 NV12 纹理归还池。空闲槽满就直接释放，避免纹理越攒越多。</summary>
    private void ReturnNv12(ID3D11Texture2D tex)
    {
        if (_texPool.Count < MaxInFlight) _texPool.Add(tex);
        else tex.Dispose();
    }

    private void EncodeLoop()
    {
        while (!_stop && !_queue.IsCompleted)
        {
            (ID3D11Texture2D tex, long pts100) item;
            try { item = _queue.Take(); } catch { break; }
            if (item.tex is null) continue;
            try
            {
                // 持锁提交：改码率时换引用的那一方也拿这把锁，所以这里拿到的实例一定还没被 Dispose。
                lock (_encSwap)
                {
                    var enc = _enc;
                    if (enc is not null)
                    {
                        enc.SubmitSurface(item.pts100, item.tex, (data, cbPts) => _ring!.Append(cbPts / 10, data));
                        _encoded++;
                    }
                }
            }
            catch (Exception ex) { if (_encErr.Length == 0) _encErr = ex.Message; }
            finally
            {
                Interlocked.Decrement(ref _texInFlight);
                ReturnNv12(item.tex);
            }
        }
    }

    private void OnFrame()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            using var frame = _pool!.TryGetNextFrame();
            if (frame is null) return;
            var tex = Native.TextureOf(frame);
            if (tex is null) return;
            try
            {
                _frames++;
                var now = _clock.ElapsedMilliseconds;

                // 显示刷新率比目标帧率高的时候（165/179Hz 屏很常见），WGC 会按刷新率送帧。
                // 全收下来等于用 60fps 的码控去压 160fps 的内容：码率翻 2.6 倍、GOP 从 1 秒
                // 缩成 0.37 秒、GPU 白烧。按目标帧率放行，留 25% 抖动余量。
                if (_lastAcceptedMs != long.MinValue &&
                    now - _lastAcceptedMs < 1000.0 / Math.Max(1, TargetFps) * 0.75)
                {
                    _droppedFrames++; _limiterDrops++;
                    return;
                }
                _lastAcceptedMs = now;
                // 不来一帧限流：WGC 的 FrameArrived 本身就是采集帧率（≈窗口刷新率），
                // 逐帧限流会误杀 60fps 流里 ±1~2ms 的抖动帧（历史遗留，早期 CPU 探针时代才需要）。
                // 背压只靠纹理池上限（GPU 端）和队列容量（编码端）；检测仍单独 10fps 限流。

                // 编码：GPU 直通（BGRA → NV12 全在 GPU，整帧不落 CPU）
                if (_enc is not null && !Halted)
                {
                    if (Interlocked.Increment(ref _texInFlight) > MaxInFlight)
                    {
                        Interlocked.Decrement(ref _texInFlight);
                        _droppedFrames++; _capDrops++;
                    }
                    else
                    {
                        ID3D11Texture2D nv12Tex;
                        if (_texPool.TryTake(out var t)) nv12Tex = t;
                        else { nv12Tex = CreateNv12(); _poolCreated++; }   // 没从池里拿到 = 新建
                        try
                        {
                            _converter!.Convert(tex, nv12Tex);
                            if (_queue.TryAdd((nv12Tex, now * 10_000))) { nv12Tex = null!; /* 所有权交给队列 */ }
                            else { _droppedFrames++; _queueDrops++; }
                        }
                        catch (Exception ex) { if (_encErr.Length == 0) _encErr = "conv: " + ex.Message; }
                        finally
                        {
                            if (nv12Tex is not null)
                            {
                                Interlocked.Decrement(ref _texInFlight);
                                _texPool.Add(nv12Tex);
                            }
                        }
                    }
                }

                // 一次性整帧 dump（仅 --dump 触发，独立懒建 staging）
                if (DumpAtSec > 0 && now >= DumpAtSec * 1000L && !_dumped)
                {
                    _dumped = true;
                    DumpFrame(tex);
                }

                // 检测降到 10fps
                if (now - _lastDetectTicks < 100) return;
                _lastDetectTicks = now;

                MaybeQuotaCheck(false);

                ReadBand(tex);

                if (FakeHitAtSec > 0 && _fakeFired < FakeHitRepeat
                    && now >= FakeHitAtSec * 1000L + (long)_fakeFired * FakeHitGapMs)
                {
                    HasFiredFake = true;
                    // 按真实时间间隔逐个触发（不是一次性排队），这样合并窗口的判据能被真实验证
                    QueueClip(new Detection(now, 0.99f - _fakeFired * 0.01f, 0, 0, null)
                    { WallClock = DateTime.Now, Label = "fake" });
                    _fakeFired++;
                }

                var sw = Stopwatch.StartNew();
                var det = _eng.Score(_band);

                // 灰区补扫（用户没指定 HUD 缩放时才做）：横幅肯定在，只是尺度对不上。
                //
                // 为什么要三条件：补扫一轮 = 4 档 × 3 张模板 = 12 遍 NCC。真机日志里
                // 命中率明明很高，检测却平均 28ms/帧 —— 横幅淡出、半截露出、别的 UI 文字
                // 都会掉进灰区，那几帧每帧都白扫 12 遍。所以要求「连续两帧都在灰区」
                // （瞬间的噪声过不了）且「距上次补扫 ≥150ms」（一段横幅最多补几次）。
                if (_eng.HudScalePct == 0 && det.Score >= GrayFloor && det.Score < det.Threshold)
                {
                    if (++_grayStreak >= 2 && now - _lastGrayMs >= 150)
                    {
                        _lastGrayMs = now;
                        _grayScans++;
                        det = _eng.ScoreScaled(_band, det);
                    }
                }
                else _grayStreak = 0;
                sw.Stop();
                _samples++; _totalMs += sw.ElapsedMilliseconds;

                bool above = det.Score >= det.Threshold;
                if (above && !_wasAbove && now - _lastHitMs >= (long)_prof.MinGapMs)
                {
                    _lastHitMs = now;
                    _hits++; HotLoops++;
                    Directory.CreateDirectory(OutDir);
                    var path = Path.Combine(OutDir, $"hit_{now / 1000:00000}_{det.Score:F2}.bmp");
                    BandBmp.Write(path, _band, _prof.Band.W, _prof.Band.H);
                    // 只登记，不在这里切：切片段线程会等后摇录满再落盘
                    QueueClip(new Detection(now, det.Score, det.X, det.Y, path)
                    {
                        WallClock = DateTime.Now,
                        Label = det.Label,
                    });
                }                else if (!above && det.Score >= NearFloor(det.Threshold) && now - _lastNearMs >= 2000 && _nearCount < 40)
                {
                    _lastNearMs = now; _nearCount++;
                    Directory.CreateDirectory(OutDir);
                    BandBmp.Write(Path.Combine(OutDir, $"near_{now / 1000:00000}_{det.Score:F2}.bmp"), _band, _prof.Band.W, _prof.Band.H);
                }
                _wasAbove = above;
            }
            finally { tex.Dispose(); }
        }
        catch (Exception ex)
        {
            if (_err.Length == 0) _err = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>只把条带小区域从 GPU 拷进小 staging 再回读（检测用，10fps）。</summary>
    private unsafe void ReadBand(ID3D11Texture2D tex)
    {
        var box = new Vortice.Mathematics.Box(_rx, _ry, 0, _rx + _bw, _ry + _bh, 1);
        _ctx!.CopySubresourceRegion(_bandStaging!, 0, 0, 0, 0, tex, 0, box);
        var map = _ctx.Map(_bandStaging!, 0, MapMode.Read);
        try { unsafe { ExtractBand((byte*)map.DataPointer, (int)map.RowPitch, _bw, _bh); } }
        finally { _ctx.Unmap(_bandStaging!, 0); }
    }

    private void DumpFrame(ID3D11Texture2D tex)
    {
        try
        {
            _dumpStaging ??= _dev!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _ctx!.CopyResource(_dumpStaging, tex);
            var map = _ctx.Map(_dumpStaging, 0, MapMode.Read);
            try
            {
                var buf = new byte[Width * Height * 4];
                unsafe
                {
                    var src = (byte*)map.DataPointer;
                    int pitch = (int)map.RowPitch;
                    fixed (byte* dst = buf)
                        for (int y = 0; y < Height; y++)
                            Buffer.MemoryCopy(src + (long)y * pitch, dst + (long)y * Width * 4, Width * 4, Width * 4);
                }
                WriteBmp(buf, Width, Height, Path.Combine(OutDir, "dump.bmp"));
                Console.WriteLine($"[dump] {Path.Combine(OutDir, "dump.bmp")} {Width}x{Height}");
            }
            finally { _ctx.Unmap(_dumpStaging, 0); }
        }
        catch (Exception ex) { if (_err.Length == 0) _err = "dump: " + ex.Message; }
    }

    /// <summary>
    /// 命中：先登记，交给切片段线程等后摇录满再落盘。
    /// 立刻切的话 coverTo 只到命中时刻，后摇会整段丢掉。
    /// </summary>
    private void QueueClip(Detection det)
    {
        lock (_pendingGate) _pending.Add(det);
        if (_clipThread is null)
        {
            _clipThread = new Thread(ClipLoop) { IsBackground = true, Name = "clip" };
            _clipThread.Start();
        }
    }

    /// <summary>
    /// 等「命中时刻 + 后摇」真的录进环里，再切、再落盘、再报事件。
    ///
    /// 合并（spec §4「相邻击倒间隔 ≤ 8s 合并为一段」）：Apex 的横幅在屏幕上停留
    /// 两秒多，期间每过 minGapMs 就会再命中一次 —— 结果是一次击倒连出 3 段视频
    /// （实测 19:43:06 / 19:43:07 / 19:43:08 三条）。靠文案判重不管用：玩家名
    /// 中英混排，OCR 每次读出来的乱码都不一样。所以改成按**时间**合并：从第一次
    /// 命中起，MergeMs 内再来的命中都并进同一段，片段范围取「首命中 − 前摇」到
    /// 「末命中 + 后摇」。停顿超过 MergeMs 才算下一场交火。
    /// </summary>
    private void ClipLoop()
    {
        while (!_stop)
        {
            Thread.Sleep(100);

            Detection first;
            lock (_pendingGate)
            {
                if (_pending.Count == 0) continue;
                first = _pending[0];
                _pending.RemoveAt(0);
            }

            var group = new List<Detection> { first };
            long lastMs = first.Ms;
            long started = _clock.ElapsedMilliseconds;

            // 一边等后摇、一边继续吸纳合并窗口内的新命中
            while (!_stop)
            {
                lock (_pendingGate)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        var p = _pending[i];
                        if (p.Ms - lastMs > MergeMs) continue;   // 太远，留给下一场
                        group.Add(p);
                        _pending.RemoveAt(i);
                        if (p.Ms > lastMs) lastMs = p.Ms;
                    }
                }

                bool ringReady = false;
                if (_ring is not null)
                {
                    var (_, coverTo) = _ring.Coverage();
                    ringReady = coverTo >= lastMs * 1000L + PostRollUs;
                }
                long quiet = _clock.ElapsedMilliseconds - lastMs;

                // 后摇录满了、并且安静够一个合并窗口，才算这一场结束
                if (ringReady && quiet >= MergeMs) break;
                // 兜底：环形已经滑过足够远（比如命中后立刻暂停/退出），不再干等
                if (_clock.ElapsedMilliseconds - started > MergeMs + 3000 + PostRollUs / 1000) break;

                Thread.Sleep(80);
            }

            // 种类/分数取分数最高的那一次（横幅最完整、OCR 最准）
            var rep = group[0];
            foreach (var g in group) if (g.Score > rep.Score) rep = g;
            var d = Classify(rep);

            // 最高分那帧有时正是横幅被人物模型挡住一半的时候，OCR 会返回「未识别」。
            // 这时再试剩下几帧（最多 2 次），拿到种类就收手——
            // 否则助攻击倒会被写成普通击倒（文件名少个 _ak_），事后分不出来。
            if (d.Kind == BannerKind.Unknown && group.Count > 1)
            {
                foreach (var g in group.OrderByDescending(x => x.Score))
                {
                    if (ReferenceEquals(g, rep) || g.BandPath is null) continue;
                    var alt = Classify(g);
                    if (alt.Kind == BannerKind.Unknown) continue;
                    d = d with { Kind = alt.Kind, OcrText = alt.OcrText };
                    break;
                }
            }

            // 横幅是渐入的：最高分那帧可能正好只画出后半截「擊倒」，「助攻，」还没显出来，
            // OCR 就会读成普通击倒（文件名少个 _ak_，事后分不出来）。
            // 击倒属于粗判，所以再翻一遍同组其它帧，谁读到更具体的种类就用谁。
            if (d.Kind == BannerKind.Knock && group.Count > 1)
            {
                foreach (var g in group.OrderByDescending(x => x.Score))
                {
                    if (ReferenceEquals(g, rep) || g.BandPath is null) continue;
                    var alt = Classify(g);
                    // 只接受「助攻击倒」这一种升级：别的字（淘汰）在噪声里也偶尔出现，
                    // 放开会让普通击倒被误判成淘汰，得不偿失。
                    if (alt.Kind == BannerKind.AssistKnock)
                    {
                        Log.Write($"[ocr] 更具体的种类：{ClipKinds.Label(ClipKinds.FromBanner(d.Kind))} → " +
                                  $"{ClipKinds.Label(ClipKinds.FromBanner(alt.Kind))}（读到「{alt.OcrText}」）");
                        d = d with { Kind = alt.Kind, OcrText = alt.OcrText };
                        break;
                    }
                }
            }

            if (group.Count > 1)
            {
                _merged += group.Count - 1;
                Log.Write($"[merge] {group.Count} 次命中合并为一段（{group[0].Ms / 1000.0:F1}s–{lastMs / 1000.0:F1}s），种类取 {BannerOcr.Label(d.Kind)}");
            }

            // 用户在托盘里把这个种类关掉了：这一场就不落盘了，直接等下一场。
            // d.Kind 是横幅种类（BannerKind），开关用的是落盘种类（ClipKind），走 FromBanner 对齐，
            // 否则未识别的横幅（Unknown → 击倒）会和用户勾的「击倒」对不上。
            var kind = ClipKinds.FromBanner(d.Kind);
            if (!Records(kind))
            {
                Log.Write($"[skip] 种类「{ClipKinds.Label(kind)}」已关，跳过切片（{group[0].Ms / 1000.0:F1}s–{lastMs / 1000.0:F1}s）");
                continue;
            }

            var clip = WriteClip(d, group[0].Ms, lastMs);
            Detected?.Invoke(d with { ClipPath = clip.Path, Bytes = clip.Bytes });
        }
    }

    /// <summary>
    /// 读条带图上的横幅文案，判断这是击倒/再次击倒/助攻击倒/小队淘汰/助攻淘汰中的哪一种。
    /// 只由 "clip" 线程调用（OcrEngine 非线程安全）。
    /// OCR 失败不是错误：返回 Unknown，照样录，只是文件名不带种类后缀。
    /// </summary>
    private Detection Classify(Detection det)
    {
        if (!OcrEnabled || det.BandPath is null) return det;
        try
        {
            var (kind, text) = _ocr.Classify(det.BandPath);
            _ocrCount = _ocr.Count;
            _ocrMs = _ocr.TotalMs;
            if (!_ocrHinted && _ocr.Hint.Length > 0) { _ocrHinted = true; Log.Write($"[ocr] {_ocr.Hint}"); }
            return det with { Kind = kind, OcrText = text };
        }
        catch (Exception ex)
        {
            if (_err.Length == 0) _err = "ocr: " + ex.Message;
            return det;
        }
    }

    /// <summary>退出时把队列里还没切的片段切掉（后摇可能没录满，但前摇画面不能白丢）。</summary>
    private void FlushPending()
    {
        Detection[] batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }
        // 同样按合并窗口分组，避免退出瞬间把一场交火补切成好几段
        int i = 0;
        while (i < batch.Length)
        {
            long firstMs = batch[i].Ms, lastMs = firstMs;
            int j = i + 1;
            while (j < batch.Length && batch[j].Ms - lastMs <= MergeMs) { lastMs = batch[j].Ms; j++; }
            var rep = batch[i];
            for (int k = i; k < j; k++) if (batch[k].Score > rep.Score) rep = batch[k];
            var done = Classify(rep);
            // 退出补切也守同一条闸门，否则「关了击倒」在关程序那一刻还是会漏一段出来
            var doneKind = ClipKinds.FromBanner(done.Kind);
            bool keep = Records(doneKind);
            var clip = keep ? WriteClip(done, firstMs, lastMs) : (Path: null, Bytes: 0L);
            if (clip.Path is not null) Log.Write($"[CLIP] {clip.Path}（退出时补切）");
            else if (!keep) Log.Write($"[skip] 退出补切时种类「{ClipKinds.Label(doneKind)}」已关，丢弃 {firstMs / 1000.0:F1}s 那一段");
            i = j;
        }
    }

    /// <summary>按前摇/后摇从环形里切一片出来写成 mp4。片段范围是 [首命中−前摇, 末命中+后摇]。</summary>
    private (string? Path, long Bytes) WriteClip(Detection det, long firstMs, long lastMs)
    {
        if (_ring is null || _ring.Segments.Count == 0) return (null, 0);
        try
        {
            var (coverFrom, coverTo) = _ring.Coverage();
            long endUs = coverTo;
            long startUs = Math.Max(coverFrom, firstMs * 1000L - PreRollUs);
            long stopUs = Math.Min(endUs, lastMs * 1000L + PostRollUs);
            if (stopUs - startUs < 500_000) return (null, 0);

            Directory.CreateDirectory(ClipDir);
            var name = $"{_prof.Id}{KindToken(det.Kind)}_{DateTime.Now:MMdd-HHmmss}_{det.Score:F2}.mp4";
            var path = Path.Combine(ClipDir, name);

            // 流式写：先写 .tmp 再改名。
            // 原来是把整个片段拼成一个 byte[] 再 WriteAllBytes —— 30 秒 20Mbps 就是
            // 内存里同时存在「样本字节 + 同样大的输出数组」，峰值直接翻倍。
            // 先写 tmp 的意义是：中途失败或进程被杀，库里不会留一个能播一半的残片。
            var tmp = path + ".tmp";
            long written;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    written = _ring.WriteClip(fs, startUs, stopUs, _aRing);
                if (written == 0) { try { File.Delete(tmp); } catch { } return (null, 0); }
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }

            // 把两条轨的实际长度写进日志：以后有人报「音画不同步」，第一眼就能看出是
            // 时间轴对不上（两个数差很多）还是内容对不上（两个数一样但听着错位）。
            // 顺手把实测码率打出来：画面「糊/脏」时先看这个数，比猜快
            double secs = Math.Max(0.001, (stopUs - startUs) / 1_000_000.0);
            Log.Write($"[clip] {name} {written / (1024 * 1024)}MB {written * 8 / secs / 1_000_000:F1}Mbps {_ring.LastReport}");

            // 提示交给宿主（托盘）：这里只报「存好了、是什么、多长」
            ClipSaved?.Invoke(ClipKinds.FromBanner(det.Kind), Math.Max(0, stopUs - startUs) / 1_000_000.0);
            // 新片落盘后立即查一次配额（spec §5 的清理触发点之一）
            MaybeQuotaCheck(true);
            return (path, written);
        }
        catch (Exception ex) { if (_encErr.Length == 0) _encErr = "clip: " + ex.Message; return (null, 0); }
    }

    /// <summary>
    /// 种类 → 文件名后缀。文件名是回顾库的唯一索引源（见 ClipLibrary 的注释），
    /// 所以种类必须落在名字里，不能只存在内存中。
    /// 刻意用短记号而不是中文：这个库要能跨语言环境、跨系统工具打开。
    /// </summary>
    private static string KindToken(BannerKind k) => k switch
    {
        BannerKind.ReKnock => "_rk",
        BannerKind.AssistKnock => "_ak",
        BannerKind.Elim => "_el",
        BannerKind.AssistElim => "_ae",
        _ => "",   // 击倒（默认种类）与未识别都不加后缀，保持 v1 的老命名
    };

    private unsafe void ExtractBand(byte* src, int pitch, int bw, int bh)
    {
        int Luma(int x, int y)
        {
            var p = src + y * pitch + x * 4;
            return (p[0] * 114 + p[1] * 587 + p[2] * 299 + 500) / 1000;
        }

        if (bw == _prof.Band.W && bh == _prof.Band.H)
        {
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    _band[y * _prof.Band.W + x] = (byte)Luma(x, y);
            return;
        }

        for (int y = 0; y < _prof.Band.H; y++)
        {
            double fy = (y + 0.5) * bh / _prof.Band.H - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, bh - 1), y1 = Math.Min(y0 + 1, bh - 1);
            double wy = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < _prof.Band.W; x++)
            {
                double fx = (x + 0.5) * bw / _prof.Band.W - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, bw - 1), x1 = Math.Min(x0 + 1, bw - 1);
                double wx = Math.Clamp(fx - x0, 0, 1);
                double v = (Luma(x0, y0) * (1 - wx) + Luma(x1, y0) * wx) * (1 - wy)
                         + (Luma(x0, y1) * (1 - wx) + Luma(x1, y1) * wx) * wy;
                _band[y * _prof.Band.W + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        }
    }

    /// <summary>把 BGRA 缓冲写成 BMP（自下而上）。</summary>
    private static void WriteBmp(byte[] bgra, int w, int h, string path)
    {
        int rowSize = w * 4, imgSize = rowSize * h, fileSize = 54 + imgSize;
        var hdr = new byte[54];
        hdr[0] = (byte)'B'; hdr[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(hdr, 2);
        BitConverter.GetBytes(54).CopyTo(hdr, 10);
        BitConverter.GetBytes(40).CopyTo(hdr, 14);
        BitConverter.GetBytes(w).CopyTo(hdr, 18);
        BitConverter.GetBytes(h).CopyTo(hdr, 22);
        BitConverter.GetBytes((short)1).CopyTo(hdr, 26);
        BitConverter.GetBytes((short)32).CopyTo(hdr, 28);
        BitConverter.GetBytes(imgSize).CopyTo(hdr, 34);
        using var fs = File.Create(path);
        fs.Write(hdr);
        var row = new byte[rowSize];
        for (int y = h - 1; y >= 0; y--)   // BMP 自下而上
        {
            Buffer.BlockCopy(bgra, y * rowSize, row, 0, rowSize);
            fs.Write(row);
        }
    }

    public string Stats()
    {
        var s = $"帧={_frames} 采样={_samples} 事件={_hits} 近失={_nearCount} 灰扫={_grayScans}";
        if (_samples > 0) s += $" 检测={(_totalMs / (double)_samples):F1}ms";
        var enc = _enc;                       // 改码率会换实例，只读一次，免得拼出半个旧半个新
        if (enc is not null)
        {
            s += $" 编码={_encoded} 丢帧={_droppedFrames}(限{_limiterDrops}/池{_capDrops}/队{_queueDrops}) 建池={_poolCreated} 编码器丢={enc.Dropped}";
            if (_ring is not null) s += $" 环={_ring.Segments.Count}段/{_ring.Coverage()}";
            s += $" 码率={Bitrate / 1_000_000}Mbps 编码={enc.Name} 旋={enc.Rotations} gop[{NvEncoder.GopInfo}] q[{NvEncoder.QualityInfo}]";
        }
        if (_aRing is not null)
        {
            var ac = _aRing.Coverage();
            s += $" 音频={_aRing.TotalFrames}帧/{_aRing.DiskBytes() / 1024}KB 覆盖({ac.From / 1000},{ac.To / 1000})ms";
            s += $" 源[系统={(_wcap is null ? "关" : "开")} 游戏={(_game is null ? "关" : "开")}{(GameFellBack ? "回退" : "")}" +
                 $" 麦={(MicOn ? "开" : "关")}" +
                 $"{(MicOn ? $"/{_micMixer?.Frames ?? 0}帧缓" : "")}]";
        }
        else if (Mode == AudioMode.None) s += " 音频=关";
        if (OcrEnabled && _ocrCount > 0)
            s += $" OCR={_ocrCount}次/{_ocrMs}ms 合并={_merged}次";
        if (_ocr.Err.Length > 0) s += $"  ocrErr={_ocr.Err}";
        if (_err.Length > 0) s += $"  err={_err}";
        if (_encErr.Length > 0) s += $"  encErr={_encErr}";
        if (_audioErr.Length > 0) s += $"  audioErr={_audioErr}";
        if (_lib is not null) s += $" 库={_lastQuotaClipCount}片 配额{_policy.QuotaGb}GB/{_policy.RetainDays}天" +
            $"{(RecordingPaused ? " [已暂停:磁盘水位]" : UserPaused ? " [已暂停:手动]" : "")}";
        return s;
    }

    public void Dispose()
    {
        _stop = true;
        _audioFillRun = false;      // 静音补齐线程自己会退出（它每 20ms 看一次这个标志）
        try { _queue.CompleteAdding(); } catch { }
        // 退出前把还没切完的片段切掉（后摇可能没录满，但前摇画面不能浪费）
        try { FlushPending(); } catch { }
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { _wcap?.Dispose(); } catch { }
        try { _mic?.Dispose(); } catch { }
        try { _ring?.Flush(); } catch { }
        try { _enc?.Dispose(); } catch { }
        try { _aac?.Dispose(); } catch { }
        try { _encThread?.Join(2000); } catch { }
        try { _audioFill?.Join(1000); } catch { }
        try { _clipThread?.Join(2000); } catch { }
        _converter?.Dispose();
        foreach (var t in _texPool) try { t.Dispose(); } catch { }
        _bandStaging?.Dispose();
        _dumpStaging?.Dispose();
        _ctx?.Dispose();
        _dev?.Dispose();
    }
}

internal static class BandBmp
{
    public static void Write(string path, byte[] gray, int w, int h)
    {
        int rowSize = (w * 3 + 3) & ~3;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int size = 54 + rowSize * h;
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(size); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h);
        bw.Write((short)1); bw.Write((short)24);
        bw.Write(0); bw.Write(rowSize * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        var row = new byte[rowSize];
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = gray[y * w + x];
                row[x * 3] = v; row[x * 3 + 1] = v; row[x * 3 + 2] = v;
            }
            bw.Write(row);
        }
    }
}
