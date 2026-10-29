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

    /// <summary>
    /// 日志文件打开之前写的那几行先攒着。
    ///
    /// 为什么需要：启动最前面那几步（释放 games/、合并新模板、读配置）比日志初始化还早，
    /// 那时候 <see cref="Write"/> 没有文件可写，消息就静默蒸发了 —— 用户升级后问
    /// 「日志里怎么没有『补进 2 个新模板』那行」，其实合并做了，只是这句被丢在半路上。
    /// 现在先存下，Init 之后补写进去（容量设个上限，免得异常情况下无限攒）。
    /// </summary>
    private static readonly List<string> Pending = new();
    private const int PendingMax = 200;

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
                // BOM: 加。文件是给人看的（记事本/写字板/VS Code），
                // 不带 BOM 时这些老工具会按 GBK 解码，中文全变乱码。
                _writer = new StreamWriter(path, append: true, new UTF8Encoding(true)) { AutoFlush = true };
                Path = path;
                _writer.WriteLine();
                _writer.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动 =====");
                // 初始化之前攒下的那几行（释放 games/、合并模板…）补写进来，
                // 否则用户升级后在日志里看不到「补进 N 个新模板」，会以为没生效。
                if (Pending.Count > 0)
                {
                    foreach (var line in Pending) _writer.WriteLine($"        {line}");
                    Pending.Clear();
                }
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
            if (_writer is null)
            {
                // 还没 Init：攒下来等 Init 补写，别让启动早期的消息凭空消失
                if (Pending.Count < PendingMax) Pending.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
                return;
            }
            try { _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
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
