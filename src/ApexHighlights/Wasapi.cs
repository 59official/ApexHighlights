using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>抓哪一个流：系统回环（扬声器在放什么）还是麦克风（用户在说什么）。</summary>
internal enum AudioSourceKind
{
    /// <summary>默认渲染设备的回环流 = 游戏声/桌面声。</summary>
    System,
    /// <summary>默认录音设备 = 麦克风。</summary>
    Microphone,
    /// <summary>
    /// 某个进程（含子进程树）自己发出的声音，不含别的程序。
    /// 走「进程回环」虚拟设备，见 <see cref="LoopbackCapture"/> 里的 ActivateAudioInterfaceAsync。
    /// </summary>
    GameProcess,
}

/// <summary>
/// WASAPI 采集：默认抓「默认渲染设备的回环流」（系统/游戏声音），也可以抓默认录音设备（麦克风）。
/// 不碰麦克风除非明确选了 <see cref="AudioSourceKind.Microphone"/>、不需要任何第三方库。
/// 输出统一成 **int16 交错立体声**，采样率跟随设备混音格式（通常 48000），由 <see cref="SampleRate"/> 报告。
///
/// 线程模型：专用后台线程等事件 → 排空 packet → 转换 → 回调（回调里做 AAC 编码，零分配）。
/// 系统没有声音在播时不会有 packet，这是正常的（等于静音），不会产生空洞时间戳。
/// 麦克风则是「一直有包」（哪怕静音也送），因为录音设备不会因为安静就停止供流。
/// </summary>
internal sealed class LoopbackCapture : IDisposable
{
    private const int DataFlowRender = 0;
    private const int DataFlowCapture = 1;
    private const int RoleConsole = 0;

    private const int ShareModeShared = 0;
    private const uint FlagsLoopback = 0x00020000;
    private const uint FlagsEventCallback = 0x00040000;

    private readonly AudioSourceKind _kind;
    private readonly int _dataFlow;
    private readonly uint _baseFlags;
    /// <summary>进程回环时抓哪个进程（0 = 不是进程回环）。</summary>
    private readonly int _processId;

    public LoopbackCapture(AudioSourceKind kind = AudioSourceKind.System, int processId = 0)
    {
        _kind = kind;
        _processId = processId;
        _dataFlow = kind == AudioSourceKind.Microphone ? DataFlowCapture : DataFlowRender;
        // 麦克风与进程回环都不能带 LOOPBACK 标志（那是「默认渲染设备整机回环」专属的），只保留事件驱动
        _baseFlags = kind == AudioSourceKind.System ? FlagsLoopback : 0u;
    }

    /// <summary>这个实例抓的是哪种流。</summary>
    public AudioSourceKind Kind => _kind;

    /// <summary>设备回环流里一个 packet 缓冲：int16 交错立体声，回调期间有效。</summary>
    public delegate void PcmHandler(byte[] stereo16, int byteCount);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr props);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig]
        int GetBufferSize(out uint frames);
        [PreserveSig]
        int GetStreamLatency(out long latency);
        [PreserveSig]
        int GetCurrentPadding(out uint frames);
        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig]
        int GetMixFormat(out IntPtr format);
        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig]
        int Start();
        [PreserveSig]
        int Stop();
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int SetEventHandle(IntPtr handle);
        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig]
        int ReleaseBuffer(uint frames);
        [PreserveSig]
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
        if (_kind != AudioSourceKind.GameProcess)
        {
            StartCore(onPcm);
            return;
        }

        // 进程回环必须在 MTA 线程上激活。
        // 托盘的「开始录制」是在 WinForms 的 STA 界面线程里调的：COM 会把 ActivateAudioInterfaceAsync
        // 返回的对象编组进 STA，而 mmdevapi 给进程回环客户端准备的代理并不实现 IAudioClient，
        // 第一次碰它（GetMixFormat / 强转）就 E_NOINTERFACE（0x80004002）。
        // 控制台里跑同样的代码却是好的——因为控制台主线程是 MTA。这个差异坑过一次真机。
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { StartCore(onPcm); }
            catch (Exception ex) { err = ex; }
        })
        { IsBackground = true, Name = "wasapi-activate" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        if (!t.Join(8000)) throw new TimeoutException("进程回环 8 秒没启动起来");
        if (err is not null) throw err;
    }

    private void StartCore(PcmHandler onPcm)
    {
        if (_kind == AudioSourceKind.GameProcess)
        {
            _client = ActivateProcessLoopback(_processId);
            DeviceName = "进程 " + _processId;
            FinishStart(onPcm);
            return;
        }

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        int hr = enumerator.GetDefaultAudioEndpoint(_dataFlow, RoleConsole, out var device);
        if (hr < 0)
            throw new COMException($"GetDefaultAudioEndpoint 0x{hr:X8}" +
                $"（没有默认{(_kind == AudioSourceKind.Microphone ? "录音" : "播放")}设备？）");
        try
        {
            var iid = typeof(IAudioClient).GUID;
            hr = device.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj);
            if (hr < 0) throw new COMException($"IMMDevice.Activate(IAudioClient) 0x{hr:X8}");
            _client = (IAudioClient)obj;

            try { DeviceName = device.GetId(out var id) >= 0 ? ShortId(id) : "?"; } catch { }
            FinishStart(onPcm);
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    /// <summary>拿到 IAudioClient 之后的公共部分：定格式 → 初始化 → 起采集线程。</summary>
    private void FinishStart(PcmHandler onPcm)
    {
        int hr = _client!.GetMixFormat(out _format);
        if (hr < 0)
        {
            // 进程回环在部分驱动上不支持 GetMixFormat —— 退回 48k/2ch/16bit 的标准混音格式
            if (_kind != AudioSourceKind.GameProcess) throw new COMException($"GetMixFormat 0x{hr:X8}");
            UseFallbackFormat();
        }
        else
        {
            ParseFormat(_format, out int rate, out int ch);
            SampleRate = rate; Channels = ch;
        }

        _signal = new AutoResetEvent(false);

        // 整机回环：LOOPBACK 标志 + 事件驱动；进程回环/麦克风：不能带 LOOPBACK。
        // 每个变体再按 100ms/20ms/200ms/引擎默认 的周期各试一遍 —— 驱动对周期的容忍度差别很大。
        uint[] variants = _kind == AudioSourceKind.System
            ? new[] { _baseFlags | FlagsEventCallback, _baseFlags }
            : new uint[] { FlagsEventCallback, 0u, FlagsLoopback | FlagsEventCallback, FlagsLoopback };
        long[] durations = { 1_000_000, 200_000, 2_000_000, 0 };
        uint flags = 0;
        bool ok = false;
        string last = "";
        foreach (var v in variants)
        {
            foreach (var dur in durations)
            {
                hr = _client.Initialize(ShareModeShared, v, dur, 0, _format, IntPtr.Zero);
                if (hr >= 0) { ok = true; flags = v; break; }
                last += $" [flags=0x{v:X} dur={dur} 0x{hr:X8}]";
            }
            if (ok) break;
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

    /// <summary>GetMixFormat 不可用时手搭一个 48kHz / 立体声 / 16bit 的 WAVEFORMATEX。</summary>
    private void UseFallbackFormat()
    {
        const int ch = 2, rate = 48000, bits = 16;
        int block = ch * bits / 8;
        if (_format != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(_format); } catch { } }
        _format = Marshal.AllocCoTaskMem(18);
        Marshal.WriteInt16(_format, 0, 1);                  // WAVE_FORMAT_PCM
        Marshal.WriteInt16(_format, 2, (short)ch);
        Marshal.WriteInt32(_format, 4, rate);
        Marshal.WriteInt32(_format, 8, rate * block);
        Marshal.WriteInt16(_format, 12, (short)block);
        Marshal.WriteInt16(_format, 14, (short)bits);
        Marshal.WriteInt16(_format, 16, 0);
        SampleRate = rate; Channels = ch; _bits = bits; _isPcm = true; _isFloat = false;
    }

    // ── 进程回环（AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK，Windows 10 2004+）──────────
    // 整机回环只能抓「扬声器上的一切」，Discord/浏览器全混进来。想让用户只录游戏，
    // 就得在进程级开一条回环：激活一个挂在 VAD\Process_Loopback 虚拟设备上的 IAudioClient，
    // 用 PROPVARIANT 告诉系统要抓哪个进程（含它的子进程树）。
    private const string VirtualProcessLoopback = @"VAD\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackIncludeTree = 0;   // INCLUDE_TARGET_PROCESS_TREE
    private const int ProcessLoopbackExcludeTree = 1;   // EXCLUDE_TARGET_PROCESS_TREE

    // 注意：这个接口**不能**标 [ComImport] —— 我们自己要在托管代码里实现它（ActivationWaiter），
    // 而 ComImport 接口是「由 COM 提供的」，CLR 拒绝为它生成调用包装，运行时直接抛
    // NotImplementedException（E_NOTIMPL），排查起来像激活失败，实际上是接口声明方式错了。
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(
            [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation operation);

    /// <summary>等 ActivateAudioInterfaceAsync 的回调（回调来自 COM 的线程池线程，所以只用事件同步）。</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationWaiter : IActivateAudioInterfaceCompletionHandler, IDisposable
    {
        private readonly ManualResetEventSlim _done = new(false);
        public IAudioClient? Client;
        public int Hr = unchecked((int)0x80004005);

        public bool Wait(int ms) => _done.Wait(ms);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(out int result, out object? iface);
                if (hr < 0) Hr = hr;
                else if (result < 0) Hr = result;
                else if (iface is IAudioClient client) { Client = client; Hr = 0; }
                else Hr = unchecked((int)0x80004005);
            }
            catch (Exception ex) { Hr = ex.HResult; }
            finally { _done.Set(); }
            return 0;
        }

        public void Dispose() => _done.Dispose();
    }

    /// <summary>
    /// 激活一条「只抓某个进程（含子进程）」的回环 IAudioClient。
    /// 失败抛异常，由上层决定要不要退回整机回环。
    /// </summary>
    private static IAudioClient ActivateProcessLoopback(int pid)
    {
        if (pid <= 0) throw new ArgumentException("进程回环需要一个有效的进程号", nameof(pid));

        // AUDIOCLIENT_ACTIVATION_PARAMS = { ActivationType; ProcessLoopbackParams{ pid; mode } }，共 12 字节
        const int paramsBytes = 12;
        IntPtr blob = Marshal.AllocCoTaskMem(paramsBytes);
        IntPtr prop = Marshal.AllocCoTaskMem(24);          // PROPVARIANT（x64 上是 24 字节）
        var waiter = new ActivationWaiter();
        IActivateAudioInterfaceAsyncOperation? op = null;
        try
        {
            Marshal.WriteInt32(blob, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(blob, 4, pid);
            Marshal.WriteInt32(blob, 8, ProcessLoopbackIncludeTree);

            Marshal.WriteInt16(prop, 0, 0x0041);           // VT_BLOB
            Marshal.WriteInt32(prop, 8, paramsBytes);      // BLOB.cbSize
            Marshal.WriteIntPtr(prop, 16, blob);           // BLOB.pBlobData

            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync(VirtualProcessLoopback, ref iid, prop, waiter, out op);
            if (!waiter.Wait(3000)) throw new TimeoutException("ActivateAudioInterfaceAsync 3 秒没回调");
            if (waiter.Client is null)
                throw new COMException($"进程回环激活失败 0x{waiter.Hr:X8}（进程 {pid} 可能没有音频会话）");
            return waiter.Client;
        }
        finally
        {
            if (op is not null) { try { Marshal.ReleaseComObject(op); } catch { } }
            waiter.Dispose();
            Marshal.FreeCoTaskMem(prop);
            Marshal.FreeCoTaskMem(blob);
        }
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

        // 进程回环的对象是在 MTA 里激活的，释放也得回 MTA：从 STA 调它的方法会再走一次编组，
        // 又可能撞上同样的 E_NOINTERFACE（Stop 抛异常倒无所谓，ReleaseComObject 漏了会漏引用）。
        void ComCleanup()
        {
            try { _client?.Stop(); } catch { }
            try { if (_client is not null) Marshal.ReleaseComObject(_client); } catch { }
            try { if (_capture is not null) Marshal.ReleaseComObject(_capture); } catch { }
        }

        if (_kind == AudioSourceKind.GameProcess)
        {
            try
            {
                var t = new Thread(ComCleanup) { IsBackground = true, Name = "wasapi-release" };
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                t.Join(2000);
            }
            catch { ComCleanup(); }
        }
        else ComCleanup();

        if (_format != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(_format); } catch { } _format = IntPtr.Zero; }
        _signal?.Dispose();
    }
}
