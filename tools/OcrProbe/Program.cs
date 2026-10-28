using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0] == "--mem") return await Mem();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()!;
        Console.WriteLine($"[1] 语言 {engine.RecognizerLanguage.LanguageTag}");

        var dir = args.Length > 0 ? args[0] : @"D:\played\captures\ocr_native";
        var files = Directory.GetFiles(dir, "*.png")
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        Console.WriteLine($"[2] {files.Count} 张原尺寸（1280x120）");

        await Ocr(files[0], engine); // 预热

        var sw = Stopwatch.StartNew();
        var results = new List<(string Name, string Text)>();
        foreach (var f in files)
            results.Add((Path.GetFileNameWithoutExtension(f), await Ocr(f, engine)));
        sw.Stop();
        Console.WriteLine($"[3] 原尺寸：平均 {sw.Elapsed.TotalMilliseconds / files.Count:F1} ms/张，总计 {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine();

        foreach (var (name, text) in results)
        {
            var kind = Kind(text);
            var u = text.ToUpperInvariant();
            Console.WriteLine($"  {name,-20} [{kind,-10}] {text}");
        }
        return 0;
    }

    /// <summary>
    /// 量 OCR 到底要吃多少内存 / CPU。用户的顾虑是「会不会让内存 CPU 显存占用变大」，
    /// 所以这里分开量三件事：① 引擎本身常驻多少 ② 每识别一张花多少 CPU ③ 有没有 GPU(显存)参与。
    /// </summary>
    private static async Task<int> Mem()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        long ws0 = p.WorkingSet64, priv0 = p.PrivateMemorySize64;
        var cpu0 = p.TotalProcessorTime;

        Console.WriteLine($"[0] 基线（还没建引擎） 工作集 {ws0 / 1024.0 / 1024:F1} MB，私有 {priv0 / 1024.0 / 1024:F1} MB");
        Console.WriteLine("[0] 显存：Windows OCR 是纯 CPU 的软件识别器，不分配 D3D 设备、不用 GPU");

        var sw = Stopwatch.StartNew();
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()!;
        sw.Stop();
        p.Refresh();
        long ws1 = p.WorkingSet64, priv1 = p.PrivateMemorySize64;
        Console.WriteLine($"[1] 建引擎 {sw.Elapsed.TotalMilliseconds:F0} ms → 工作集 +{(ws1 - ws0) / 1024.0 / 1024:F1} MB（{ws1 / 1024.0 / 1024:F1} MB），私有 +{(priv1 - priv0) / 1024.0 / 1024:F1} MB");
        Console.WriteLine($"[1] 语言 {engine.RecognizerLanguage.LanguageTag}，MaxImageDimension {OcrEngine.MaxImageDimension}");

        var dir = @"D:\played\captures\ocr_native";
        var files = Directory.GetFiles(dir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList();
        await Ocr(files[0], engine); // 预热：首次识别会加载语言模型，必须排除在统计外

        p.Refresh();
        long wsWarm = p.WorkingSet64, privWarm = p.PrivateMemorySize64;
        var cpuWarm = p.TotalProcessorTime;
        Console.WriteLine($"[2] 预热后 工作集 {wsWarm / 1024.0 / 1024:F1} MB（相对基线 +{(wsWarm - ws0) / 1024.0 / 1024:F1}），私有 {privWarm / 1024.0 / 1024:F1} MB（+{(privWarm - priv0) / 1024.0 / 1024:F1}）");

        sw.Restart();
        for (int i = 1; i < files.Count; i++) await Ocr(files[i], engine);
        sw.Stop();
        p.Refresh();
        var cpu = p.TotalProcessorTime - cpuWarm;
        long ws2 = p.WorkingSet64;
        int n = files.Count - 1;
        Console.WriteLine($"[3] 连续识别 {n} 张：{sw.Elapsed.TotalMilliseconds / n:F1} ms/张（墙钟），CPU {cpu.TotalMilliseconds / n:F1} ms/张");
        Console.WriteLine($"[3] 跑完后工作集 {ws2 / 1024.0 / 1024:F1} MB（相对基线 +{(ws2 - ws0) / 1024.0 / 1024:F1}，相对预热后 +{(ws2 - wsWarm) / 1024.0 / 1024:F1}）");
        Console.WriteLine($"[4] 单核占比：{cpu.TotalMilliseconds / sw.Elapsed.TotalMilliseconds * 100:F0}%（占一个核的百分比；本机 {Environment.ProcessorCount} 核 → 整机 {(cpu.TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100):F1}%）");
        Console.WriteLine();
        Console.WriteLine("[5] 换算到实际使用频率：只有 NCC 命中那一刻才需要 OCR（一局约 3-10 次），");
        Console.WriteLine($"    一次 {sw.Elapsed.TotalMilliseconds / n:F0} ms × 6 次 ≈ {sw.Elapsed.TotalMilliseconds / n * 6 / 1000:F2} 秒 CPU / 局，占一局 15 分钟的比例约 {sw.Elapsed.TotalMilliseconds / n * 6 / (15 * 60 * 1000) * 100:F3}%");
        return 0;
    }

    private static string Kind(string text)
    {
        var u = text.ToUpperInvariant();
        if (u.Contains("RE-KNOCK") || u.Contains("RE-KNO") || u.Contains("REKNOCK")) return "再次击倒";
        if (u.Contains("ASSIST") && u.Contains("ELIMIN")) return "助攻淘汰";
        if (u.Contains("ASSIST") && (u.Contains("KNOCK") || u.Contains("KNOCKE"))) return "助攻击倒";
        if (u.Contains("ELIMIN")) return "淘汰";
        if (u.Contains("KNOCK")) return "击倒";
        return "?";
    }

    private static async Task<string> Ocr(string path, OcrEngine engine)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var r = await engine.RecognizeAsync(bmp);
        return r.Text.Replace('\n', ' ').Trim();
    }
}
