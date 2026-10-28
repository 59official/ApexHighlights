using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>离线自检：WASAPI loopback 抓 3 秒，报告设备格式、包数、电平和音调。</summary>
internal static class AudioTest
{
    public static int Run(int seconds)
    {
        using var cap = new LoopbackCapture();
        long frames = 0, peak = 0;
        double sumSq = 0;
        var first = new List<short[]>();
        var sw = Stopwatch.StartNew();

        cap.Start((buf, n) =>
        {
            frames += n / 4;
            // 电平 + 峰值
            for (int i = 0; i + 1 < n; i += 2)
            {
                short s = (short)(buf[i] | (buf[i + 1] << 8));
                int a = Math.Abs((int)s);
                if (a > peak) peak = a;
                sumSq += (double)s * s;
            }
            if (first.Count < 3)
            {
                var copy = new short[n / 2];
                for (int i = 0; i < copy.Length; i++) copy[i] = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8));
                first.Add(copy);
            }
        });

        Console.WriteLine($"[1] 设备 {cap.DeviceName}");
        Console.WriteLine($"[2] 混音格式 {cap.SampleRate} Hz / {cap.Channels} ch / {cap.Bits} bit float={cap.IsFloat}");

        // 抓满指定秒数
        while (sw.Elapsed.TotalSeconds < seconds) Thread.Sleep(50);

        double sec = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[3] {sec:F1}s 内 {cap.Packets} 个 packet / {frames} 帧 / {frames / sec:F0} 帧每秒（应≈{cap.SampleRate}）");
        Console.WriteLine($"[4] 峰值 {peak} ({20 * Math.Log10(Math.Max(1, peak) / 32768.0):F1} dBFS)  RMS {Math.Sqrt(sumSq / Math.Max(1, frames) / 32768.0 * 32768.0):F1}");

        if (cap.Err.Length > 0) Console.WriteLine($"[X] {cap.Err}");

        // 静音检测：如果全是 0，说明没有声音在播（或 loopback 没通）
        if (peak == 0)
            Console.WriteLine("[!] 全静音 —— 要么当前没声音在播，要么 loopback 没抓到（跑之前先放点声音）");

        // 过零率粗判是否有真实波形
        if (first.Count > 0)
        {
            var s = first[0];
            int zc = 0;
            for (int i = 1; i < s.Length; i++) if ((s[i - 1] < 0) != (s[i] < 0)) zc++;
            Console.WriteLine($"[5] 首包 {s.Length} 帧样本，过零 {zc} 次（{(double)zc / s.Length * cap.SampleRate / 2:F0} Hz 粗估基频）");
        }
        return peak > 0 ? 0 : 5;
    }
}
