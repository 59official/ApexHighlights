using System.Text;

namespace ApexHighlights;

/// <summary>
/// 托盘模式的日志出口。
///
/// 为什么需要它：托盘模式启动后会把控制台藏起来（<see cref="Native.HideConsole"/>），
/// stdout 谁都看不见。而第一次真实对局「一片都没录到」时，正因为什么都没落盘，
/// 事后完全查不出到底是没启动、没找到窗口、还是找到了没命中。
/// 所以托盘模式必须自己写文件。
///
/// 用追加而不是覆盖：托盘是常驻的，一次开一整天，出事要能往前翻。
/// 每次启动写一行分隔头，方便定位是哪一次运行。
///
/// 全程加锁：会调它的线程有采集（WGC 回调）、编码、clip、音频补齐、UI 好几个，
/// StreamWriter 本身不保证并发安全。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _console = true;

    /// <summary>正在写的日志文件，没开就是 null。</summary>
    public static string? Path { get; private set; }

    /// <summary>
    /// 打开日志文件。path 为 null 时只往控制台写。
    /// 打不开就退回只写控制台 —— 日志不该成为启动失败的理由。
    /// </summary>
    public static void Init(string? path, bool console = true)
    {
        lock (Gate)
        {
            _console = console;
            Close_NoLock();
            if (path is null) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                // append: true —— 一次会话追加一段，历史留着
                _writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                Path = path;
                _writer.WriteLine();
                _writer.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动 =====");
            }
            catch (Exception ex)
            {
                Path = null;
                if (_console) Console.WriteLine($"[!] 日志开不了（{path}）: {ex.Message}");
            }
        }
    }

    /// <summary>写一行。带时间戳，同时进控制台和文件（之一或都写）。</summary>
    public static void Write(string s)
    {
        lock (Gate)
        {
            if (_console) Console.WriteLine(s);
            try { _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
            catch { /* 磁盘满/被删也不该拖垮录制 */ }
        }
    }

    /// <summary>写一行，但只进文件，不进控制台（高频刷屏的东西用这个）。</summary>
    public static void FileOnly(string s)
    {
        lock (Gate)
        {
            try { _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
            catch { }
        }
    }

    public static void Close()
    {
        lock (Gate) Close_NoLock();
    }

    private static void Close_NoLock()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
        Path = null;
    }
}
