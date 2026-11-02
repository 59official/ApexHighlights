using System.Diagnostics;

namespace ApexHighlights;

/// <summary>
/// 离线的“视频+音频”端到端自检：合成画面 → NVENC，同时合成一段 440Hz 正弦 → AAC，
/// 两条轨都走各自的环形，最后拼成一个 mp4，再用 MF 读回来确认两条轨都在。
/// 不需要开游戏，也不碰真机声卡，专门用来验 mp4a 轨写对没有。
/// </summary>
internal static class AvTest
{
    public static int Run(string outDir, int seconds = 4, int fps = 60)
    {
        Directory.CreateDirectory(outDir);
        var ringDir = Path.Combine(outDir, "ring_av");
        var clipPath = Path.Combine(outDir, "clip_av.mp4");
        int w = 2560, h = 1440;
        if (seconds < 1) seconds = 1;

        int frames = seconds * fps;
        Console.WriteLine($"[1] {w}x{h} @{fps}fps, {frames} 帧 + {seconds}s 音频");

        using var aac = new AacEncoder(48000, 2, 160000);
        Console.WriteLine($"[2] AAC {aac.Name} {aac.SampleRate}Hz/{aac.Channels}ch/{aac.Bitrate}bps  ASC={Convert.ToHexString(aac.Asc)}");

        var aRing = new AudioRing(aac.SampleRate, aac.Channels, 120) { Asc = aac.Asc };
        var vRing = new SegmentRing(ringDir, w, h, 120, fps: 60);
        using var enc = new EncoderChain(w, h, 20_000_000, 60);

        var bgra = new byte[w * h * 4];
        var nv12 = new byte[w * h * 3 / 2];

        // ---- 音频先行：把 seconds 秒的正弦一次性喂完，时间轴和视频对齐在 0 ----
        var swA = Stopwatch.StartNew();
        int pcmPerChunk = 4800;           // 100ms 一块
        var pcm = new byte[pcmPerChunk * 4];
        long fedFrames = 0;
        double phase = 0;
        while (fedFrames < (long)seconds * 48000)
        {
            int n = (int)Math.Min(pcmPerChunk, (long)seconds * 48000 - fedFrames);
            for (int i = 0; i < n; i++)
            {
                double t = (fedFrames + i) / 48000.0;
                short s = (short)(Math.Sin(2 * Math.PI * 440 * t) * 12000);
                // 每 0.5 秒加一次“嘀”的包络，方便肉眼看波形
                double env = 0.5 + 0.5 * Math.Cos(2 * Math.PI * 2 * t);
                s = (short)(s * env);
                pcm[i * 4] = (byte)(s & 0xFF); pcm[i * 4 + 1] = (byte)(s >> 8);
                pcm[i * 4 + 2] = pcm[i * 4]; pcm[i * 4 + 3] = pcm[i * 4 + 1];
            }
            long pts100 = fedFrames * 10_000_000L / 48000;
            aac.Feed(pcm.AsSpan(0, n * 4), pts100, (data, pts) => aRing.Append(pts / 10, data));
            fedFrames += n;
        }
        // 补一个空帧把编码器里的余量冲出来
        aac.Drain((data, pts) => aRing.Append(pts / 10, data));
        swA.Stop();
        var ac = aRing.Coverage();
        Console.WriteLine($"[3] 音频 {aRing.TotalFrames} 帧 / {aRing.DiskBytes()} 字节 / {swA.ElapsedMilliseconds}ms  覆盖({ac.From / 1000},{ac.To / 1000})ms");
        if (aRing.TotalFrames == 0) { Console.WriteLine("[X] 一个 AAC 帧都没编出来"); return 5; }

        // ---- 视频 ----
        var swV = Stopwatch.StartNew();
        int packets = 0;
        for (int f = 0; f < frames; f++)
        {
            Fill(bgra, w, h, f);
            Nv12.FromBgra(bgra, w * 4, w, h, nv12);
            enc.Submit(f * 10_000_000L / fps, nv12, (data, pts) => { packets++; vRing.Append(pts / 10, data); });
        }
        vRing.Flush();
        swV.Stop();
        Console.WriteLine($"[4] 视频 {packets} 包 / {vRing.Segments.Count} 段 / {vRing.DiskBytes() / 1024.0 / 1024.0:F1} MB / {swV.ElapsedMilliseconds}ms");
        if (vRing.Segments.Count == 0) { Console.WriteLine("[X] 视频一段都没落盘"); return 3; }

        // ---- 合成 ----
        var clip = vRing.BuildClip(0, long.MaxValue, aRing);
        File.WriteAllBytes(clipPath, clip);
        Console.WriteLine($"[5] 写出 {clipPath}  {clip.Length / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine("[6] 手动查 mp4 结构：");
        Dump(clip);

        Console.WriteLine("[7] MF 回读：");
        int rc = Mp4Check.Run(clipPath);
        Console.WriteLine(rc == 0 ? "[ok] 双轨都在" : $"[X] 回读失败 rc={rc}");
        return rc;
    }

    /// <summary>不解包，只沿着 box 树把 track 和 handler 打出来——出问题时比 MF 的报错更好定位。</summary>
    private static void Dump(byte[] f)
    {
        var tracks = new List<string>();
        Walk(f, 0, f.Length, 0, tracks);
        foreach (var t in tracks) Console.WriteLine("    " + t);
    }

    private static void Walk(byte[] f, int a, int b, int depth, List<string> log)
    {
        while (a + 8 <= b)
        {
            int sz = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(a, 4));
            string type = System.Text.Encoding.ASCII.GetString(f, a + 4, 4);
            if (sz < 8) return;
            int end = Math.Min(a + sz, b);
            if (depth == 0) log.Add($"{type} @{a} size={sz}");
            if (type is "moov" or "trak" or "mdia" or "minf" or "stbl")
                Walk(f, a + 8, end, depth + 1, log);
            else if (type == "hdlr" && end >= a + 20)
            {
                string handler = System.Text.Encoding.ASCII.GetString(f, a + 16, 4);
                log.Add($"  hdlr={handler}");
            }
            else if (type == "stsd" && end > a + 16)
            {
                string entry = System.Text.Encoding.ASCII.GetString(f, a + 20, Math.Min(4, end - a - 20));
                log.Add($"  stsd entry={entry}");
            }
            a = end;
        }
    }

    private static void Fill(byte[] bgra, int w, int h, int f)
    {
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
