using System.Globalization;
using System.Text.RegularExpressions;

namespace ApexHighlights;

internal static class CaptureDiagnostics
{
    public const int RetentionDays = 7;
    private const string Folder = "diagnostics";
    private static readonly Regex SessionPattern = new(@"\A[0-9]{8}-[0-9]{9}-[a-f0-9]{8}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ImagePattern = new(@"\A(?:hit|near)_[0-9]+_(?:[0-9]{3}_)?[0-9]+\.[0-9]{2}\.bmp\z", RegexOptions.CultureInvariant);

    public static string NewSessionId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    public static string SessionDir(string captures, string session) => Path.Combine(captures, Folder, session);

    public static string ImageName(string kind, long ms, float score) =>
        FormattableString.Invariant($"{kind}_{ms / 1000:00000}_{ms % 1000:000}_{score:F2}.bmp");

    public static bool IsManagedImage(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        return dir is not null && SessionPattern.IsMatch(Path.GetFileName(dir)) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), Folder, StringComparison.OrdinalIgnoreCase) &&
            ImagePattern.IsMatch(Path.GetFileName(path));
    }

    public static IEnumerable<string> Files(string captures, string searchPattern)
    {
        if (!Directory.Exists(captures)) yield break;
        foreach (var path in Directory.EnumerateFiles(captures, searchPattern)) yield return path;
        foreach (var dir in SessionDirectories(captures))
            foreach (var path in Directory.EnumerateFiles(dir, searchPattern))
                if (IsManagedImage(path)) yield return path;
    }

    public static void Prune(string captures, DateTime utcNow)
    {
        var cutoff = utcNow.AddDays(-RetentionDays);
        foreach (var dir in SessionDirectories(captures))
        {
            foreach (var image in Directory.EnumerateFiles(dir, "*.bmp"))
                if (IsManagedImage(image) && File.GetLastWriteTimeUtc(image) < cutoff)
                    File.Delete(image);
            if (Directory.GetCreationTimeUtc(dir) < cutoff && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    private static IEnumerable<string> SessionDirectories(string captures)
    {
        var root = new DirectoryInfo(Path.Combine(captures, Folder));
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield break;
        foreach (var dir in root.EnumerateDirectories())
            if (SessionPattern.IsMatch(dir.Name) && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                yield return dir.FullName;
    }
}
