using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 麦克风混音缓冲的自检（不需要真麦克风）：重采样、单声道上混、叠加饱和、缺料、溢出丢帧。
///
/// 这些东西平时只在「真的对着麦说话」时才跑到，跑错了表现为「声音忽大忽小/爆音/整段没声」，
/// 靠耳朵发现太晚，所以这里用合成波形把边界都走一遍。
/// </summary>
internal static class MicTest
{
    private const int Rate = 48000;
    private const int Ch = 2;

    public static int Run()
    {
        int fail = 0;

        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "    ok  " : "    NG  ") + what);
            if (!ok) fail++;
        }

        Console.WriteLine("[mic] 麦克风混音自检");

        // ① 48k/2ch 直通：不该改变样本值，也不该插值出多余帧
        {
            var m = new MicMixer(Rate, Ch);
            var src = Sine(480, Rate, 440, 0.25, 2);
            m.Append(src, src.Length, Rate, 2);
            var dst = new byte[480 * 4];
            m.MixInto(dst, dst.Length);

            var got = Read(dst, 100 * 4);
            var want = Read(src, 100 * 4);
            Check(got == want, $"同采样率直通样本不变（第 100 帧 {want} → {got}）");
            Check(m.Frames == 0, $"刚好取完，队列清空（剩 {m.Frames} 帧）");
        }

        // ② 44.1k 单声道 → 48k 立体声：帧数按比例、两声道等值（单声道复制）
        {
            var m = new MicMixer(Rate, Ch);
            int srcFrames = 44100 / 2;                       // 0.5 秒
            var src = Sine(srcFrames, 44100, 440, 0.5, 1);
            m.Append(src, src.Length, 44100, 1);
            var dst = new byte[24000 * 4];                   // 0.5 秒目标
            m.TakeInto(dst, dst.Length);

            var (l, r) = Read(dst, 1000 * 4);
            int peak = 0;
            for (int i = 0; i + 1 < dst.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)MemoryMarshal.Read<short>(dst.AsSpan(i))));
            Check(l == r, $"单声道复制到左右（{l} / {r}）");
            Check(peak > 12000 && peak < 20000, $"峰值接近 0.5 满幅（{peak}，源 16384）");
            Check(m.Frames < 1200, $"取满 0.5 秒后队列基本掏空（剩 {m.Frames} 帧，重采样会多留一点）");
        }

        // ③ 叠加饱和：30000 + 8000 要钳到 32767，而不是溢出成负数
        {
            var m = new MicMixer(Rate, Ch);
            var src = new byte[2 * 4];
            Write(src, 0, 8000);
            m.Append(src, src.Length, Rate, 2);

            var dst = new byte[4];
            Write(dst, 0, 30000);
            m.MixInto(dst, dst.Length);
            var (l, _) = Read(dst, 0);
            Check(l == 32767, $"叠加超幅钳位（30000+8000 → {l}）");

            var m2 = new MicMixer(Rate, Ch);
            m2.Append(src, src.Length, Rate, 2);
            var neg = new byte[4];
            Write(neg, 0, -30000);
            m2.MixInto(neg, neg.Length);
            var (nl, _) = Read(neg, 0);
            Check(nl == -30000 + 8000, $"不越界时正常相加（-30000+8000 → {nl}）");
        }

        // ④ 缺料：队里没有麦克风数据时，dst 必须原样不动（= 只有系统声）
        {
            var m = new MicMixer(Rate, Ch);
            var dst = new byte[8];
            Write(dst, 0, 1234);
            Write(dst, 2, -1234);
            m.MixInto(dst, dst.Length);
            var (l, r) = Read(dst, 0);
            Check(l == 1234 && r == -1234, $"没料时不动 dst（{l} / {r}）");

            // TakeInto（补静音那条路）相反：没料就该清零，否则补进去的是上一轮的残响
            m.TakeInto(dst, dst.Length);
            var (tl, tr) = Read(dst, 0);
            Check(tl == 0 && tr == 0, $"TakeInto 没料时清零（{tl} / {tr}）");
        }

        // ⑤ 溢出丢帧：塞 4 秒进 2 秒的队列，必须丢最老的而不是无限长
        {
            var m = new MicMixer(Rate, Ch, 2);
            var chunk = Sine(4800, Rate, 220, 0.2, 2);      // 100ms
            for (int i = 0; i < 40; i++) m.Append(chunk, chunk.Length, Rate, 2);
            Check(m.Dropped > 0, $"队列满时丢最老（丢了 {m.Dropped} 帧）");
            Check(m.Frames <= Rate * 2, $"队列不超过 2 秒（{m.Frames} 帧）");
        }

        // ⑥ 运行中换设备格式（44.1k→48k）：不能抛异常，也不能把相位错到爆音
        {
            var m = new MicMixer(Rate, Ch);
            var a = Sine(441, Rate, 300, 0.3, 2);
            m.Append(a, a.Length, 44100, 2);
            var b = Sine(480, Rate, 300, 0.3, 2);
            m.Append(b, b.Length, Rate, 2);
            var dst = new byte[960 * 4];
            m.TakeInto(dst, dst.Length);
            Check(m.Frames < 3000, $"换采样率后队列没堆爆（剩 {m.Frames} 帧）");
        }

        Console.WriteLine(fail == 0 ? "[done] 麦克风混音自检全部通过" : $"[done] {fail} 项失败");
        return fail == 0 ? 0 : 8;
    }

    /// <summary>合成一段正弦，16bit 交错；ch 声道数。</summary>
    private static byte[] Sine(int frames, int rate, double hz, double amp, int ch)
    {
        var buf = new byte[frames * ch * 2];
        double peak = 32767.0 * amp;
        for (int i = 0; i < frames; i++)
        {
            short v = (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / rate) * peak);
            for (int c = 0; c < ch; c++)
                MemoryMarshal.Write(buf.AsSpan(i * ch * 2 + c * 2), v);
        }
        return buf;
    }

    private static (short L, short R) Read(byte[] buf, int at)
        => (MemoryMarshal.Read<short>(buf.AsSpan(at)), MemoryMarshal.Read<short>(buf.AsSpan(at + 2)));

    private static void Write(byte[] buf, int at, short v)
        => MemoryMarshal.Write(buf.AsSpan(at), v);
}
