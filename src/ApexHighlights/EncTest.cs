using System.Diagnostics;

namespace ApexHighlights;

/// <summary>离线自检：合成画面 → NV12 → NVENC → 分片环形 → 拼一个可播放的 mp4。</summary>
internal static class EncTest
{
    public static int Run(int w, int h, int frames, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var ringDir = Path.Combine(outDir, "ring");
        var clipPath = Path.Combine(outDir, "clip.mp4");

        Console.WriteLine($"[1] 编码器 {w}x{h} @ {frames} 帧");
        var enc = new EncoderChain(w, h, 20_000_000, 60);
        Console.WriteLine($"[2] 选中 {enc.Name}（每 60 帧换编码器拿 IDR）");

        var ring = new SegmentRing(ringDir, w, h, 120, fps: 60);
        var restartSw = new Stopwatch();
        int restarts = 0;
        var bgra = new byte[w * h * 4];
        var nv12 = new byte[w * h * 3 / 2];

        int packets = 0, idrs = 0;

        long bytes = 0;
        var swConv = new Stopwatch();
        var swAll = Stopwatch.StartNew();

        for (int f = 0; f < frames; f++)
        {
            Fill(bgra, w, h, f);
            swConv.Start();
            Nv12.FromBgra(bgra, w * 4, w, h, nv12);
            swConv.Stop();

            int fi = f;
            enc.Submit(f * 166_667L, nv12, (data, pts) =>
            {
                packets++;
                if (fi % 60 == 0)
                {
                    var types = string.Join("", Mp4.SplitAnnexB(data).Select(n => n.Type.ToString()));
                    Console.WriteLine($"    f{fi} types={types}");
                }
                bytes += data.Length;
                foreach (var n in Mp4.SplitAnnexB(data))
                {
                    if (n.Type == 5) idrs++;
                    else if (n.Type == 7 && idrs == 0) { }
                }
                ring.Append(pts / 10, data);

            });
        }
        swAll.Stop();
        ring.Flush();

        Console.WriteLine($"[3] 编码包 {packets}（IDR {idrs}）  推入 {enc.Pushed} 丢 {enc.Dropped}");
        Console.WriteLine($"[3b] 换编码器 {enc.Rotations} 次，累计阻塞 {enc.RotateMs} ms");
        Console.WriteLine($"[4] 转换 {swConv.Elapsed.TotalMilliseconds / frames:F2} ms/帧   总 {swAll.Elapsed.TotalMilliseconds / frames:F2} ms/帧");
        Console.WriteLine($"[5] 分片 {ring.Segments.Count} 个，磁盘 {ring.DiskBytes() / 1024.0 / 1024.0:F1} MB，覆盖 {ring.Coverage()}");

        if (ring.Segments.Count == 0) { Console.WriteLine("[X] 一段都没落盘"); return 3; }

        var gaps = new List<long>();
        for (int i = 1; i < ring.Segments.Count; i++)
            gaps.Add(ring.Segments[i].StartUs - ring.Segments[i - 1].StartUs);
        Console.WriteLine($"[6] 段间隔 us: {string.Join(", ", gaps.Select(g => (g / 1000.0).ToString("F0") + "ms"))}");

        var clip = ring.BuildClip(0, long.MaxValue);
        File.WriteAllBytes(clipPath, clip);
        Console.WriteLine($"[7] 写出 {clipPath}  {clip.Length / 1024.0 / 1024.0:F1} MB  (init {ring.Init!.Length} 字节)");
        Console.WriteLine($"[9] MF 回读 clip.mp4: {(Mp4Check.Run(clipPath, requireAudio: false) == 0 ? "ok" : "失败")}");

        Console.WriteLine($"[8] 码率 {(bytes * 8.0 / Math.Max(1, frames) * 60 / 1e6):F1} Mbps");
        return 0;
    }

    private static void Fill(byte[] bgra, int w, int h, int f)
    {
        // 渐变背景 + 一条横扫的亮带 + 轻微噪点：让码率接近真实游戏画面
        int bar = (f * 23) % w;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            byte baseV = (byte)(40 + y * 120 / h);
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int v = baseV + (x >> 3) % 60;
                if (Math.Abs(x - bar) < 60) v += 140;
                v += (x ^ y) & 7;
                bgra[i] = (byte)(v * 0.9);
                bgra[i + 1] = (byte)Math.Min(255, v);
                bgra[i + 2] = (byte)Math.Min(255, v * 0.7);
                bgra[i + 3] = 255;
            }
        }
    }
}
