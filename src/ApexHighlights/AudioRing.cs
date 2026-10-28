using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ApexHighlights;

/// <summary>内存里的一帧 AAC（裸 access unit，不带 ADTS 头）。</summary>
public sealed class AudioFrame
{
    public long PtsUs;
    public byte[] Aac = Array.Empty<byte>();
}

/// <summary>
/// 音频环形缓冲。故意放在内存里而不是像视频那样落盘：
/// 160 kbps 的 AAC 只有 20 KB/s，90 秒也就 1.8 MB，比视频（8 Mbps → 90 MB）小两个数量级，
/// 所以省掉一套分段文件格式、省掉音频线程上的磁盘 IO、也省掉过期文件清理。
/// 时间轴与视频共用 Capture 的 Stopwatch：ptsUs 就是从同一个钟上取的微秒。
/// </summary>
public sealed class AudioRing
{
    private readonly object _lock = new();
    private readonly LinkedList<AudioFrame> _frames = new();
    private readonly int _keepSeconds;
    private readonly int _capacityFrames;

    public int SampleRate { get; }
    public int Channels { get; }
    /// <summary>AudioSpecificConfig，来自编码器输出类型的 UserData，写 mp4 的 esds 用。</summary>
    public byte[] Asc = Array.Empty<byte>();

    public long TotalFrames => _count;
    private long _count;
    private long _bytes;
    private long _firstPtsUs = -1;
    private long _lastPtsUs = -1;

    public AudioRing(int sampleRate, int channels, int keepSeconds = 120)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _keepSeconds = keepSeconds;
        // 一帧 1024 样本；留 2 倍余量防止 trim 抖动
        _capacityFrames = (int)((long)keepSeconds * sampleRate / 1024 * 2 + 64);
    }

    public void Append(long ptsUs, byte[] aac)
    {
        if (aac is null || aac.Length == 0) return;
        lock (_lock)
        {
            _frames.AddLast(new AudioFrame { PtsUs = ptsUs, Aac = aac });
            _count++;
            _bytes += aac.Length;
            if (_firstPtsUs < 0) _firstPtsUs = ptsUs;
            if (ptsUs > _lastPtsUs) _lastPtsUs = ptsUs;
            Trim();
        }
    }

    /// <summary>丢掉超出保留窗口的旧帧（按时间裁，不按条数，避免码率变化时裁错）。</summary>
    private void Trim()
    {
        while (_frames.Count > 0)
        {
            var f = _frames.First!.Value;
            if (_frames.Count > _capacityFrames || (_lastPtsUs - f.PtsUs) > _keepSeconds * 1_000_000L)
            {
                _frames.RemoveFirst();
                _bytes -= f.Aac.Length;
                continue;
            }
            break;
        }
        _firstPtsUs = _frames.Count > 0 ? _frames.First!.Value.PtsUs : -1;
    }

    /// <summary>当前覆盖的微秒区间；没有数据时返回 (0,0)。</summary>
    public (long From, long To) Coverage()
    {
        lock (_lock)
            return _frames.Count == 0 ? (0, 0) : (_firstPtsUs, _lastPtsUs);
    }

    public long DiskBytes() { lock (_lock) return _bytes; }

    /// <summary>取出 [startUs, endUs] 范围内的音频帧，按时间升序。</summary>
    public List<AudioFrame> BuildClip(long startUs, long endUs)
    {
        var outp = new List<AudioFrame>();
        lock (_lock)
        {
            foreach (var f in _frames)
            {
                if (f.PtsUs < startUs) continue;   // 帧开头早于片头就丢掉，避免负时间戳
                if (f.PtsUs > endUs) break;
                outp.Add(f);
            }
        }
        return outp;
    }

    public void Clear() { lock (_lock) { _frames.Clear(); _count = 0; _bytes = 0; _firstPtsUs = _lastPtsUs = -1; } }
}
