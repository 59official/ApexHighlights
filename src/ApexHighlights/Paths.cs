namespace ApexHighlights;

/// <summary>
/// 所有可写路径的唯一来源。
///
/// 不能靠“当前目录”：双击 exe 时当前目录是 exe 所在目录，但从快捷方式、
/// 从 GitHub Releases 解压到别处、或者从别的程序拉起时它会变成任意目录。
/// 也不能把开发机的绝对路径写死——那是这个项目曾经的 14 处硬编码，
/// 换一台机器（或者别人下载回去）就会往不存在的 D 盘写。
///
/// 规则（从上到下第一个命中的胜出）：
///   1. 命令行 --data=&lt;dir&gt;
///   2. exe 同目录（可写时）—— 便携式，U 盘/解压即用，删目录=清空
///   3. %LOCALAPPDATA%\ApexHighlights —— exe 装在 Program Files 之类只读位置时
/// </summary>
internal static class Paths
{
    private static string? _dataRoot;

    /// <summary>exe 所在目录（发布版里就是解压目录）。</summary>
    public static string ExeDir { get; } = AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>数据根目录。第一次访问时确定，之后固定（进程内不变）。</summary>
    public static string DataRoot => _dataRoot ??= PickDataRoot();

    /// <summary>录制产物根目录。</summary>
    public static string Captures => Path.Combine(DataRoot, "captures");

    /// <summary>成片目录（回顾库看的就是这里）。</summary>
    public static string Clips => Path.Combine(Captures, "clips");

    /// <summary>环形分片临时目录。</summary>
    public static string Ring => Path.Combine(Captures, "ring");

    /// <summary>游戏配置目录。</summary>
    public static string Games => Path.Combine(DataRoot, "games");

    /// <summary>配额配置。</summary>
    public static string ConfigFile => Path.Combine(DataRoot, "config.json");

    /// <summary>命令行显式指定数据目录（--data=），必须在其它路径被访问前调用。</summary>
    public static void Override(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        _dataRoot = Path.GetFullPath(dir);
    }

    private static string PickDataRoot()
    {
        // 从源码树里跑（exe 在 .../bin/Release/net8.0-.../）就用源码树根，
        // 这样开发时 captures/ 仍然落在仓库里，不会因为换了构建配置就看不到之前的片子。
        // 判据是路径里的 "bin" 段：发布出来的 exe 不在 bin 下，不会误判成开发树。
        if (IsUnderBin(ExeDir))
        {
            for (var d = new DirectoryInfo(ExeDir); d is not null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) ||
                    File.Exists(Path.Combine(d.FullName, "games", "apex.json")))
                {
                    return d.FullName;
                }
            }
        }

        if (CanWrite(ExeDir)) return ExeDir;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ApexHighlights");
        try { Directory.CreateDirectory(local); } catch { /* 实在不行就用临时目录兜底 */ }
        return CanWrite(local) ? local : Path.GetTempPath().TrimEnd('\\', '/');
    }

    private static bool IsUnderBin(string dir)
    {
        var parts = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            if (string.Equals(parts[i], "bin", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>真的写一个文件再删掉，别只信 ACL 查询（UAC 虚拟化会骗人）。</summary>
    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_test");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把内嵌的游戏配置释放到数据目录（存在就不覆盖，用户改过的配置不能丢）。
    /// 返回释放/已存在的配置根目录，供 GameProfile 搜索。
    /// </summary>
    public static void EnsureGames()
    {
        try
        {
            var asm = typeof(Paths).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("games/", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = name["games/".Length..];
                var dst = Path.Combine(Games, rel);
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using var src = asm.GetManifestResourceStream(name);
                if (src is null) continue;
                using var outFile = File.Create(dst);
                src.CopyTo(outFile);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] 释放内嵌游戏配置失败: " + ex.Message);
        }
    }
}
