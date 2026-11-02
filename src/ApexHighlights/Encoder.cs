using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ApexHighlights;

/// <summary>
/// NVENC（NVIDIA H.264 Encoder MFT）封装。异步 MFT，必须按 METransformNeedInput(601) /
/// METransformHaveOutput(602) 的事件节拍喂帧，硬推会得到 MF_E_NOTACCEPTING。
/// 关键坑：MF_TRANSFORM_ASYNC_UNLOCK 必须用 Vortice 常量，手写 GUID 写错不报错但整台机器都拒收。
/// </summary>
public sealed class NvEncoder : IDisposable
{
    private const int NeedInput = 601, HaveOutput = 602;

    private readonly IMFTransform _enc;
    private readonly IMFMediaEventGenerator _gen;
    private readonly IMFSample _inSample;
    private readonly IMFMediaBuffer _inBuf;
    private readonly IMFDXGIDeviceManager? _mgr;
    private readonly IntPtr _d3dMgrPtr;
    private readonly int _w, _h, _gopFrames, _fps;
    private readonly bool _provides;
    private readonly int _osiSize;
    private bool _pendingInput;
    private bool _streaming;
    private readonly Queue<long> _ptsQueue = new();

    public static string QualityInfo = "";
    public static string GopInfo = "-";
    /// <summary>
    /// surface 直喂时 IMFSample 的构造方式（排查用，--surface-mode=N 可切）：
    /// 0 = 仅 MFCreateVideoSampleFromSurface；1 = surface sample + DXGI surface buffer；
    /// 2 = MFCreateSample() + DXGI surface buffer（不用 VideoSampleFromSurface）。
    /// </summary>
    public static int SurfaceMode = 2;
    /// <summary>D3D 管理器诊断（mgr=set/null/失败 + MFT 是否 D3D-aware）。</summary>
    public static string D3DInfo = "-";
    public string SurfaceErr = "";
    /// <summary>开关：给 surface sample 额外挂 DXGI surface buffer（当前会让 ProcessInput 返回 MF_E_NOTACCEPTING，默认关）。</summary>
    public static bool UseSurfaceBuffer = false;
    public int Gop { get; private set; }
    public int Dropped { get; private set; }
    public int Pushed { get; private set; }
    public string Name { get; private set; } = "?";

    public NvEncoder(int w, int h, int bitrate, int gopFrames = 60, int fps = 60, ID3D11Device? d3d = null)
    {
        _w = w; _h = h; _gopFrames = gopFrames; _fps = Math.Max(1, fps);
        MediaFactory.MFStartup(false);

        // GPU 直通：给 MFT 一个 D3D 设备管理器，让它能直接读 DXGI surface（NV12 纹理）。
        // 必须在 SetInputType 之前通过 MFT_MESSAGE_SET_D3D_MANAGER 送达（见 Configure）。
        if (d3d is not null)
        {
            _mgr = MediaFactory.MFCreateDXGIDeviceManager();
            try
            {
                var rr = _mgr.ResetDevice(d3d);
                _d3dMgrPtr = rr.Success ? _mgr.NativePointer : IntPtr.Zero;
                if (rr.Failure) Console.WriteLine($"[enc] ResetDevice 0x{(int)rr:X8} → 退回 CPU 直喂");
            }
            catch (Exception e) { Console.WriteLine($"[enc] ResetDevice 异常 {e.Message}"); _d3dMgrPtr = IntPtr.Zero; }
        }
        else _d3dMgrPtr = IntPtr.Zero;

        var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x7, inInfo, outInfo, out var ptrs, out var count);
        if (count == 0) throw new InvalidOperationException("机器上没有 H.264 编码器");

        var arr = new IntPtr[count];
        Marshal.Copy(ptrs, arr, 0, (int)count);

        IMFTransform? chosen = null;
        string? err = null;
        bool provides = false;
        int osiSize = 0, gopOut = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var act = new IMFActivate(arr[i]);
            string name;
            try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "?"; } catch { name = "?"; }
            IMFTransform t;
            try { t = act.ActivateObject<IMFTransform>(); }
            catch (Exception e) { act.Dispose(); err = $"{name}: activate {e.Message}"; continue; }
            act.Dispose();

            if (Configure(t, w, h, bitrate, gopFrames, fps, _d3dMgrPtr, out var st))
            {
                chosen = t; Name = name; provides = st.provides; osiSize = st.osiSize; gopOut = st.gop;
                break;
            }
            err = (err is null ? "" : err + " | ") + $"{name}: {st.fail}";
            t.Dispose();
        }
        if (chosen is null) throw new InvalidOperationException($"没有编码器配置成功（{err}）");

        _enc = chosen;
        _provides = provides;
        _osiSize = osiSize;
        Gop = gopOut;
        _gen = _enc.QueryInterface<IMFMediaEventGenerator>();
        _inBuf = MediaFactory.MFCreateMemoryBuffer(w * h * 3 / 2);
        _inSample = MediaFactory.MFCreateSample();
        _inSample.AddBuffer(_inBuf);
    }

    private readonly record struct CfgState(bool provides, int osiSize, string fail, int gop);

    private static bool Configure(IMFTransform enc, int w, int h, int bitrate, int gopFrames, int fps, IntPtr d3dMgrPtr, out CfgState state)
    {
        state = default;
        int gop = 0;
        try { enc.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u); } catch { }
        // GPU 直通：在协商类型之前把 D3D 管理器交给编码器（MFT_MESSAGE_SET_D3D_MANAGER）
        if (d3dMgrPtr != IntPtr.Zero)
        {
            try
            {
                enc.ProcessMessage(TMessageType.MessageSetD3DManager, new UIntPtr((ulong)d3dMgrPtr));
                D3DInfo = "mgr=set";
            }
            catch (Exception e) { D3DInfo = "mgr=失败 " + e.Message; }
            // MFT 是否真的 D3D-aware（MF_SA_D3D_AWARE = {e3f2e203-dcd4-4a39-9d65-ea0af9b2a3f1}）
            try
            {
                var aware = enc.Attributes.GetUInt32(new Guid("e3f2e203-dcd4-4a39-9d65-ea0af9b2a3f1"));
                D3DInfo += $" aware={aware}";
            }
            catch { D3DInfo += " aware=?"; }
        }
        else D3DInfo = "mgr=null";
        // 协商前先试写一次 GOP（NVENC 可能只在这里认）
        try { using var pre = CodecApi.TryGet(enc.NativePointer); pre?.SetUInt(CodecApi.GopSize, (uint)gopFrames); } catch { }


        IMFMediaType? outType = null;
        try
        {
            for (int i = 0; i < 32; i++)
            {
                IMFMediaType mt;
                try { mt = enc.GetOutputAvailableType(0, i); } catch { break; }
                if (mt == null) break;
                Guid sub = Guid.Empty;
                try { sub = mt.GetGUID(MediaTypeAttributeKeys.Subtype); } catch { }
                if (sub == VideoFormatGuids.H264) { outType = mt; break; }
                mt.Dispose();
            }
        }
        catch { }

        if (outType is null) { state = new CfgState(false, 0, "没有 H264 输出类型", gop); return false; }

        try
        {
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outType.Set(MediaTypeAttributeKeys.FrameSize, Pack64(w, h));
            outType.Set(MediaTypeAttributeKeys.FrameRate, Pack32(Math.Max(1, fps), 1));
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack32(1, 1));
            outType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, 60u);  // GOP=60 → 1s 一个 IDR

            // 顺序不能反：先出后入
            enc.SetOutputType(0, outType, 0);

            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack64(w, h));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack32(Math.Max(1, fps), 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack32(1, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            enc.SetInputType(0, inType, 0);

            // 类型协商完 ICodecAPI 才认参数；这里是唯一能给 NVENC 设 GOP 的时机
            using var api = CodecApi.TryGet(enc.NativePointer);
            if (api is null) GopInfo = "无ICodecAPI";
            else
            {
                bool sup = api.IsSupported(CodecApi.GopSize);
                bool set = sup && api.SetUInt(CodecApi.GopSize, (uint)gopFrames);
                var rb = api.GetUInt(CodecApi.GopSize);
                GopInfo = $"sup={sup} set={set} rb={rb}";
                if (set) gop = gopFrames;

                // 画质三件套。NVENC 的默认预设偏「低延迟」，静态画面看不出，
                // 一进运动画面（尤其是 1440p60 + 细碎纹理）就开始糊，还带一圈振铃边缘，
                // 看着像被锐化过。把质量优先度拧到高、并给峰值码率留 1.5 倍余量。
                bool qSup = api.IsSupported(CodecApi.QualityVsSpeed);
                bool qSet = qSup && api.SetUInt(CodecApi.QualityVsSpeed, 80);
                bool mSup = api.IsSupported(CodecApi.MaxBitRate);
                bool mSet = mSup && api.SetUInt(CodecApi.MaxBitRate, (uint)(bitrate * 3 / 2));
                // PeakConstrainedVBR：静场面少给点、动场面多给点，同样平均码率下观感更好
                bool rSup = api.IsSupported(CodecApi.RateControlMode);
                bool rSet = rSup && api.SetUInt(CodecApi.RateControlMode, 1);
                QualityInfo = $"qvs=sup{qSup}/set{qSet} max=sup{mSup}/set{mSet} rc=sup{rSup}/set{rSet}";
            }
        }
        catch (Exception e) { state = new CfgState(false, 0, "设类型失败 " + e.Message, gop); return false; }

        try
        {
            var osi = enc.GetOutputStreamInfo(0);
            state = new CfgState((osi.Flags & 0x100) != 0, osi.Size, "", gop);
            return true;
        }
        catch (Exception e) { state = new CfgState(false, 0, "OutputStreamInfo " + e.Message, gop); return false; }
    }

    private void Start()
    {
        _enc.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _enc.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _streaming = true;
        _pendingInput = WaitForInput(200);
    }

    /// <summary>提交一帧 NV12；每拿到一个编码包就回调（Annex-B 字节 + 100ns 时间戳）。</summary>
    public void Submit(long pts100ns, ReadOnlySpan<byte> nv12, Action<byte[], long> onPacket)
    {
        if (!_streaming) Start();
        if (!_pendingInput)
        {
            int et = NextEvent(0);
            if (et == HaveOutput) { Pull(onPacket); }
            else if (et == NeedInput) _pendingInput = true;
            else { Dropped++; return; }
        }
        if (!_pendingInput) { Dropped++; return; }

        _inBuf.Lock(out IntPtr p, out _, out _);
        unsafe { nv12.CopyTo(new Span<byte>((void*)p, nv12.Length)); }
        _inBuf.Unlock();
        _inBuf.CurrentLength = nv12.Length;
        _inSample.SampleTime = pts100ns;
        _inSample.SampleDuration = 10_000_000 / _fps;

        try { _enc.ProcessInput(0, _inSample, 0); }
        catch { Dropped++; return; }

        Pushed++;
        _ptsQueue.Enqueue(pts100ns);
        _pendingInput = false;

        while (true)
        {
            int et = NextEvent(0);
            if (et == HaveOutput) { if (!Pull(onPacket)) break; }
            else if (et == NeedInput) { _pendingInput = true; break; }
            else break;
        }
    }

    /// <summary>提交一帧已在 GPU 上的 NV12 纹理（D3D11 surface 直喂，不落 CPU）。</summary>
    public void SubmitSurface(long pts100ns, ID3D11Texture2D nv12Tex, Action<byte[], long> onPacket)
    {
        if (!_streaming) Start();
        if (!_pendingInput)
        {
            int et = NextEvent(0);
            if (et == HaveOutput) { Pull(onPacket); }
            else if (et == NeedInput) _pendingInput = true;
            else { Dropped++; return; }
        }
        if (!_pendingInput) { Dropped++; return; }

        IMFSample? sample = null;
        bool accepted = false;
        try
        {
            sample = BuildSurfaceSample(nv12Tex);
            if (sample is null) { Dropped++; return; }

            sample.SampleTime = pts100ns;
            sample.SampleDuration = 10_000_000 / _fps;
            try { _enc.ProcessInput(0, sample, 0); accepted = true; }
            catch (Exception ex)
            {
                // 关键修正（2026-10-28）：以前这里直接 return，_pendingInput 仍是 true，
                // 于是后面每一帧都跳过取事件、撞同一个 NOTACCEPTING —— 180 帧全丢就是这么来的。
                // 现在：把「待输入」状态清掉，并抽干事件队列重新同步，下一帧重新问 MFT 要 NeedInput。
                SurfaceErr = ex.Message;
                _pendingInput = false;
                Resync();
                Dropped++;
                return;
            }
        }
        finally { sample?.Dispose(); }

        if (!accepted) return;

        Pushed++;
        _ptsQueue.Enqueue(pts100ns);
        _pendingInput = false;

        while (true)
        {
            int et = NextEvent(0);
            if (et == HaveOutput) { if (!Pull(onPacket)) break; }
            else if (et == NeedInput) { _pendingInput = true; break; }
            else break;
        }
    }

    /// <summary>按 <see cref="SurfaceMode"/> 构造一个「带 D3D 纹理」的输入 sample。</summary>
    private IMFSample? BuildSurfaceSample(ID3D11Texture2D nv12Tex)
    {
        switch (SurfaceMode)
        {
            case 0:
            {
                // 只有 surface sample，不含显式 buffer（ProcessInput 成功但编码器编出全黑）
                var r0 = MediaFactory.MFCreateVideoSampleFromSurface(nv12Tex, out var s0);
                return r0.Failure ? null : s0;
            }
            case 1:
            {
                // surface sample + 再挂一条 DXGI surface buffer
                var r1 = MediaFactory.MFCreateVideoSampleFromSurface(nv12Tex, out var s1);
                if (r1.Failure || s1 is null) return null;
                var buf = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12Tex, 0, false);
                if (buf is null) { s1.Dispose(); return null; }
                s1.AddBuffer(buf);
                buf.Dispose();
                return s1;
            }
            default:
            {
                // 普通 sample + DXGI surface buffer（不用 MFCreateVideoSampleFromSurface）
                var s2 = MediaFactory.MFCreateSample();
                var buf = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12Tex, 0, false);
                if (buf is null) { s2.Dispose(); return null; }
                s2.AddBuffer(buf);
                buf.Dispose();
                return s2;
            }
        }
    }

    /// <summary>抽干事件队列，丢弃输出，重新建立 NeedInput 状态。</summary>
    private void Resync()
    {
        for (int i = 0; i < 16; i++)
        {
            int et = NextEvent(0);
            if (et == NeedInput) { _pendingInput = true; return; }
            if (et < 0) return;   // 没有更多事件
            // HaveOutput：丢掉（失败后这一段本来也不连续）
        }
    }

    private bool Pull(Action<byte[], long> onPacket)
    {
        var odb = new OutputDataBuffer();
        IMFSample? own = null;
        if (!_provides)
        {
            var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(_osiSize, 1024));
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(mb);
            odb.Sample = own;
        }
        var res = _enc.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
        if (!res.Success) { own?.Dispose(); return false; }

        var sample = odb.Sample;
        if (sample is null) { own?.Dispose(); return false; }
        try
        {
            var buf = sample.ConvertToContiguousBuffer();
            buf.Lock(out IntPtr p, out _, out int cur);
            var data = new byte[cur];
            Marshal.Copy(p, data, 0, cur);
            buf.Unlock();
            buf.Dispose();
            onPacket(data, _ptsQueue.Count > 0 ? _ptsQueue.Dequeue() : sample.SampleTime);
        }
        finally { sample.Dispose(); }
        return true;
    }

    /// <summary>在同一 MFT 会话里重新协商类型，逼编码器吐新的 SPS+IDR（不新建 NVENC 会话）。</summary>
    public bool Reinit()
    {
        try
        {
            _enc.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _enc.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack64(_w, _h));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack32(Math.Max(1, _fps), 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack32(1, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            _enc.SetInputType(0, inType, 0);
            inType.Dispose();
            _enc.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _enc.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            _pendingInput = WaitForInput(300);
            return true;
        }
        catch { return false; }
    }

    /// <summary>冲洗编码器：让下一帧成为 IDR（用于 1 秒分片），不重建会话。</summary>
    public void Flush()
    {
        if (!_streaming) return;
        try { _enc.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        try { _enc.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero); } catch { }
        try { _enc.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero); } catch { }
        _pendingInput = WaitForInput(300);
    }

    private bool WaitForInput(int timeoutMs)
    {
        for (int i = 0; i < 8; i++)
        {
            int et = NextEvent(timeoutMs);
            if (et == NeedInput) return true;
            if (et == HaveOutput) continue;
            if (et < 0) return false;
        }
        return false;
    }

    private int NextEvent(int timeoutMs)
    {
        for (int i = 0; i < 8; i++)
        {
            IMFMediaEvent? ev = null;
            try { ev = _gen.GetEvent(timeoutMs); } catch { return -1; }
            if (ev is null) return -1;
            int t = (int)ev.EventType;
            ev.Dispose();
            if (t == NeedInput || t == HaveOutput) return t;
        }
        return -1;
    }

    public void Dispose()
    {
        try { if (_streaming) _enc.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        _inSample?.Dispose();
        _inBuf?.Dispose();
        _gen?.Dispose();
        _enc?.Dispose();
        _mgr?.Dispose();
    }

    // MF 约定：FRAME_SIZE 的宽在高 32 位、高在低 32 位；FRAME_RATE 的分子在高、分母在低。
    private static ulong Pack64(int w, int h) => ((ulong)(uint)w << 32) | (uint)h;
        private static ulong Pack32(int num, int den) => ((ulong)(uint)num << 32) | (uint)den;
}
