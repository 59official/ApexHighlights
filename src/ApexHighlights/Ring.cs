namespace ApexHighlights;

/// <summary>环形缓冲里的一段（= 一个 GOP 的 fMP4 media segment，独立文件）。</summary>
public readonly record struct SegmentRef(int Seq, long StartUs, long EndUs, string Path);

/// <summary>
/// v1 的环形缓冲：分片文件环。
/// 契约（换成 (c) 预分配单文件环时只需换实现）：
///   Append(ptsUs, annexB) → 追加一帧
///   Segments              → 当前保留下来的段
///   BuildClip(startUs, endUs) → 可播放的 fMP4 字节流
/// </summary>
public sealed class SegmentRing
{
    private readonly string _dir;
    private readonly int _w, _h;
    private readonly int _keepSeconds;
    private readonly VideoCodec _codec;
    private readonly long _frameUs;
    private readonly List<SegmentRef> _segs = new();

    private readonly MemoryStream _payload = new();
    private readonly List<(long ptsUs, int size, bool key)> _samples = new();
    private long _curStartUs = -1;
    private long _lastPtsUs = -1;
    private int _seq;

    public byte[]? Init { get; private set; }
    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    /// <summary>HEVC 才有的 VPS；H.264 恒为 null。</summary>
    public byte[]? Vps { get; private set; }
    public VideoCodec Codec => _codec;
    public int KeyFrames { get; private set; }
    public long TotalSamples { get; private set; }

    public SegmentRing(string dir, int width, int height, int keepSeconds = 120, VideoCodec codec = VideoCodec.H264,
        int fps = 60)
    {
        _codec = codec;
        _frameUs = 1_000_000L / Math.Max(1, fps);
        _dir = dir;
        _w = width; _h = height;
        _keepSeconds = keepSeconds;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "seg_*.mp4")) File.Delete(f);
    }

    public IReadOnlyList<SegmentRef> Segments => _segs;

    /// <summary>追加一帧（Annex-B 形式的 access unit）。返回是否产生了新的段。</summary>
    public bool Append(long ptsUs, ReadOnlySpan<byte> annexB)
    {
        bool hevc = _codec == VideoCodec.Hevc;
        var nals = Mp4.SplitAnnexB(annexB, hevc);
        if (nals.Count == 0) return false;

        // H.264：SPS=7 PPS=8 IDR=5；HEVC：VPS=32 SPS=33 PPS=34，IRAP 是 16..23（BLA/IDR/CRA）
        int vpsType = 32, spsType = hevc ? 33 : 7, ppsType = hevc ? 34 : 8;
        bool key = false;
        foreach (var n in nals)
        {
            if (hevc && n.Type == vpsType && Vps is null) { Vps = annexB.Slice(n.Start, n.Length).ToArray(); Init = null; }
            else if (!hevc && n.Type == spsType && Sps is null) { Sps = annexB.Slice(n.Start, n.Length).ToArray(); Init = null; }
            else if (hevc && n.Type == spsType && Sps is null) { Sps = annexB.Slice(n.Start, n.Length).ToArray(); Init = null; }
            else if (n.Type == ppsType && Pps is null) { Pps = annexB.Slice(n.Start, n.Length).ToArray(); Init = null; }
            else if (hevc ? (n.Type >= 16 && n.Type <= 23) : n.Type == 5) key = true;
        }
        if (Init is null && Sps is not null && Pps is not null && (!hevc || Vps is not null))
            Init = Mp4.BuildInit(Sps, Pps, _w, _h, _codec, Vps);

        bool closed = false;
        if (key && _samples.Count > 0) { CloseSegment(); closed = true; }

        // 样本里剔掉参数集：hvc1/avc1 的参数集只允许在 hvcC/avcC 里（array_completeness 也写了 1），
        // 流内再带一份就是规范冲突 —— 解码器宽容时没事，严格的一律报错或重复注入。
        var keep = new List<Mp4.Nal>(nals.Count);
        foreach (var n in nals)
        {
            bool paramSet = hevc
                ? (n.Type == 32 || n.Type == 33 || n.Type == 34)
                : (n.Type == 7 || n.Type == 8);
            if (!paramSet) keep.Add(n);
        }
        if (keep.Count == 0) keep = nals;      // 整帧都是参数集（不该发生），别把自己搞成空样本
        var avcc = Mp4.ToAvcc(annexB, keep);
        _payload.Write(avcc, 0, avcc.Length);
        _samples.Add((ptsUs, avcc.Length, key));
        if (key) KeyFrames++;
        TotalSamples++;
        if (_curStartUs < 0) _curStartUs = ptsUs;
        _lastPtsUs = ptsUs;
        return closed;
    }

    private void CloseSegment()
    {
        if (_samples.Count == 0) return;
        int n = _samples.Count;
        var specs = new SegSample[n];
        for (int i = 0; i < n; i++)
        {
            long d = (i + 1 < n ? _samples[i + 1].ptsUs : _samples[i].ptsUs + _frameUs) - _samples[i].ptsUs;
            int ticks = (int)Math.Round(d * Mp4.Timescale / 1_000_000.0);
            specs[i] = new SegSample(Math.Max(1, ticks), _samples[i].size, _samples[i].key);
        }
        ulong baseTime = (ulong)Math.Round(_curStartUs * Mp4.Timescale / 1_000_000.0);
        var seg = Mp4.BuildSegment((uint)_seq, baseTime, specs, _payload.ToArray());
        var path = Path.Combine(_dir, $"seg_{_seq:D6}.mp4");
        File.WriteAllBytes(path, seg);
        _segs.Add(new SegmentRef(_seq, _curStartUs, _lastPtsUs, path));
        _seq++;

        _payload.SetLength(0);
        _samples.Clear();
        _curStartUs = -1;
        Trim();
    }

    private void Trim()
    {
        long newest = _segs.Count > 0 ? _segs[^1].EndUs : 0;
        while (_segs.Count > 1 && newest - _segs[0].StartUs > _keepSeconds * 1_000_000L)
        {
            try { File.Delete(_segs[0].Path); } catch { }
            _segs.RemoveAt(0);
        }
    }

    /// <summary>收尾：把当前没写完的段落盘。</summary>
    public void Flush() => CloseSegment();

    public void Dispose() { }

    /// <summary>拼一个可播放的普通 mp4：读回段文件 → 按时间裁剪 → 从关键帧起 → 写 ftyp/moov/mdat。</summary>
    public byte[] BuildClip(long startUs, long endUs) => BuildClip(startUs, endUs, null);

    /// <summary>
    /// 带音轨的版本。视频会回退到关键帧，所以音轨也必须对齐到同一个起点：
    /// 先算出第一帧视频的绝对时间，再把音频帧的相对时间戳减掉它。
    /// 这个重载把结果拼进 byte[]（自检工具用）；真正落盘走 <see cref="WriteClip(Stream,long,long,AudioRing?)"/>。
    /// </summary>
    public byte[] BuildClip(long startUs, long endUs, AudioRing? audio)
    {
        var p = Prepare(startUs, endUs, audio);
        if (p is null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        Mp4Writer.Write(ms, Sps!, Pps!, _w, _h, p.Value.Video, p.Value.Audio,
            p.Value.AudioRate, p.Value.Asc, p.Value.Channels, _codec, Vps);
        return ms.ToArray();
    }

    /// <summary>
    /// 直接写进目标流（通常是 FileStream）。
    /// 为什么要加这个：原来 BuildClip 返回 byte[] 再 File.WriteAllBytes，
    /// 切片瞬间内存里同时存在「全部样本字节」和「同样大的输出数组」，
    /// 30 秒 20Mbps 的片段就是两个 80MB —— 那是工作集峰值的主要来源。
    /// </summary>
    /// <returns>写出的字节数；没有可用分段时返回 0。</returns>
    public long WriteClip(Stream dst, long startUs, long endUs, AudioRing? audio)
    {
        var p = Prepare(startUs, endUs, audio);
        if (p is null) return 0;
        return Mp4Writer.Write(dst, Sps!, Pps!, _w, _h, p.Value.Video, p.Value.Audio,
            p.Value.AudioRate, p.Value.Asc, p.Value.Channels, _codec, Vps);
    }

    /// <summary>最近一次 Prepare 的音视频长度说明，给 WriteClip 写日志用。</summary>
    public string LastReport { get; private set; } = "";

    private readonly record struct Prepared(
        List<Mp4Writer.ClipSample> Video,
        List<Mp4Writer.ClipSample>? Audio,
        int AudioRate, byte[]? Asc, int Channels);

    /// <summary>选段、回退到关键帧、对齐音轨时间戳。BuildClip 与 WriteClip 共用。</summary>
    private Prepared? Prepare(long startUs, long endUs, AudioRing? audio)
    {
        if (Sps is null || Pps is null) throw new InvalidOperationException("还没拿到 SPS/PPS");
        var chosen = _segs.Where(s => s.EndUs >= startUs && s.StartUs <= endUs).ToList();
        if (chosen.Count == 0) return null;

        var all = new List<Mp4Writer.ClipSample>();
        foreach (var s in chosen) all.AddRange(Mp4Writer.ReadSegment(File.ReadAllBytes(s.Path)));
        if (all.Count == 0) return null;

        long startTicks = startUs <= 0 ? 0 : startUs * Mp4.Timescale / 1_000_000;
        int first = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].DtsTicks <= startTicks) first = i;
            else break;
        }
        while (first > 0 && !all[first].Sync) first--;

        long endTicks = endUs >= long.MaxValue / Mp4.Timescale ? long.MaxValue : endUs * Mp4.Timescale / 1_000_000;
        int last = all.Count - 1;
        for (int i = first; i < all.Count; i++)
        {
            if (all[i].DtsTicks <= endTicks) last = i;
            else break;
        }

        var video = all.GetRange(first, last - first + 1);

        if (audio is null || audio.TotalFrames == 0)
        {
            LastReport = $"视频 {video.Sum(x => (double)x.DurTicks) / Mp4.Timescale:F2}s/{video.Count}帧 音频 无";
            return new Prepared(video, null, 0, null, 0);
        }

        // 视频第一帧的绝对微秒时间，作为两条轨共同的 0 点
        long firstUs = all[first].DtsTicks * 1_000_000L / Mp4.Timescale;
        var frames = audio.BuildClip(firstUs, endUs);
        var aSamples = new List<Mp4Writer.ClipSample>(frames.Count);
        foreach (var f in frames)
        {
            long dts = (f.PtsUs - firstUs) * audio.SampleRate / 1_000_000L;
            if (dts < 0) continue;
            aSamples.Add(new Mp4Writer.ClipSample(dts, 1024, true, f.Aac));
        }
        if (aSamples.Count == 0)
            return new Prepared(video, null, 0, null, 0);

        double vDur = video.Sum(x => (double)x.DurTicks) / Mp4.Timescale;
        double aDur = (double)aSamples.Count * 1024 / audio.SampleRate;
        // 音频帧的 DtsTicks 是「从片头算起的采样数」，除采样率就是它落在片头的第几秒。
        // 正常情况下第一帧应该是 0.00~0.02s；差出 0.1 秒以上就说明两条轨的时间基准没对齐。
        double aStart = aSamples[0].DtsTicks / (double)audio.SampleRate;
        LastReport = $"视频 {vDur:F2}s/{video.Count}帧 音频 {aDur:F2}s/{aSamples.Count}帧 音频起点 {aStart:F3}s";
        return new Prepared(video, aSamples, audio.SampleRate, audio.Asc, audio.Channels);
    }

    /// <summary>整段覆盖时间范围（用于报告）</summary>
    public (long from, long to) Coverage()
        => _segs.Count == 0 ? (0, 0) : (_segs[0].StartUs, _segs[^1].EndUs);

    public long DiskBytes()
    {
        long n = 0;
        foreach (var s in _segs) { try { n += new FileInfo(s.Path).Length; } catch { } }
        return n;
    }
}
