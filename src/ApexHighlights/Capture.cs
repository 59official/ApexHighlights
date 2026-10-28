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
    private readonly ConcurrentBag<ID3D11Texture2D> _texPool = new();
    private int _texInFlight;
    private const int MaxInFlight = 8;

    private EncoderChain? _enc;
    private SegmentRing? _ring;
    private Thread? _encThread;
    private readonly BlockingCollection<(ID3D11Texture2D tex, long pts100)> _queue = new(new ConcurrentQueue<(ID3D11Texture2D, long)>(), 4);
    private volatile bool _stop;

    // 音轨（系统回环 → AAC → 内存环形）
    private AacEncoder? _aac;
    private AudioRing? _aRing;
    private LoopbackCapture? _wcap;
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
    public bool AudioEnabled = true;
    public int AudioRate = 48000;
    public int AudioChannels = 2;
    public int AudioBitrate = 160000;
    /// <summary>录完一段是否响一声（用户要的：有提示音、不弹窗）。</summary>
    public bool BeepOnSave = true;

    private long _frames, _samples, _hits, _encoded, _droppedFrames, _totalMs;
    private long _limiterDrops, _capDrops, _queueDrops, _poolCreated;
    public bool HasFiredFake { get; private set; }
    private long _lastDetectTicks, _lastHitMs = -10000, _lastNearMs = -10000;

    // 待切片段队列：命中时刻只登记，等环形真的录满后摇才落盘。
    // 为什么不能命中即切：命中那一瞬间环形只写到命中时刻，coverTo == hitMs，
    // 于是 stopUs 被夹成 hitMs，后摇 5 秒整段丢失（v1 实测 24s 命中出片只有 23.5s）。
    private readonly object _pendingGate = new();
    private readonly List<Detection> _pending = new();
    private Thread? _clipThread;

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
    public bool Encoding => _enc is not null;
    public int HotLoops { get; set; }

    public event Action<Detection>? Detected;

    public readonly record struct Detection(long Ms, float Score, int X, int Y, string BandPath)
    {
        public DateTime WallClock { get; init; }
        public string Label { get; init; }
        public string? ClipPath { get; init; }
        public long Bytes { get; init; }
    }

    public string OutDir { get; set; } = @"D:\played\captures";
    public string ClipDir { get; set; } = @"D:\played\captures\clips";
    public string? RingDir { get; set; }

    /// <summary>逐帧诊断 CSV（t_ms,score,x,y）；为空则不写。</summary>
    public string? DiagPath { get; set; }
    private StreamWriter? _diag;

    private void Diag(long ms, float score, int x, int y)
    {
        if (DiagPath is null) return;
        try
        {
            _diag ??= new StreamWriter(DiagPath, append: false) { AutoFlush = false };
            _diag.WriteLine($"{ms},{score:F4},{x},{y}");
        }
        catch { DiagPath = null; }
    }

    public Capture(IntPtr hwnd, BannerEngine engine)
    {
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
        _converter = new GpuConverter(_dev!, _ctx!, Width, Height, EncodedFps);
        _enc = new EncoderChain(Width, Height, Bitrate, GopFrames, EncodedFps, _dev);
        _ring = new SegmentRing(dir, Width, Height, capacitySec);
        _lib = new ClipLibrary(ClipDir);

        // 音轨：顺手起了系统回环采集；麦克风不碰（用户只要游戏声）。
        // 编码器构造失败不该让视频也停掉，所以整段兜住。
        if (AudioEnabled)
        {
            try
            {
                _aac = new AacEncoder(AudioRate, AudioChannels, AudioBitrate);
                _aRing = new AudioRing(_aac.SampleRate, _aac.Channels, capacitySec) { Asc = _aac.Asc };
                _wcap = new LoopbackCapture();
                _wcap.Start(OnPcm);
                _audioName = _wcap.DeviceName;
            }
            catch (Exception ex)
            {
                _audioErr = "音频启动失败: " + ex.Message;
                _aac = null;
                _aRing = null;
            }
        }

        _encThread = new Thread(EncodeLoop) { IsBackground = true, Name = "encode", Priority = ThreadPriority.AboveNormal };
        _encThread.Start();

        // 静音补齐：WASAPI 在完全安静时不送包，靠这个线程按壁钟把音频时间轴推上去
        if (_aac is not null)
        {
            _audioFill = new Thread(AudioFillLoop) { IsBackground = true, Name = "audiofill" };
            _audioFill.Start();
        }
    }

    /// <summary>配额策略（外部可改，线程安全地换引用即可）。</summary>
    public QuotaPolicy Quota
    {
        get => _policy;
        set { _policy = value; _lastQuotaMs = -1; }
    }

    /// <summary>最近一次配额检查的说明，给日志/UI 用。</summary>
    public string QuotaMessage => _quotaMsg;

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
                    while (fill > 0)
                    {
                        int take = (int)Math.Min(per, fill);
                        aac.Feed(_zeroBuf.AsSpan(0, take * 4), now100, (d, p) => ring.Append(p / 10, d));
                        _audioFedFrames += take;
                        _silenceFilled += take;
                        fill -= take;
                    }
                }

                if (pcm is not null && bytes > 0)
                {
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
        while (!_stop)
        {
            Thread.Sleep(20);
            Pump(null, 0, _clock.ElapsedMilliseconds * 10_000L);
        }
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
                _enc!.SubmitSurface(item.pts100, item.tex, (data, cbPts) => _ring!.Append(cbPts / 10, data));
                _encoded++;
            }
            catch (Exception ex) { if (_encErr.Length == 0) _encErr = ex.Message; }
            finally
            {
                Interlocked.Decrement(ref _texInFlight);
                _texPool.Add(item.tex);
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
                        var nv12Tex = _texPool.TryTake(out var t) ? t : CreateNv12();
                        if (_texPool.Count == 0) _poolCreated++;
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

                if (FakeHitAtSec > 0 && now >= FakeHitAtSec * 1000L && !HasFiredFake)
                {
                    HasFiredFake = true;
                    QueueClip(new Detection(now, 0.99f, 0, 0, null) { WallClock = DateTime.Now, Label = "fake" });
                }

                var sw = Stopwatch.StartNew();
                var det = _eng.Score(_band);
                sw.Stop();
                _samples++; _totalMs += sw.ElapsedMilliseconds;

                Diag(now, det.Score, det.X, det.Y);
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
                }                else if (!above && det.Score >= 0.45f && now - _lastNearMs >= 2000 && _nearCount < 40)
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

    /// <summary>等「命中时刻 + 后摇」真的录进环里，再切、再落盘、再报事件。</summary>
    private void ClipLoop()
    {
        while (!_stop)
        {
            Thread.Sleep(100);
            Detection[] batch;
            lock (_pendingGate)
            {
                if (_pending.Count == 0) continue;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var det in batch)
            {
                long wantUs = det.Ms * 1000L + _prof.PostRollSec * 1_000_000L;
                // 最多多等 3 秒：环形已经滑过足够远（比如命中后立刻暂停）就不再干等
                long giveUp = _clock.ElapsedMilliseconds;
                while (!_stop && _ring is not null)
                {
                    var (_, coverTo) = _ring.Coverage();
                    if (coverTo >= wantUs) break;
                    if (_stop || _clock.ElapsedMilliseconds - giveUp > 3000 + _prof.PostRollSec * 1000L) break;
                    Thread.Sleep(80);
                }

                var clip = WriteClip(det.Ms, det.Score);
                Detected?.Invoke(det with { ClipPath = clip.Path, Bytes = clip.Bytes });
            }
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
        foreach (var det in batch)
        {
            var clip = WriteClip(det.Ms, det.Score);
            if (clip.Path is not null) Console.WriteLine($"[CLIP] {clip.Path}（退出时补切）");
        }
    }

    /// <summary>按前摇/后摇从环形里切一片出来写成 mp4。</summary>
    private (string? Path, long Bytes) WriteClip(long hitMs, float score)
    {
        if (_ring is null || _ring.Segments.Count == 0) return (null, 0);
        try
        {
            var (coverFrom, coverTo) = _ring.Coverage();
            long endUs = coverTo;
            long startUs = Math.Max(coverFrom, hitMs * 1000L - _prof.PreRollSec * 1_000_000L);
            long stopUs = Math.Min(endUs, hitMs * 1000L + _prof.PostRollSec * 1_000_000L);
            if (stopUs - startUs < 500_000) return (null, 0);

            var bytes = _ring.BuildClip(startUs, stopUs, _aRing);
            Directory.CreateDirectory(ClipDir);
            var name = $"{_prof.Id}_{DateTime.Now:MMdd-HHmmss}_{score:F2}.mp4";
            var path = Path.Combine(ClipDir, name);
            File.WriteAllBytes(path, bytes);
            if (BeepOnSave) Beep.Saved();
            // 新片落盘后立即查一次配额（spec §5 的清理触发点之一）
            MaybeQuotaCheck(true);
            return (path, bytes.Length);
        }
        catch (Exception ex) { if (_encErr.Length == 0) _encErr = "clip: " + ex.Message; return (null, 0); }
    }

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
        var s = $"帧={_frames} 采样={_samples} 事件={_hits} 近失={_nearCount}";
        if (_samples > 0) s += $" 检测={(_totalMs / (double)_samples):F1}ms";
        if (_enc is not null)
        {
            s += $" 编码={_encoded} 丢帧={_droppedFrames}(限{_limiterDrops}/池{_capDrops}/队{_queueDrops}) 建池={_poolCreated} 编码器丢={_enc.Dropped}";
            if (_ring is not null) s += $" 环={_ring.Segments.Count}段/{_ring.Coverage()}";
            s += $" 编码={_enc.Name} 旋={_enc.Rotations} gop[{NvEncoder.GopInfo}]";
        }
        if (_aRing is not null)
        {
            var ac = _aRing.Coverage();
            s += $" 音频={_aRing.TotalFrames}帧/{_aRing.DiskBytes() / 1024}KB 覆盖({ac.From / 1000},{ac.To / 1000})ms";
        }
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
        try { _queue.CompleteAdding(); } catch { }
        // 退出前把还没切完的片段切掉（后摇可能没录满，但前摇画面不能浪费）
        try { FlushPending(); } catch { }
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { _diag?.Flush(); _diag?.Dispose(); } catch { }
        try { _wcap?.Dispose(); } catch { }
        try { _ring?.Flush(); } catch { }
        try { _enc?.Dispose(); } catch { }
        try { _aac?.Dispose(); } catch { }
        try { _encThread?.Join(2000); } catch { }
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
