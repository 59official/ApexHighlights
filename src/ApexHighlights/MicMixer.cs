namespace ApexHighlights;

/// <summary>
/// 麦克风混音缓冲：把麦克风那条**独立线程**送来的 PCM 攒成一个队列，
/// 由系统声音那条时间轴（<see cref="Capture.Pump"/>）按帧取走混进去。
///
/// 为什么不能直接「收到就编码」：麦克风和回环是两个不同的设备、两条线程、两个时钟。
/// 直接各编各的，AAC 时间轴会被两个时钟来回拽（抖 10ms 就够听出来了）。
/// 所以统一由回环/静音补齐这条「主时钟」决定喂多少帧，麦克风只负责往队列里堆料——
/// 主时钟缺料时补静音，料多了丢最老的（见 <see cref="MaxFrames"/>）。
///
/// 采样率/声道也在这里统一：麦克风常见 48000/1ch、44100/1ch，输出一律 48000/2ch 交错 int16。
/// </summary>
internal sealed class MicMixer
{
    private readonly int _rate;          // 目标采样率（= AAC 的采样率）
    private readonly int _ch;            // 目标声道数（= AAC 的声道数，固定 2）
    private readonly object _gate = new();

    private readonly short[] _buf;       // 环形，交错 int16
    private int _head;                   // 读位置（样本，不是帧）
    private int _count;                  // 队列里的样本数

    private int _srcRate;
    private int _srcCh;
    private bool _started;
    private double _pos;                 // 重采样相位（单位：源样点，相对当前包起点，可为负）
    private float _prevL, _prevR;        // 上一包最后一个样本（线性插值要用）

    /// <summary>因队列溢出丢掉的帧数（诊断：一直涨说明两个时钟在漂）。</summary>
    public long Dropped { get; private set; }

    /// <summary>队里现有多少帧。</summary>
    public int Frames
    {
        get { lock (_gate) return _count / _ch; }
    }

    /// <summary>队列最多攒多久（秒）。攒过这个数就是漂了，丢最老的。</summary>
    private const int MaxSeconds = 2;

    public MicMixer(int rate, int channels, int capacitySec = MaxSeconds)
    {
        _rate = rate > 0 ? rate : 48000;
        _ch = channels > 0 ? channels : 2;
        int sec = capacitySec < 1 ? 1 : capacitySec;
        _buf = new short[_rate * _ch * sec];
    }

    /// <summary>
    /// 收一包麦克风 PCM（任意采样率/声道，16bit 交错）。在麦克风采集线程上调用。
    /// </summary>
    public void Append(byte[] pcm, int bytes, int srcRate, int srcCh)
    {
        if (pcm is null || bytes < 2) return;
        if (srcCh < 1) srcCh = 1;
        int frames = bytes / (2 * srcCh);
        if (frames <= 0) return;
        if (srcRate <= 0) srcRate = _rate;

        lock (_gate)
        {
            if (!_started || srcRate != _srcRate || srcCh != _srcCh)
            {
                _srcRate = srcRate;
                _srcCh = srcCh;
                _pos = 0;
                _started = true;
            }

            double step = (double)_srcRate / _rate;

            // 读源样本：i = -1 表示「上一包最后一个」，插值跨包才不会断（否则每 10ms 一个咔哒）
            (float L, float R) At(int i)
            {
                if (i < 0) return (_prevL, _prevR);
                int s = i * srcCh * 2;
                int l = BitConverter.ToInt16(pcm, s);
                int r = srcCh == 1 ? l : BitConverter.ToInt16(pcm, s + 2);
                return (l, r);
            }

            while (_pos < frames - 1)
            {
                int i0 = (int)Math.Floor(_pos);
                float f = (float)(_pos - i0);
                var a = At(i0);
                var b = At(i0 + 1);
                Push(a.L + (b.L - a.L) * f, a.R + (b.R - a.R) * f);
                _pos += step;
            }

            var last = At(frames - 1);
            _prevL = last.L;
            _prevR = last.R;
            _pos -= frames;
        }
    }

    /// <summary>
    /// 把这 N 帧麦克风**叠加**到 dst 上（dst 是主时钟已经填好的立体声 int16，长度 bytes）。
    /// 队里料不够就只混有的那几帧，剩下的保持原样（= 只有系统声）。
    /// </summary>
    public void MixInto(byte[] dst, int bytes)
    {
        if (dst is null || bytes < 4) return;
        lock (_gate) AddInto(dst, bytes);
    }

    /// <summary>
    /// 用麦克风**替换** dst 的内容（主时钟在补静音时用：游戏安静、只有用户在说话，这段不能是纯静音）。
    /// 队里料不够，剩下的清零。
    /// </summary>
    public void TakeInto(byte[] dst, int bytes)
    {
        if (dst is null || bytes < 4) return;
        var span = dst.AsSpan(0, bytes);
        span.Clear();
        lock (_gate) AddInto(dst, bytes);
    }

    private void AddInto(byte[] dst, int bytes)
    {
        int frames = bytes / 4;
        for (int f = 0; f < frames; f++)
        {
            if (_count < _ch) return;      // 料没了：剩下的保持原样
            int at = _head;
            int l = (short)(dst[f * 4] | (dst[f * 4 + 1] << 8)) + _buf[at];
            int r = (short)(dst[f * 4 + 2] | (dst[f * 4 + 3] << 8)) + _buf[at + 1];
            _head = (_head + _ch) % _buf.Length;
            _count -= _ch;
            Put(dst, f * 4, l);
            Put(dst, f * 4 + 2, r);
        }
    }

    private static void Put(byte[] dst, int at, int v)
    {
        int c = Clamp(v);
        dst[at] = (byte)(c & 0xFF);
        dst[at + 1] = (byte)((c >> 8) & 0xFF);
    }

    private static int Clamp(int v) => v > 32767 ? 32767 : v < -32768 ? -32768 : v;

    private void Push(float l, float r)
    {
        if (_count + _ch > _buf.Length)
        {
            // 队列满了：丢掉最老的一帧，别让延迟越滚越大
            _head = (_head + _ch) % _buf.Length;
            _count -= _ch;
            Dropped++;
        }
        int tail = (_head + _count) % _buf.Length;
        _buf[tail] = ToShort(l);
        _buf[tail + 1] = ToShort(r);
        _count += _ch;
    }

    private static short ToShort(float v)
    {
        int i = (int)(v + (v >= 0 ? 0.5f : -0.5f));
        return (short)(i > 32767 ? 32767 : i < -32768 ? -32768 : i);
    }
}
