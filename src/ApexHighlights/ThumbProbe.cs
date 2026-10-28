using System.Diagnostics;

namespace ApexHighlights;

/// <summary>
/// 缩略图取图线程模型探针。
///
/// 为什么单独留一个自检：库窗口的缩略图一度全部取不到，界面上只看到占位方块，
/// 而且**不报错**（Get 内部 catch 掉返回 null）。文件问题、尺寸问题、COM 单元模型
/// 问题，光看代码猜不出来，只能几种线程模型各跑一遍对比。
///
/// 用法：ApexHighlights.exe --thumb-probe=captures/clips
/// </summary>
internal static class ThumbProbe
{
    private const int W = 200;
    private const int H = 120;

    public static int Run(string dir)
    {
        var files = Directory.GetFiles(dir, "*.mp4")
            .OrderByDescending(File.GetLastWriteTime)
            .Take(6)
            .ToArray();
        if (files.Length == 0) { Console.WriteLine("[!] 目录里没有 mp4"); return 9; }

        Console.WriteLine($"[1] 取 {files.Length} 个文件，各测 3 种线程模型，请求 {W}x{H}");

        var rows = files.Select(f => new Row(f) { Current = TryOnCurrent(f) }).ToList();

        Measure(rows, ApartmentState.STA, (r, ok, _) => { r.Sta = ok; });
        Measure(rows, ApartmentState.MTA, (r, ok, _) => { r.Mta = ok; });

        Console.WriteLine();
        Console.WriteLine($"    {"文件",-30}{"当前",6}{"STA线程",9}{"MTA线程",9}");
        foreach (var r in rows)
            Console.WriteLine($"    {Path.GetFileName(r.Path),-30}{Mark(r.Current),6}{Mark(r.Sta),9}{Mark(r.Mta),9}");

        Console.WriteLine();
        Console.WriteLine($"汇总：当前 {rows.Count(r => r.Current)}/{rows.Count}，" +
                          $"STA {rows.Count(r => r.Sta)}/{rows.Count}，MTA {rows.Count(r => r.Mta)}/{rows.Count}");
        return rows.Count(r => r.Current) == rows.Count ? 0 : 7;
    }

    private static string Mark(bool ok) => ok ? "ok" : "null";

    private static bool TryOnCurrent(string path)
    {
        var bmp = ShellThumb.Get(path, W, H);
        if (bmp is null) return false;
        bool ok = bmp.Width > 1 && bmp.Height > 1;
        bmp.Dispose();
        return ok;
    }

    /// <summary>整批文件都跑到同一个指定单元模型的线程上，逐文件记录成败。</summary>
    private static void Measure(List<Row> rows, ApartmentState apt, Action<Row, bool, long> record)
    {
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            foreach (var r in rows)
            {
                var sw = Stopwatch.StartNew();
                bool ok;
                try { ok = TryOnCurrent(r.Path); } catch { ok = false; }
                sw.Stop();
                record(r, ok, sw.ElapsedMilliseconds);
            }
            done.Set();
        })
        {
            IsBackground = true,
            Name = "probe-" + apt,
        };
        t.SetApartmentState(apt);
        t.Start();
        done.Wait(TimeSpan.FromSeconds(30));
    }

    private sealed class Row
    {
        public Row(string path) => Path = path;
        public string Path { get; }
        public bool Current { get; set; }
        public bool Sta { get; set; }
        public bool Mta { get; set; }
    }
}
