using System.Diagnostics;

namespace ApexHighlights;

/// <summary>
/// 「只录游戏」那条链路（WASAPI 进程回环）的自检。
///
/// 进程回环必须真的抓到数据才算通——光「激活成功」不够，Windows 上有的是激活成功但一个包都不给的情况。
/// 为了不依赖游戏，这里把探针挂在自己进程上，然后播一声自己的提示音：
/// PlaySound 是进程内的，所以这一声理应被自己进程的进程回环抓到。
/// 抓到 = 整条路（激活 → 初始化 → 取包 → 转格式）都通了。
/// </summary>
internal static class AudioProbe
{
    public static int Run(int ms)
    {
        int pid = Environment.ProcessId;
        Console.WriteLine($"进程回环探针：目标 pid {pid}，监听 {ms}ms，期间会播一声提示音");

        long packets = 0, bytes = 0;
        LoopbackCapture cap;
        try
        {
            cap = new LoopbackCapture(AudioSourceKind.GameProcess, pid);
            cap.Start((_, n) => { Interlocked.Increment(ref packets); Interlocked.Add(ref bytes, n); });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[X] 进程回环起不来: {ex}");
            return 1;
        }

        using (cap)
        {
            Console.WriteLine($"  已开：{cap.DeviceName}  {cap.SampleRate}Hz/{cap.Channels}ch  运行={cap.Running}");
            if (cap.Err.Length > 0) Console.WriteLine($"  err={cap.Err}");

            Thread.Sleep(150);
            Beep.Saved();                       // 自己的声音，应该被自己抓到

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && Volatile.Read(ref packets) == 0) Thread.Sleep(50);
            Thread.Sleep(200);                  // 再收一会儿，避免只抓到半包

            long p = Volatile.Read(ref packets), b = Volatile.Read(ref bytes);
            Console.WriteLine($"  收到 {p} 包 / {b} 字节（{sw.ElapsedMilliseconds}ms 内）");
            if (p > 0)
            {
                Console.WriteLine("[ok] 进程回环通了：激活、初始化、取包、格式转换全过");
                return 0;
            }

            Console.WriteLine("[!] 一个包都没收到——要么这一声没被记到本进程，要么进程回环被系统挡了");
            return 2;
        }
    }
}
