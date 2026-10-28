using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// WASAPI loopback 采集：抓默认渲染设备（扬声器）的回环流 = 系统/游戏声音。
/// 不碰麦克风、不需要任何第三方库。输出统一成 **int16 交错立体声**，
/// 采样率跟随设备混音格式（通常 48000），由 <see cref="SampleRate"/> 报告。
///
/// 线程模型：专用后台线程等事件 → 排空 packet → 转换 → 回调（回调里做 AAC 编码，零分配）。
/// 系统没有声音在播时不会有 packet，这是正常的（等于静音），不会产生空洞时间戳。
/// </summary>
internal sealed class LoopbackCapture : IDisposable
{
    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;

    private const int ShareModeShared = 0;
    private const uint FlagsLoopback = 0x00020000;
    private const uint FlagsEventCallback = 0x00040000;

    /// <summary>设备回环流里一个 packet 缓冲：int16 交错立体声，回调期间有效。</summary>
    public delegate void PcmHandler(byte[] stereo16, int byteCount);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr callback);
        int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr props);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint frames);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint frames);
        int GetNextPacketSize(out uint frames);
    }

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private AutoResetEvent? _signal;
    private Thread? _thread;
    private IntPtr _format;
    private volatile bool _stop;

    private byte[] _stage = new byte[64 * 1024];

    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;
    public int Bits => _bits;
    public bool IsFloat => _isFloat;
    public string DeviceName { get; private set; } = "?";
    public string Err { get; private set; } = "";

    /// <summary>已转换出的立体声 int16 字节数 / packet 数（诊断用）。</summary>
    public long Packets;
    public long Bytes;
    public volatile bool Running;

    /// <summary>起采集。失败抛异常（上层决定是静默降级还是报错）。</summary>
    public void Start(PcmHandler onPcm)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        int hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device);
        if (hr < 0) throw new COMException($"GetDefaultAudioEndpoint 0x{hr:X8}（没有默认播放设备？）");
        try
        {
            var iid = typeof(IAudioClient).GUID;
            hr = device.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj);
            if (hr < 0) throw new COMException($"IMMDevice.Activate(IAudioClient) 0x{hr:X8}");
            _client = (IAudioClient)obj;

            try { DeviceName = device.GetId(out var id) >= 0 ? ShortId(id) : "?"; } catch { }

            hr = _client.GetMixFormat(out _format);
            if (hr < 0) throw new COMException($"GetMixFormat 0x{hr:X8}");
            ParseFormat(_format, out int rate, out int ch);
            SampleRate = rate; Channels = ch;

            _signal = new AutoResetEvent(false);

            // 先试事件驱动（CPU 最省）；不行退回轮询
            uint flags = FlagsLoopback | FlagsEventCallback;
            long[] durations = { 1_000_000, 200_000, 2_000_000, 0 };   // 100ms / 20ms / 200ms / 引擎默认
            bool ok = false;
            string last = "";
            foreach (var dur in durations)
            {
                hr = _client.Initialize(ShareModeShared, flags, dur, 0, _format, IntPtr.Zero);
                if (hr >= 0) { ok = true; break; }
                last = $"event dur={dur} 0x{hr:X8}";
            }
            if (!ok)
            {
                foreach (var dur in durations)
                {
                    hr = _client.Initialize(ShareModeShared, FlagsLoopback, dur, 0, _format, IntPtr.Zero);
                    if (hr >= 0) { ok = true; flags = FlagsLoopback; break; }
                    last += $" | poll dur={dur} 0x{hr:X8}";
                }
            }
            if (!ok) throw new COMException($"IAudioClient.Initialize 全失败：{last}");

            var capIid = typeof(IAudioCaptureClient).GUID;
            hr = _client.GetService(ref capIid, out var capObj);
            if (hr < 0) throw new COMException($"GetService(IAudioCaptureClient) 0x{hr:X8}");
            _capture = (IAudioCaptureClient)capObj;

            if ((flags & FlagsEventCallback) != 0)
            {
                hr = _client.SetEventHandle(_signal.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0) throw new COMException($"SetEventHandle 0x{hr:X8}");
            }

            hr = _client.Start();
            if (hr < 0) throw new COMException($"IAudioClient.Start 0x{hr:X8}");

            Running = true;
            _thread = new Thread(() => Loop(onPcm, (flags & FlagsEventCallback) != 0))
            {
                IsBackground = true,
                Name = "wasapi",
            };
            _thread.Start();
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    private static string ShortId(string id)
    {
        int i = id.IndexOf("}.{", StringComparison.Ordinal);
        return i > 0 ? id[..(i + 1)] : id;
    }

    /// <summary>解析 WAVEFORMATEX / WAVEFORMATEXTENSIBLE，拿到真实采样率与声道数；同时记下样本格式。</summary>
    private void ParseFormat(IntPtr p, out int rate, out int channels)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        channels = Marshal.ReadInt16(p, 2);
        rate = Marshal.ReadInt32(p, 4);
        ushort bits = (ushort)Marshal.ReadInt16(p, 14);
        ushort cbSize = (ushort)Marshal.ReadInt16(p, 16);

        _isFloat = tag == 3;
        _bits = bits;
        if (tag == 0xFFFE && cbSize >= 22)
        {
            // SubFormat GUID 在 WAVEFORMATEX 之后 6 字节处（wValidBits 2 + dwChannelMask 4）
            _bits = (ushort)Marshal.ReadInt16(p, 18);
            int d1 = Marshal.ReadInt32(p, 24);
            _isFloat = d1 == 3;    // 00000003-0000-0010-8000-00aa00389b71 = IEEE float
            _isPcm = d1 == 1;
        }
        else _isPcm = tag == 1;

        if (channels <= 0) channels = 2;
        if (rate <= 0) rate = 48000;
        if (!_isFloat && !_isPcm) throw new NotSupportedException($"混音格式既不是 PCM 也不是 float（tag=0x{tag:X4}）");
        if (_bits != 16 && _bits != 24 && _bits != 32) throw new NotSupportedException($"不支持 {_bits} bit 采样");
    }

    private bool _isFloat, _isPcm;
    private int _bits = 32;

    private void Loop(PcmHandler onPcm, bool eventDriven)
    {
        int frameBytes = Channels * (_bits / 8);
        try
        {
            while (!_stop)
            {
                if (eventDriven) _signal!.WaitOne(200);
                else Thread.Sleep(5);

                while (!_stop)
                {
                    if (_capture!.GetNextPacketSize(out uint avail) < 0) return;
                    if (avail == 0) break;
                    if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint pflags, out _, out _) < 0) return;
                    try
                    {
                        if (frames == 0) continue;
                        // AUDCLNT_BUFFERFLAGS_SILENT = 0x2：数据指针无效，按静音处理
                        bool silent = (pflags & 0x2) != 0;
                        int srcBytes = (int)frames * frameBytes;
                        int need = (int)frames * 4;                      // 目标：立体声 int16
                        if (_stage.Length < need) _stage = new byte[need];
                        Convert(data, silent, (int)frames, _stage);
                        Packets++;
                        Bytes += need;
                        onPcm(_stage, need);
                    }
                    finally { _capture.ReleaseBuffer(frames); }
                }
            }
        }
        catch (Exception ex) { if (Err.Length == 0) Err = $"{ex.GetType().Name}: {ex.Message}"; }
        finally { Running = false; }
    }

    /// <summary>把设备混音格式的 PCM 转成**交错立体声 int16**。</summary>
    private unsafe void Convert(IntPtr src, bool silent, int frames, byte[] dst)
    {
        int outCh = 2, kind = _bits / 8;
        fixed (byte* d = dst)
        {
            var o = (short*)d;
            if (silent || src == IntPtr.Zero) { Array.Clear(dst, 0, frames * 4); return; }
            var s = (byte*)src;

            // 统一返回「int16 量程」（-32768..32767）的浮点值；float 源额外乘 32767。
            float Sample(int frame, int ch)
            {
                int idx = frame * Channels + ch;
                if (_isFloat) return *(float*)(s + idx * 4) * 32767f;
                return kind switch
                {
                    2 => (short)(s[idx * 2] | (s[idx * 2 + 1] << 8)),
                    3 => (s[idx * 3] | (s[idx * 3 + 1] << 8) | ((sbyte)s[idx * 3 + 2] << 16)) / 256f,
                    _ => (s[idx * 4] | (s[idx * 4 + 1] << 8) | (s[idx * 4 + 2] << 16) | ((sbyte)s[idx * 4 + 3] << 24)) / 65536f,
                };
            }

            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < outCh; c++)
                {
                    int sc = Channels == 1 ? 0 : Math.Min(c, Channels - 1);
                    float v = Sample(f, sc);
                    int i = (int)(v + (v >= 0 ? 0.5f : -0.5f));
                    if (i > 32767) i = 32767; else if (i < -32768) i = -32768;
                    o[f * outCh + c] = (short)i;
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _signal?.Set(); } catch { }
        try { _thread?.Join(1500); } catch { }
        try { _client?.Stop(); } catch { }
        try { if (_client is not null) Marshal.ReleaseComObject(_client); } catch { }
        try { if (_capture is not null) Marshal.ReleaseComObject(_capture); } catch { }
        if (_format != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(_format); } catch { } _format = IntPtr.Zero; }
        _signal?.Dispose();
    }
}
