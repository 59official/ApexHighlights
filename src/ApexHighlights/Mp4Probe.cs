using System.Buffers.Binary;
using System.Text;

namespace ApexHighlights;

/// <summary>
/// 轻量的 mp4 元数据读取：只走 box 树，不开 Media Foundation。
///
/// 为什么不用 source reader：回顾库列表要一次性显示几百片的时长/分辨率，
/// 每片起一个解码器太慢也太重（还会跟正在录制的 NVENC 抢资源）。
/// 我们自己的文件是 ftyp+moov+mdat 渐进式布局，moov 就在文件开头，读几 KB 就够。
/// 解析失败一律返回零值，绝不让 UI 因为一个坏文件崩掉。
/// </summary>
public static class Mp4Probe
{
    public sealed record Info(double Seconds, int Width, int Height, bool HasAudio, string Codec)
    {
        public static readonly Info Empty = new(0, 0, 0, false, "");
        public bool Valid => Width > 0 && Height > 0;
    }

    public static Info Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            long len = fs.Length;
            long pos = 0;
            Span<byte> buf = stackalloc byte[16];

            while (pos + 8 <= len)
            {
                fs.Position = pos;
                if (fs.Read(buf[..8]) != 8) break;
                long size = BinaryPrimitives.ReadInt32BigEndian(buf[..4]);
                var type = Encoding.ASCII.GetString(buf[4..8]);
                int hdr = 8;
                if (size == 1)
                {
                    if (fs.Read(buf[8..16]) != 8) break;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[8..16]);
                    hdr = 16;
                }
                else if (size == 0) size = len - pos;
                if (size < hdr) break;

                if (type == "moov")
                {
                    // moov 通常只有几 KB；给个上限防止畸形文件把内存吃掉
                    int n = (int)Math.Min(size, 4 << 20);
                    var moov = new byte[n];
                    fs.Position = pos + hdr;
                    int got = fs.Read(moov, 0, n - hdr);
                    return ParseMoov(moov, got);
                }
                pos += size;
            }
        }
        catch { /* 坏文件就当没元数据 */ }
        return Info.Empty;
    }

    private static Info ParseMoov(byte[] b, int len)
    {
        double seconds = 0;
        int w = 0, h = 0;
        bool hasAudio = false;
        string codec = "";

        void Walk(int a, int end, int depth)
        {
            while (a + 8 <= end)
            {
                long size = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(a, 4));
                var type = Encoding.ASCII.GetString(b, a + 4, 4);
                if (size < 8 || a + size > end) return;
                int boxEnd = a + (int)size;
                int body = a + 8;

                switch (type)
                {
                    case "mvhd":
                        if (body + 4 <= boxEnd)
                        {
                            int ver = b[body];
                            if (ver == 1 && body + 32 <= boxEnd)
                            {
                                uint ts = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 20, 4));
                                ulong dur = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(body + 24, 8));
                                if (ts > 0) seconds = (double)dur / ts;
                            }
                            else if (body + 20 <= boxEnd)
                            {
                                uint ts = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 12, 4));
                                uint dur = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 16, 4));
                                if (ts > 0) seconds = (double)dur / ts;
                            }
                        }
                        break;

                    case "tkhd":
                        // 宽高是 box 末尾两个 16.16 定点数
                        if (boxEnd - 8 >= a &&
                            BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(boxEnd - 8, 4)) is var fw &&
                            BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(boxEnd - 4, 4)) is var fh)
                        {
                            int cw = (int)(fw >> 16), ch = (int)(fh >> 16);
                            // 音频轨的宽高是 0，视频轨才有值
                            if (cw > w) w = cw;
                            if (ch > h) h = ch;
                        }
                        break;

                    case "hdlr":
                        if (body + 12 <= boxEnd)
                        {
                            var handler = Encoding.ASCII.GetString(b, body + 8, 4);
                            if (handler == "soun") hasAudio = true;
                        }
                        break;

                    case "stsd":
                        // body+8 是 entry_count，之后第一个 sample entry 的 4 字符类型
                        if (body + 16 <= boxEnd && codec.Length == 0)
                            codec = Encoding.ASCII.GetString(b, body + 12, 4).TrimEnd('\0');
                        break;
                }

                if (depth < 6 && (type is "moov" or "trak" or "mdia" or "minf" or "stbl"))
                    Walk(body, boxEnd, depth + 1);
                a = boxEnd;
            }
        }

        Walk(0, len, 0);
        return new Info(seconds, w, h, hasAudio, codec);
    }

    /// <summary>把秒数写成 12.3s / 1:02 这种，UI 里用。</summary>
    public static string Fmt(double s)
    {
        if (s <= 0) return "—";
        if (s < 60) return $"{s:F1}s";
        return $"{(int)(s / 60)}:{(int)(s % 60):D2}";
    }
}
