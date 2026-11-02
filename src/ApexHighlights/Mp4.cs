using System.Buffers.Binary;
using System.Text;

namespace ApexHighlights;

/// <summary>一段 fMP4 里的一个样本（= 一个 access unit）。</summary>
public readonly record struct SegSample(int Duration, int Size, bool Sync);

/// <summary>
/// 手写 fMP4（ISO BMFF）：一个 init segment + 若干 media segment。
/// 每个 media segment 自己带 moof+mdat，字节拼接即成可播放片段，不需要重封装。
/// </summary>
public static class Mp4
{
    public const int Timescale = 90000;

    // ---- 盒子构造 ----

    private static byte[] Box(string type, params byte[][] parts)
    {
        int size = 8;
        foreach (var p in parts) size += p.Length;
        var b = new byte[size];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0, 4), size);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        int off = 8;
        foreach (var p in parts) { p.CopyTo(b, off); off += p.Length; }
        return b;
    }

    private static byte[] Full(string type, byte version, uint flags, params byte[][] parts)
    {
        var head = new byte[4];
        head[0] = version;
        head[1] = (byte)(flags >> 16);
        head[2] = (byte)(flags >> 8);
        head[3] = (byte)flags;
        var all = new byte[parts.Length + 1][];
        all[0] = head;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Box(type, all);
    }

    private static byte[] U32(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4, 4), v[i]);
        return b;
    }

    private static byte[] U16(params ushort[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i * 2, 2), v[i]);
        return b;
    }

    private static byte[] I32(params int[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(i * 4, 4), v[i]);
        return b;
    }

    private static byte[] U64(params ulong[] v)
    {
        var b = new byte[v.Length * 8];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(i * 8, 8), v[i]);
        return b;
    }

    /// <summary>
    /// HEVC 的 hvcC：三个参数集（VPS/SPS/PPS）+ 从 SPS 里抄出来的 profile/tier/level。
    /// 手写比特解析容易错，这里只取固定偏移 —— SPS 的负载开头就是
    /// vps_id(4) max_sub_layers(3) nesting(1) / space(2) tier(1) idc(5) / compat(32) / constraint(48) / level(8)。
    /// </summary>
    /// <summary>去掉 NAL 里的防竞争字节（00 00 03），从头部的 <paramref name="from"/> 字节起算。</summary>
    private static byte[] StripEpb(byte[] nal, int from)
    {
        var dst = new List<byte>(Math.Max(0, nal.Length - from));
        int zeros = 0;
        for (int i = from; i < nal.Length; i++)
        {
            byte v = nal[i];
            if (zeros >= 2 && v == 3) { zeros = 0; continue; }   // 这就是插进去的那个 03
            dst.Add(v);
            zeros = v == 0 ? zeros + 1 : 0;
        }
        return dst.ToArray();
    }

    public static byte[] HvcC(byte[]? vps, byte[] sps, byte[] pps)
    {
        // SPS 前两个字节是 NAL 头。**必须先去掉防竞争字节（00 00 03 → 00 00）**：
        // profile_tier_level 是按去防竞争后的比特流定的，直接按原字节数偏移会整体错一格，
        // compat/constraint/level 全读错（实测 level 会读成 0，播放器直接不认这个 hvcC）。
        var sp = StripEpb(sps, 2);
        byte ptl = sp.Length > 1 ? sp[1] : (byte)0x01;   // space|tier|profile_idc
        int profileIdc = ptl & 0x1F;
        int tier = (ptl >> 5) & 1;
        int space = (ptl >> 6) & 3;
        var compat = new byte[4];
        var cons = new byte[6];
        for (int i = 0; i < 4; i++) compat[i] = sp.Length > 2 + i ? sp[2 + i] : (byte)0;
        for (int i = 0; i < 6; i++) cons[i] = sp.Length > 6 + i ? sp[6 + i] : (byte)0;
        byte level = sp.Length > 12 ? sp[12] : (byte)123;   // 4.1

        var arrays = new List<byte>();
        int count = 0;
        void AddArray(int type, byte[]? nal)
        {
            if (nal is null || nal.Length == 0) return;
            count++;
            arrays.Add((byte)(0x80 | type));            // array_completeness=1
            arrays.AddRange(U16((ushort)1));
            arrays.AddRange(U16((ushort)nal.Length));
            arrays.AddRange(nal);
        }
        AddArray(32, vps);
        AddArray(33, sps);
        AddArray(34, pps);

        var head2 = new List<byte>
        {
            1,                                          // configurationVersion
            (byte)((space << 6) | (tier << 5) | profileIdc),
        };
        head2.AddRange(compat);
        head2.AddRange(cons);
        head2.Add(level);
        head2.Add(0xF0);                                // min_spatial_segmentation_idc = 0
        head2.Add(0x00);
        head2.Add(0xFC);                                // parallelismType = 0
        head2.Add(0xFD);                                // chromaFormat = 1 (4:2:0)
        head2.Add(0xF8);                                // bitDepthLuma = 8
        head2.Add(0xF8);                                // bitDepthChroma = 8
        head2.Add(0x00); head2.Add(0x00);               // avgFrameRate
        head2.Add(0x03);                                // constantFrameRate|temporalLayers|nested|lengthSize-1
        head2.Add((byte)count);
        head2.AddRange(arrays);
        return Box("hvcC", head2.ToArray());
    }

    private static readonly byte[] Matrix = Box("_", U32(
        0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000)).AsSpan(8).ToArray();

    // ---- init segment ----

    public static byte[] BuildInit(byte[] sps, byte[] pps, int width, int height,
        VideoCodec codec = VideoCodec.H264, byte[]? vps = null)
    {
        bool hevc = codec == VideoCodec.Hevc;
        var ftyp = Box("ftyp",
            Encoding.ASCII.GetBytes("isom"), U32(512),
            Encoding.ASCII.GetBytes("isom"), Encoding.ASCII.GetBytes("iso5"),
            Encoding.ASCII.GetBytes(hevc ? "hvc1" : "avc1"), Encoding.ASCII.GetBytes("mp41"));

        var sample = hevc
            ? Box("hvc1", Mp4Writer.VisualEntryHeader(width, height), HvcC(vps, sps, pps))
            : Box("avc1", Mp4Writer.VisualEntryHeader(width, height),
                  Box("avcC", new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1 },
                      U16((ushort)sps.Length), sps, new byte[] { 1 }, U16((ushort)pps.Length), pps));
        var avc1 = sample;

        var stsd = Full("stsd", 0, 0, U32(1), avc1);
        var stts = Full("stts", 0, 0, U32(0));
        var stsc = Full("stsc", 0, 0, U32(0));
        var stsz = Full("stsz", 0, 0, U32(0, 0));
        var stco = Full("stco", 0, 0, U32(0));
        var stbl = Box("stbl", stsd, stts, stsc, stsz, stco);

        var vmhd = Full("vmhd", 0, 1, U16(0, 0, 0, 0));
        var url = Full("url ", 0, 1);
        var dref = Full("dref", 0, 0, U32(1), url);
        var dinf = Box("dinf", dref);
        var minf = Box("minf", vmhd, dinf, stbl);

        var hdlr = Full("hdlr", 0, 0, U32(0), Encoding.ASCII.GetBytes("vide"),
            U32(0, 0, 0), Encoding.ASCII.GetBytes("VideoHandler\0"));
        var mdhd = Full("mdhd", 0, 0, U32(0, 0, (uint)Timescale, 0), U16(0x55C4), U16(0));
        var mdia = Box("mdia", mdhd, hdlr, minf);

        var tkhd = Full("tkhd", 0, 7,
            U32(0, 0, 1, 0, 0),                 // creation, modification, track_id, reserved, duration
            U32(0, 0),                          // reserved[2]
            U16(0, 0, 0, 0),                    // layer, alternate_group, volume, reserved
            Matrix,
            U32((uint)width << 16, (uint)height << 16));
        var trak = Box("trak", tkhd, mdia);

        var mvhd = Full("mvhd", 0, 0,
            U32(0, 0, 1000, 0), U32(0x00010000), U16(0x0100), U16(0), U32(0, 0),
            Matrix, U32(0, 0, 0, 0, 0, 0), U32(2));
        var trex = Full("trex", 0, 0, U32(1, 1, 0, 0, 0));
        var moov = Box("moov", mvhd, trak, Box("mvex", trex));

        var res = new byte[ftyp.Length + moov.Length];
        ftyp.CopyTo(res, 0);
        moov.CopyTo(res, ftyp.Length);
        return res;
    }

    // ---- media segment ----

    public static byte[] BuildSegment(uint seq, ulong baseDecodeTime, IReadOnlyList<SegSample> samples, byte[] payload)
    {
        int n = samples.Count;

        // trun 里的每样本三元组：duration, size, flags
        var perSample = new byte[n * 12];
        for (int i = 0; i < n; i++)
        {
            var s = samples[i];
            BinaryPrimitives.WriteUInt32BigEndian(perSample.AsSpan(i * 12, 4), (uint)s.Duration);
            BinaryPrimitives.WriteUInt32BigEndian(perSample.AsSpan(i * 12 + 4, 4), (uint)s.Size);
            uint flags = s.Sync ? 0x02000000u : 0x01010000u;   // depends_on=2(独立)/1，non_sync 位
            BinaryPrimitives.WriteUInt32BigEndian(perSample.AsSpan(i * 12 + 8, 4), flags);
        }

        // 先估一个 data_offset 的行宽，再回填（trun 长度固定，先写 0 再 patch）
        var mfhd = Full("mfhd", 0, 0, U32(seq));
        var tfhd = Full("tfhd", 0, 0x020000, U32(1));               // default-base-is-moof
        var tfdt = Full("tfdt", 1, 0, U64(baseDecodeTime));

        byte[] BuildTrun(int dataOffset)
        {
            var trun = Full("trun", 0, 0x000701,
                U32((uint)n), I32(dataOffset), perSample);
            return trun;
        }

        // 迭代一次让 offset 收敛（长度不随内容变化，所以一次就够）
        var probe = BuildTrun(0);
        int trafLen = tfhd.Length + tfdt.Length + probe.Length;
        int moofLen = 8 + mfhd.Length + 8 + trafLen;
        int dataOffsetFinal = moofLen + 8;                          // mdat 头之后

        var traf = Box("traf", tfhd, tfdt, BuildTrun(dataOffsetFinal));
        var moof = Box("moof", mfhd, traf);
        var mdat = Box("mdat", payload);

        if (moof.Length + mdat.Length != moofLen + mdat.Length)
            throw new InvalidOperationException("moof 长度估算与实际不一致");

        var res = new byte[moof.Length + mdat.Length];
        moof.CopyTo(res, 0);
        mdat.CopyTo(res, moof.Length);
        return res;
    }

    // ---- 给 Mp4Writer 用的内部桥接 ----

    internal static byte[] MatrixBox => Matrix;
    internal static byte[] BoxOf(string type, byte[][] parts) => Box(type, parts);
    internal static byte[] FullOf(string type, byte version, uint flags, byte[][] parts) => Full(type, version, flags, parts);
    internal static byte[] U32Of(params uint[] v) => U32(v);
    internal static byte[] U16Of(params ushort[] v) => U16(v);

    // ---- Annex-B → AVCC ----

    public readonly record struct Nal(int Start, int Length, int Type);

    /// <summary>拆 Annex-B 的起始码，返回每个 NAL 的位置与类型。</summary>
    public static List<Nal> SplitAnnexB(ReadOnlySpan<byte> d, bool hevc = false)
    {
        var list = new List<Nal>();
        int i = 0;
        while (i + 3 <= d.Length)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                int start = i + 3;
                int j = start;
                while (j + 3 <= d.Length && !(d[j] == 0 && d[j + 1] == 0 && d[j + 2] == 1)) j++;
                int end = (j + 3 <= d.Length) ? j : d.Length;
                // 去掉尾部可能的 0 填充
                while (end > start && d[end - 1] == 0) end--;
                // H.264 的类型在首字节低 5 位；HEVC 在头两字节里：forbidden(1) type(6) layer(6) tid(3)
                int type = hevc ? (d[start] >> 1) & 0x3F : d[start] & 0x1F;
                if (end > start) list.Add(new Nal(start, end - start, type));
                i = (end == d.Length) ? d.Length : end;
            }
            else i++;
        }
        return list;
    }

    /// <summary>把一个 AU 的 NAL 转成 4 字节长度前缀的形式（mp4 用）。</summary>
    public static byte[] ToAvcc(ReadOnlySpan<byte> d, List<Nal> nals)
    {
        int total = 0;
        foreach (var n in nals) total += 4 + n.Length;
        var res = new byte[total];
        int o = 0;
        foreach (var n in nals)
        {
            BinaryPrimitives.WriteInt32BigEndian(res.AsSpan(o, 4), n.Length);
            d.Slice(n.Start, n.Length).CopyTo(res.AsSpan(o + 4));
            o += 4 + n.Length;
        }
        return res;
    }
}
