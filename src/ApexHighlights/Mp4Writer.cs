using System.Buffers.Binary;

namespace ApexHighlights;

/// <summary>
/// 把环形缓冲里的片段拼成**普通（progressive）MP4**：ftyp + moov + mdat(moov 在前)。
/// 不用 fMP4 的原因：Windows Media Foundation 根本不认我们写的那种分片结构
/// （`--mp4-check` 直接报 MF_E_UNSUPPORTED_BYTESTREAM_TYPE，而同样的解码器能正常读系统自带的 mp4）。
/// 分片只用于磁盘上的可追加环形；对外的成品一律转成普通 mp4：双击就播、剪映/PR 也能直接吃。
/// </summary>
internal static class Mp4Writer
{
    public readonly record struct ClipSample(long DtsTicks, int DurTicks, bool Sync, byte[] Avcc);

    // ---- 读回我们自己写的 media segment ----

    public static List<ClipSample> ReadSegment(byte[] file)
    {
        int moofPos = -1, moofLen = 0;
        int p = 0;
        while (p + 8 <= file.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(p, 4));
            string type = System.Text.Encoding.ASCII.GetString(file, p + 4, 4);
            if (sz < 8) break;
            if (type == "moof") { moofPos = p; moofLen = sz; break; }
            p += sz;
        }
        if (moofPos < 0) return new List<ClipSample>();

        ulong baseTime = 0;
        int dataOffset = 0;
        var sizes = new List<int>();
        var durs = new List<int>();
        var sync = new List<bool>();

        int q = moofPos + 8, end = moofPos + moofLen;
        while (q + 8 <= end)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(q, 4));
            string type = System.Text.Encoding.ASCII.GetString(file, q + 4, 4);
            if (sz < 8) break;
            if (type == "traf") Traf(file, q + 8, q + sz);
            q += sz;
        }

        int dataStart = moofPos + dataOffset;
        var list = new List<ClipSample>(sizes.Count);
        ulong t = baseTime;
        int off = dataStart;
        for (int i = 0; i < sizes.Count; i++)
        {
            var bytes = new byte[sizes[i]];
            int avail = Math.Min(sizes[i], file.Length - off);
            if (avail > 0) Array.Copy(file, off, bytes, 0, avail);
            list.Add(new ClipSample((long)t, durs[i], sync[i], bytes));
            off += sizes[i];
            t += (ulong)Math.Max(1, durs[i]);
        }
        return list;

        void Traf(byte[] f, int a, int b)
        {
            while (a + 8 <= b)
            {
                int sz = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(a, 4));
                string type = System.Text.Encoding.ASCII.GetString(f, a + 4, 4);
                if (sz < 8) break;
                if (type == "tfdt")
                {
                    int ver = f[a + 8];
                    baseTime = ver == 1
                        ? BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(a + 12, 8))
                        : BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(a + 12, 4));
                }
                else if (type == "trun")
                {
                    int flags = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(a + 8, 4)) & 0xFFFFFF;
                    int cnt = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(a + 12, 4));
                    dataOffset = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(a + 16, 4));
                    int cp = a + 20;
                    for (int i = 0; i < cnt; i++)
                    {
                        int dur = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(cp, 4));
                        int size = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(cp + 4, 4));
                        int fl = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(cp + 8, 4));
                        durs.Add(dur); sizes.Add(size); sync.Add((fl & 0x10000) == 0);
                        cp += 12;
                    }
                }
                a += sz;
            }
        }
    }

    // ---- 写普通 mp4 ----

    public static byte[] Build(byte[] sps, byte[] pps, int width, int height, IReadOnlyList<ClipSample> samples,
        VideoCodec codec = VideoCodec.H264, byte[]? vps = null)
        => Build(sps, pps, width, height, samples, null, 48000, null, 2, codec, vps);

    /// <summary>
    /// 写普通 mp4。audio 非空时额外写一条 mp4a 音轨（track_id = 2）。
    /// mdat 里先放全部视频样本、再放全部音频样本，两条轨的 stco 各自指向自己的那一段。
    ///
    /// 注意：这个重载会把整个文件拼进一个 byte[]，30 秒 20Mbps 的片段就是 80MB 现分配。
    /// 真正的落盘路径用 <see cref="Write(Stream, byte[], byte[], int, int, IReadOnlyList{ClipSample}, IReadOnlyList{ClipSample}, int, byte[], int)"/>，
    /// 这里是给自检工具（--av-test / --enc-test / --gpu-test）用的便利版本。
    /// </summary>
    public static byte[] Build(byte[] sps, byte[] pps, int width, int height,
        IReadOnlyList<ClipSample> samples,
        IReadOnlyList<ClipSample>? audio, int audioTimescale, byte[]? asc, int audioChannels,
        VideoCodec codec = VideoCodec.H264, byte[]? vps = null)
    {
        using var ms = new MemoryStream();
        Write(ms, sps, pps, width, height, samples, audio, audioTimescale, asc, audioChannels, codec, vps);
        return ms.ToArray();
    }

    /// <summary>
    /// 流式写普通 mp4：算好 stco 偏移后依次把 ftyp、moov、mdat 头、各样本字节写进目标流。
    ///
    /// 为什么要流式：原来先 `new byte[]` 拼整个文件再 `File.WriteAllBytes`，
    /// 意味着切片那一瞬间内存里同时存在「全部样本字节」+「同样大小的输出数组」，
    /// 30 秒 20Mbps 的片段光这一下就 160MB 峰值 —— 这是工作集峰值的主要来源。
    /// 改成直接写文件后，只有样本字节本身还占内存。
    /// </summary>
    /// <returns>写出的总字节数。</returns>
    public static long Write(Stream dst, byte[] sps, byte[] pps, int width, int height,
        IReadOnlyList<ClipSample> samples,
        IReadOnlyList<ClipSample>? audio, int audioTimescale, byte[]? asc, int audioChannels,
        VideoCodec codec = VideoCodec.H264, byte[]? vps = null)
    {
        int n = samples.Count;
        long totalTicks = 0;
        long totalBytes = 0;
        foreach (var s in samples) { totalTicks += s.DurTicks; totalBytes += s.Avcc.Length; }

        int m = audio?.Count ?? 0;
        long audioTicks = 0;
        for (int i = 0; i < m; i++) { audioTicks += audio![i].DurTicks; totalBytes += audio[i].Avcc.Length; }

        var ftyp = Ftyp();
        var offsets = new uint[n];
        var aOffsets = new uint[m];

        var moovProbe = Moov(sps, pps, width, height, samples, offsets, totalTicks,
            audio, aOffsets, audioTimescale, asc, audioChannels, audioTicks, codec, vps);
        long run = ftyp.Length + moovProbe.Length + 8L;
        for (int i = 0; i < n; i++) { offsets[i] = (uint)run; run += samples[i].Avcc.Length; }
        for (int i = 0; i < m; i++) { aOffsets[i] = (uint)run; run += audio![i].Avcc.Length; }

        var moov = Moov(sps, pps, width, height, samples, offsets, totalTicks,
            audio, aOffsets, audioTimescale, asc, audioChannels, audioTicks, codec, vps);
        if (moov.Length != moovProbe.Length) throw new InvalidOperationException("moov 长度不一致");

        long written = 0;
        dst.Write(ftyp, 0, ftyp.Length); written += ftyp.Length;
        dst.Write(moov, 0, moov.Length); written += moov.Length;

        Span<byte> mdatHeader = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(mdatHeader, (int)(8 + totalBytes));
        System.Text.Encoding.ASCII.GetBytes("mdat", mdatHeader[4..]);
        dst.Write(mdatHeader); written += 8;

        foreach (var s in samples) { dst.Write(s.Avcc, 0, s.Avcc.Length); written += s.Avcc.Length; }
        for (int i = 0; i < m; i++) { dst.Write(audio![i].Avcc, 0, audio[i].Avcc.Length); written += audio[i].Avcc.Length; }
        return written;
    }

    private static byte[] Ftyp() => Box("ftyp",
        System.Text.Encoding.ASCII.GetBytes("isom"), U32(0),
        System.Text.Encoding.ASCII.GetBytes("isom"),
        System.Text.Encoding.ASCII.GetBytes("mp41"),
        System.Text.Encoding.ASCII.GetBytes("isom"),
        System.Text.Encoding.ASCII.GetBytes("avc1"));

    /// <summary>
    /// SampleEntry 的公共固定头（avc1/hvc1 都一样）：6 保留 + 2 dref + 2 pre_defined + 2 reserved
    /// + 12 pre_defined + 宽高 + 分辨率 + 保留 + frame_count + compressorname + depth + pre_defined。
    /// </summary>
    internal static byte[] VisualEntryHeader(int w, int h) => Box("_",
        new byte[] { 0, 0, 0, 0, 0, 0 }, U16(1),
        U16(0, 0), U32(0, 0, 0),
        U16((ushort)w, (ushort)h),
        U32(0x00480000, 0x00480000), U32(0), U16(1),
        new byte[32], U16(0x0018), U16(0xFFFF)).AsSpan(8).ToArray();

    private static byte[] Moov(byte[] sps, byte[] pps, int w, int h,
        IReadOnlyList<ClipSample> samples, uint[] offsets, long totalTicks,
        IReadOnlyList<ClipSample>? audio, uint[] aOffsets, int audioTimescale,
        byte[]? asc, int audioChannels, long audioTicks,
        VideoCodec codec = VideoCodec.H264, byte[]? vps = null)
    {
        bool hevc = codec == VideoCodec.Hevc;
        int n = samples.Count;
        int movieMs = (int)Math.Max(1, totalTicks * 1000 / Mp4.Timescale);
        int m = audio?.Count ?? 0;
        if (m > 0)
        {
            long audioMs = audioTicks * 1000 / Math.Max(1, audioTimescale);
            if (audioMs > movieMs) movieMs = (int)Math.Max(1, audioMs);
        }

        // stsd
        // colr（nclx）：明确写 BT.709 + 有限范围。不写的话播放器只能靠猜，
        // 猜成 BT.601 或当成全范围，画面就会发灰、发闷 —— 看着像「脏」。
        var colr = Box("colr",
            System.Text.Encoding.ASCII.GetBytes("nclx"),
            U16(1, 1, 1),          // 原色 bt709 / 传递特性 bt709 / 矩阵 bt709
            new byte[] { 0 });     // full_range_flag = false（16..235）
        var avc1 = hevc
            ? Box("hvc1", VisualEntryHeader(w, h), colr, Mp4.HvcC(vps, sps, pps))
            : Box("avc1", VisualEntryHeader(w, h), colr,
                  Box("avcC", new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1 },
                      U16((ushort)sps.Length), sps, new byte[] { 1 }, U16((ushort)pps.Length), pps));
        var stsd = Full("stsd", 0, 0, U32(1), avc1);

        // stts：run-length
        var sttsEntries = new List<uint>();
        int i0 = 0;
        while (i0 < n)
        {
            int j = i0 + 1;
            while (j < n && samples[j].DurTicks == samples[i0].DurTicks) j++;
            sttsEntries.Add((uint)(j - i0));
            sttsEntries.Add((uint)samples[i0].DurTicks);
            i0 = j;
        }
        var stts = Full("stts", 0, 0, U32((uint)(sttsEntries.Count / 2)), U32(sttsEntries.ToArray()));

        // stsc：每个样本单独一个 chunk
        var stsc = Full("stsc", 0, 0, U32(1), U32(1, 1, 1));

        // stsz
        var sizes = new uint[n];
        for (int i = 0; i < n; i++) sizes[i] = (uint)samples[i].Avcc.Length;
        var stsz = Full("stsz", 0, 0, U32(0, (uint)n), U32(sizes));

        var stco = Full("stco", 0, 0, U32((uint)n), U32(offsets));

        var stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
        var vmhd = Full("vmhd", 0, 1, U16(0, 0, 0, 0));
        var url = Full("url ", 0, 1);
        var dref = Full("dref", 0, 0, U32(1), url);
        var minf = Box("minf", vmhd, Box("dinf", dref), stbl);

        var hdlr = Full("hdlr", 0, 0, U32(0), System.Text.Encoding.ASCII.GetBytes("vide"),
            U32(0, 0, 0), System.Text.Encoding.ASCII.GetBytes("VideoHandler\0"));
        var mdhd = Full("mdhd", 0, 0, U32(0, 0, (uint)Mp4.Timescale, (uint)totalTicks), U16(0x55C4), U16(0));
        var mdia = Box("mdia", mdhd, hdlr, minf);

        var tkhd = Full("tkhd", 0, 7,
            U32(0, 0, 1, 0, (uint)movieMs),
            U32(0, 0), U16(0, 0, 0, 0), Mp4.MatrixBox,
            U32((uint)w << 16, (uint)h << 16));
        var trak = Box("trak", tkhd, mdia);

        // ---- 音频轨（track_id = 2, mp4a + esds）----
        byte[]? atrak = null;
        if (m > 0)
        {
            var ascBytes = asc is { Length: > 0 } ? asc : new byte[] { 0x11, 0x90 };

            // 手工拼 esds 的 descriptor 链（长度都用单字节 varint，ASC 只有 2 字节，够用）
            byte[] B(byte tag, byte[] body)
            {
                var r = new byte[2 + body.Length];
                r[0] = tag;
                r[1] = (byte)body.Length;
                body.CopyTo(r, 2);
                return r;
            }
            var decSpecificInfo = B(0x05, ascBytes);
            // DecoderConfigDescriptor: objectTypeIndication=0x40(AAC), streamType=0x15(audio<<2|1), bufferSizeDB=0, maxBitrate, avgBitrate
            var dcdBody2 = new byte[13];
            dcdBody2[0] = 0x40;
            dcdBody2[1] = 0x15;
            dcdBody2[2] = 0; dcdBody2[3] = 0; dcdBody2[4] = 0;
            uint avgBps = (uint)Math.Max(1, audioTicks > 0 ? audio!.Sum(x => (long)x.Avcc.Length) * audioTimescale / audioTicks : 128000);
            BinaryPrimitives.WriteUInt32BigEndian(dcdBody2.AsSpan(5, 4), avgBps);
            BinaryPrimitives.WriteUInt32BigEndian(dcdBody2.AsSpan(9, 4), avgBps);
            var decoderConfig = B(0x04, Concat(dcdBody2, decSpecificInfo));
            var slConfig = B(0x06, new byte[] { 0x02 });
            var esDescr = B(0x03, Concat(new byte[] { 0, 1, 0 }, decoderConfig, slConfig));
            var esds = Full("esds", 0, 0, esDescr);

            // mp4a SampleEntry
            var mp4a = Box("mp4a",
                new byte[] { 0, 0, 0, 0, 0, 0 }, U16(1),
                U32(0, 0),                // reserved
                U16((ushort)audioChannels), U16(16),  // channelcount, samplesize
                U16(0, 0),                // pre_defined, reserved
                U32((uint)audioTimescale << 16),      // samplerate 16.16
                esds);

            var aStsd = Full("stsd", 0, 0, U32(1), mp4a);

            // 音频 stts
            var aSttsEntries = new List<uint>();
            int j0 = 0;
            while (j0 < m)
            {
                int j = j0 + 1;
                while (j < m && audio![j].DurTicks == audio[j0].DurTicks) j++;
                aSttsEntries.Add((uint)(j - j0));
                aSttsEntries.Add((uint)audio[j0].DurTicks);
                j0 = j;
            }
            var aStts = Full("stts", 0, 0, U32((uint)(aSttsEntries.Count / 2)), U32(aSttsEntries.ToArray()));

            var aSizes = new uint[m];
            for (int i = 0; i < m; i++) aSizes[i] = (uint)audio![i].Avcc.Length;
            var aStsz = Full("stsz", 0, 0, U32(0, (uint)m), U32(aSizes));
            var aStsc = Full("stsc", 0, 0, U32(1), U32(1, 1, 1));
            var aStco = Full("stco", 0, 0, U32((uint)m), U32(aOffsets));
            var aStbl = Box("stbl", aStsd, aStts, aStsc, aStsz, aStco);

            var smhd = Full("smhd", 0, 0, U16(0, 0));
            var aUrl = Full("url ", 0, 1);
            var aDref = Full("dref", 0, 0, U32(1), aUrl);
            var aMinf = Box("minf", smhd, Box("dinf", aDref), aStbl);

            var aHdlr = Full("hdlr", 0, 0, U32(0), System.Text.Encoding.ASCII.GetBytes("soun"),
                U32(0, 0, 0), System.Text.Encoding.ASCII.GetBytes("SoundHandler\0"));
            var aMdhd = Full("mdhd", 0, 0,
                U32(0, 0, (uint)audioTimescale, (uint)audioTicks), U16(0x55C4), U16(0));
            var aMdia = Box("mdia", aMdhd, aHdlr, aMinf);

            var aTkhd = Full("tkhd", 0, 3,     // flags=3: enabled | in_movie
                U32(0, 0, 2, 0, (uint)movieMs),
                U32(0, 0), U16(0, 0), U16(0x0100), U16(0), Mp4.MatrixBox,
                U32(0, 0));
            atrak = Box("trak", aTkhd, aMdia);
        }

        var mvhd = Full("mvhd", 0, 0,
            U32(0, 0, 1000, (uint)movieMs), U32(0x00010000), U16(0x0100), U16(0), U32(0, 0),
            Mp4.MatrixBox, U32(0, 0, 0, 0, 0, 0), U32((uint)(m > 0 ? 3 : 2)));

        return atrak is null ? Box("moov", mvhd, trak) : Box("moov", mvhd, trak, atrak);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (var p in parts) len += p.Length;
        var r = new byte[len];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    // 借用 Mp4 的私有 box 工具
    private static byte[] Box(string type, params byte[][] parts) => Mp4.BoxOf(type, parts);
    private static byte[] Full(string type, byte version, uint flags, params byte[][] parts) => Mp4.FullOf(type, version, flags, parts);
    private static byte[] U32(params uint[] v) => Mp4.U32Of(v);
    private static byte[] U16(params ushort[] v) => Mp4.U16Of(v);
}
