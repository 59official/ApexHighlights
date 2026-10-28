using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace ApexHighlights;

/// <summary>
/// Microsoft AAC Audio Encoder MFT 封装（同步 MFT，不是异步的，所以可以直接推-拉）。
/// 输入固定为 16bit 立体声 PCM，输出 AAC 裸帧（access unit，不带 ADTS 头）——裸帧才能进 mp4 的 mp4a 轨道。
/// 时间戳按累计样本数算，而不是每次读时钟：这样音频时间轴不会因为回调抖动而漂移。
/// </summary>
public sealed class AacEncoder : IDisposable
{
    private readonly IMFTransform _enc;
    private readonly IMFSample _inSample;
    private readonly IMFMediaBuffer _inBuf;
    private readonly bool _provides;
    private readonly int _osiSize;
    private readonly int _frameBytes;      // 一个 AAC 帧对应的 PCM 字节数（1024 样本 × 声道 × 2）
    private readonly int _blockAlign;

    private byte[] _pending = new byte[0];
    private int _pendingCount;
    private long _samplesFed;
    private bool _streaming;

    public int SampleRate { get; }
    public int Channels { get; }
    public int Bitrate { get; }
    /// <summary>AudioSpecificConfig（从输出类型的 MF_MT_USER_DATA 里取），写 mp4 的 esds 用。</summary>
    public byte[] Asc { get; private set; } = Array.Empty<byte>();
    public string Name { get; private set; } = "?";
    public long Frames { get; private set; }
    public long Bytes { get; private set; }
    public string Err = "";

    public AacEncoder(int sampleRate = 48000, int channels = 2, int bitrate = 160000)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Bitrate = bitrate;
        _blockAlign = channels * 2;
        _frameBytes = 1024 * _blockAlign;

        MediaFactory.MFStartup(false);

        var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, 0x7, inInfo, outInfo, out var ptrs, out var count);
        if (count == 0) throw new InvalidOperationException("机器上没有 AAC 编码器");

        var arr = new IntPtr[count];
        Marshal.Copy(ptrs, arr, 0, (int)count);
        IMFActivate? chosen = null;
        for (int i = 0; i < arr.Length; i++)
        {
            var a = new IMFActivate(arr[i]);
            string nm = "?";
            try { nm = a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "?"; } catch { }
            if (chosen is null || nm.Contains("AAC", StringComparison.OrdinalIgnoreCase))
            {
                if (chosen is not null) { a.Dispose(); continue; }
                chosen = a;
                Name = nm;
            }
            else a.Dispose();
        }
        if (chosen is null) throw new InvalidOperationException("AAC 编码器 activate 失败");

        _enc = chosen.ActivateObject<IMFTransform>();
        chosen.Dispose();

        // 输出类型：直接从可用类型里挑 48000/2ch/160000，比自己拼一个稳（UserData 里带 ASC）
        IMFMediaType? outType = null;
        for (int i = 0; i < 32; i++)
        {
            IMFMediaType? mt = null;
            try { mt = _enc.GetOutputAvailableType(0, i); } catch { break; }
            if (mt is null) break;
            bool ok;
            try
            {
                ok = mt.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond) == (uint)sampleRate
                     && mt.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels) == (uint)channels
                     && mt.GetUInt32(MediaTypeAttributeKeys.AvgBitrate) == (uint)bitrate;
            }
            catch { ok = false; }
            if (ok && outType is null) outType = mt;
            else mt.Dispose();
        }
        if (outType is null) throw new InvalidOperationException($"{sampleRate}Hz/{channels}ch/{bitrate}bps 的 AAC 输出类型不存在");

        _enc.SetOutputType(0, outType, 0);
        try
        {
            // MF_MT_USER_DATA 对 AAC 是 HEAACWAVEINFO 去掉 WAVEFORMATEX 之后的部分：
            // 固定 12 字节头（payloadType / profileLevel / audioType ...），后面才是真正的 ASC。
            // 整个 14 字节都写进 esds 会让解码器把 0x0000 读成 audioObjectType=0、索引=0 → 96000Hz。
            var ud = outType.GetBlob(MediaTypeAttributeKeys.UserData);
            Asc = ud is { Length: > 12 } ? ud[12..] : Array.Empty<byte>();
        }
        catch { Asc = Array.Empty<byte>(); }
        outType.Dispose();

        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)_blockAlign);
        inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * _blockAlign));
        _enc.SetInputType(0, inType, 0);
        inType.Dispose();

        var osi = _enc.GetOutputStreamInfo(0);
        _provides = (osi.Flags & 0x100) != 0;   // MF_OSTREAM_PROVIDES_SAMPLES
        _osiSize = Math.Max(osi.Size, 4096);

        _inBuf = MediaFactory.MFCreateMemoryBuffer(_frameBytes);
        _inSample = MediaFactory.MFCreateSample();
        _inSample.AddBuffer(_inBuf);

        _enc.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _enc.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _streaming = true;
    }

    /// <summary>喂一段交错立体声 int16 PCM；每编出一个 AAC 帧就回调（裸帧字节 + 100ns 时间戳）。</summary>
    public void Feed(ReadOnlySpan<byte> pcm, long firstPts100, Action<byte[], long> onFrame)
    {
        if (!_streaming || pcm.Length <= 0) return;

        // 第一次喂数据时定下时间基准，之后全部按累计样本数推算，避免回调抖动导致漂移
        if (firstPtsOfBlock == long.MinValue) firstPtsOfBlock = firstPts100;

        // 补够整帧：不足 1024 样本先攒着
        int off = 0;
        if (_pendingCount > 0)
        {
            int need = _frameBytes - _pendingCount;
            int take = Math.Min(need, pcm.Length);
            pcm[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            off += take;
            if (_pendingCount < _frameBytes) return;
            SubmitBlock(_pending.AsSpan(0, _frameBytes), onFrame);
            _pendingCount = 0;
        }

        while (off + _frameBytes <= pcm.Length)
        {
            SubmitBlock(pcm.Slice(off, _frameBytes), onFrame);
            off += _frameBytes;
        }

        int rest = pcm.Length - off;
        if (rest > 0)
        {
            if (_pending.Length < _frameBytes) Array.Resize(ref _pending, _frameBytes);
            pcm[off..].CopyTo(_pending);
            _pendingCount = rest;
        }
    }

    private void SubmitBlock(ReadOnlySpan<byte> block, Action<byte[], long> onFrame)
    {
        long pts = firstPtsOfBlock + _samplesFed * 10_000_000L / SampleRate;
        _samplesFed += 1024;

        _inBuf.Lock(out IntPtr p, out _, out _);
        unsafe { block.CopyTo(new Span<byte>((void*)p, _frameBytes)); }
        _inBuf.Unlock();
        _inBuf.CurrentLength = _frameBytes;

        _inSample.SampleTime = pts;
        _inSample.SampleDuration = 1024L * 10_000_000L / SampleRate;

        try { _enc.ProcessInput(0, _inSample, 0); }
        catch (Exception e) { Err = "ProcessInput: " + e.Message; return; }

        Drain(onFrame);
    }

    // 第一帧的时间基准（Feed 第一次调用时记下来）
    private long firstPtsOfBlock = long.MinValue;

    /// <summary>已经喂进编码器的样点数。时间轴就是这个数推出来的，补静音线程靠它算缺口。</summary>
    public long SamplesFed => _samplesFed;


    /// <summary>把编码器里已经就绪的输出全部取走。</summary>
    public void Drain(Action<byte[], long> onFrame)
    {
        for (int guard = 0; guard < 16; guard++)
        {
            var odb = new OutputDataBuffer();
            IMFSample? own = null;
            if (!_provides)
            {
                var mb = MediaFactory.MFCreateMemoryBuffer(_osiSize);
                own = MediaFactory.MFCreateSample();
                own.AddBuffer(mb);
                odb.Sample = own;
            }
            SharpGen.Runtime.Result res;
            try { res = _enc.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _); }
            catch (Exception e) { own?.Dispose(); Err = "ProcessOutput: " + e.Message; return; }

            if (res.Failure || odb.Sample is null) { own?.Dispose(); return; }

            var sample = odb.Sample;
            try
            {
                var buf = sample.ConvertToContiguousBuffer();
                buf.Lock(out IntPtr rp, out _, out int cur);
                var data = new byte[cur];
                if (cur > 0) Marshal.Copy(rp, data, 0, cur);
                buf.Unlock();
                buf.Dispose();
                if (cur > 0)
                {
                    Frames++;
                    Bytes += cur;
                    onFrame(data, sample.SampleTime);
                }
            }
            finally { sample.Dispose(); }
        }
    }

    public void Dispose()
    {
        try { if (_streaming) _enc.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        _inSample?.Dispose();
        _inBuf?.Dispose();
        _enc?.Dispose();
    }
}
