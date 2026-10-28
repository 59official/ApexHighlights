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
    private readonly List<SegmentRef> _segs = new();

    private readonly MemoryStream _payload = new();
    private readonly List<(long ptsUs, int size, bool key)> _samples = new();
    private long _curStartUs = -1;
    private long _lastPtsUs = -1;
    private int _seq;

    public byte[]? Init { get; private set; }
    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    public int KeyFrames { get; private set; }
    public long TotalSamples { get; private set; }

    public SegmentRing(string dir, int width, int height, int keepSeconds = 120)
    {
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
        var nals = Mp4.SplitAnnexB(annexB);
        if (nals.Count == 0) return false;

        bool key = false;
        foreach (var n in nals)
        {
            if (n.Type == 7 && Sps is null) { Sps = annexB.Slice(n.Start, n.Length).ToArray(); Init = null; }
            else if (n.Type == 8 && Pps is null) { Pps = annexB.Slice(n.Start, n.Length).ToArray(); Init = null; }
            else if (n.Type == 5) key = true;
        }
        if (Init is null && Sps is not null && Pps is not null)
            Init = Mp4.BuildInit(Sps, Pps, _w, _h);

        bool closed = false;
        if (key && _samples.Count > 0) { CloseSegment(); closed = true; }

        var avcc = Mp4.ToAvcc(annexB, nals);
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
            long d = (i + 1 < n ? _samples[i + 1].ptsUs : _samples[i].ptsUs + 16_667) - _samples[i].ptsUs;
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
            p.Value.AudioRate, p.Value.Asc, p.Value.Channels);
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
            p.Value.AudioRate, p.Value.Asc, p.Value.Channels);
    }

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
            return new Prepared(video, null, 0, null, 0);

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
