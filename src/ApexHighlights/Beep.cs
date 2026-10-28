using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 录制完成的提示音。用户要求「有一声提示音，但不弹任何窗口」，所以整个实现只有一次 PlaySound。
/// 音源在内存里现场合成一个 WAV，不依赖任何外部音频文件，也就不怕用户把目录挪走。
/// </summary>
internal static class Beep
{
    private const int SND_ASYNC = 0x0001;
    private const int SND_NODEFAULT = 0x0002;
    private const int SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(byte[]? data, IntPtr mod, int flags);

    private static byte[]? _wav;
    private static readonly object _lock = new();

    /// <summary>提醒用户「刚录好一段」。三条轨并发的概率不高，加锁只为防重入爆音。</summary>
    public static void Saved()
    {
        lock (_lock)
        {
            _wav ??= Build();
            try { PlaySound(_wav, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT); } catch { }
        }
    }

    public static void Stop()
    {
        try { PlaySound(null, IntPtr.Zero, 0); } catch { }
    }

    /// <summary>自检：把提示音写成 wav 并播一次，方便确认格式合法。</summary>
    public static int Dump(string path)
    {
        var wav = Build();
        File.WriteAllBytes(path, wav);
        Console.WriteLine($"写出 {path}  {wav.Length} 字节  {wav.Length / 2.0 / 22050 * 1000:F0}ms");
        Saved();
        Thread.Sleep(400);
        return 0;
    }

    /// <summary>两个上行音（C6→G6），总共 180ms：短到不打扰，又明显盖过游戏声。</summary>
    private static byte[] Build()
    {
        const int rate = 22050;
        int n1 = rate * 80 / 1000;   // 80ms
        int n2 = rate * 100 / 1000;  // 100ms
        int total = n1 + n2;
        var pcm = new byte[total * 2];
        int o = 0;
        void Tone(double freq, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double t = i / (double)rate;
                // 头尾各 8ms 淡入淡出，削掉爆音
                double env = 1;
                int fade = rate * 8 / 1000;
                if (i < fade) env = i / (double)fade;
                else if (i > count - fade) env = (count - i) / (double)fade;
                short v = (short)(Math.Sin(2 * Math.PI * freq * t) * 9000 * env);
                pcm[o++] = (byte)(v & 0xFF);
                pcm[o++] = (byte)(v >> 8);
            }
        }
        Tone(1046.5, n1);  // C6
        Tone(1568.0, n2);  // G6

        var wav = new byte[44 + pcm.Length];
        void S(string s, int at) { for (int i = 0; i < s.Length; i++) wav[at + i] = (byte)s[i]; }
        void U32(int v, int at) { wav[at] = (byte)v; wav[at + 1] = (byte)(v >> 8); wav[at + 2] = (byte)(v >> 16); wav[at + 3] = (byte)(v >> 24); }
        void U16(int v, int at) { wav[at] = (byte)v; wav[at + 1] = (byte)(v >> 8); }

        S("RIFF", 0); U32(36 + pcm.Length, 4); S("WAVE", 8);
        S("fmt ", 12); U32(16, 16); U16(1, 20); U16(1, 22);
        U32(rate, 24); U32(rate * 2, 28); U16(2, 32); U16(16, 34);
        S("data", 36); U32(pcm.Length, 40);
        pcm.CopyTo(wav, 44);
        return wav;
    }
}
