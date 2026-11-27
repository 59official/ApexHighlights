using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ApexHighlights;

[JsonConverter(typeof(JsonStringEnumConverter<PictureSource>))]
public enum PictureSource
{
    ApexConfig,
    CaptureWindow,
    Manual,
}

public sealed record PictureSettings
{
    public PictureSource Source { get; init; } = PictureSource.ApexConfig;
    public int Width { get; init; } = 2560;
    public int Height { get; init; } = 1600;
    public bool SaveNearFrames { get; init; }

    internal PictureSettings Normalize() => this with
    {
        Source = Enum.IsDefined(Source) ? Source : PictureSource.ApexConfig,
        Width = Math.Clamp(Width, PictureSize.MinWidth, PictureSize.MaxSize),
        Height = Math.Clamp(Height, PictureSize.MinHeight, PictureSize.MaxSize),
    };
}

internal readonly record struct PictureSize(int Width, int Height)
{
    internal const int MinWidth = 320;
    internal const int MinHeight = 200;
    internal const int MaxSize = 16384;

    public bool IsValid => Width is >= MinWidth and <= MaxSize && Height is >= MinHeight and <= MaxSize;
    public override string ToString() => $"{Width}×{Height}";
}

internal readonly record struct ApexVideoSize(PictureSize Size, string WidthKey, string HeightKey);

internal static class ApexVideoConfig
{
    // FOLDERID_SavedGames，使用当前进程用户的已知文件夹，兼容重定向。
    private static readonly Guid SavedGamesFolder = new("4c5c32ff-bb9d-43b0-b5b4-2d72e54eaaa4");
    private static readonly Regex Setting = new(
        "^\\s*\"(?<key>setting\\.(?:last_display_width|last_display_height|defaultres|defaultresheight))\"\\s*\"(?<value>[0-9]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string DefaultPath()
    {
        string? folder = null;
        if (OperatingSystem.IsWindows())
        {
            int result = SHGetKnownFolderPath(in SavedGamesFolder, 0, IntPtr.Zero, out IntPtr buffer);
            try { if (result >= 0) folder = Marshal.PtrToStringUni(buffer); }
            finally { Marshal.FreeCoTaskMem(buffer); }
        }

        if (string.IsNullOrEmpty(folder))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        return Path.Combine(folder, "Respawn", "Apex", "local", "videoconfig.txt");
    }

    public static ApexVideoSize? Read(string path)
    {
        try { return Parse(File.ReadLines(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    internal static ApexVideoSize? Parse(IEnumerable<string> lines)
    {
        var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            var match = Setting.Match(line);
            if (match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int value))
                sizes[match.Groups["key"].Value] = value;
        }

        ApexVideoSize? GetPair(string widthKey, string heightKey)
        {
            if (!sizes.TryGetValue("setting." + widthKey, out int width) ||
                !sizes.TryGetValue("setting." + heightKey, out int height)) return null;
            var size = new PictureSize(width, height);
            return size.IsValid ? new ApexVideoSize(size, widthKey, heightKey) : null;
        }

        return GetPair("last_display_width", "last_display_height") ?? GetPair("defaultres", "defaultresheight");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr userToken, out IntPtr path);
}

internal readonly record struct PictureLayout(PictureSize Capture, PictureSize Game, string Source, BandDef Band,
    int BandRefWidth)
{
    public string Description => $"采集窗口 {Capture}，画面参考 {Game}（{Source}），" +
        $"检测区 {Band.W}×{Band.H}@{Band.X},{Band.Y}";

    public static PictureLayout Resolve(GameProfile profile, PictureSize capture, PictureSettings settings,
        ApexVideoSize? autoSize)
    {
        var (game, source) = settings.Source switch
        {
            PictureSource.Manual => (new PictureSize(settings.Width, settings.Height), "手动"),
            PictureSource.ApexConfig when autoSize is { } saved => (saved.Size, saved.WidthKey + "/" + saved.HeightKey),
            _ => (capture, "采集窗口"),
        };
        if (!string.Equals(profile.Id, "apex", StringComparison.OrdinalIgnoreCase))
            return new PictureLayout(capture, capture, "采集窗口", profile.Band, profile.RefWidth);

        var area = profile.Band;
        bool defaultBand = profile.RefWidth == 2560 && profile.RefHeight == 1440 &&
            area.X == 640 && area.Y == 1000 && area.W == 1280 && area.H == 120;
        double topMargin = defaultBand ? 40 : 0;
        double left = area.X, top = area.Y - topMargin;
        double right = (double)area.X + area.W, bottom = (double)area.Y + area.H;
        double referenceAspect = (double)profile.RefWidth / profile.RefHeight;
        double captureAspect = (double)capture.Width / capture.Height;
        double gameAspect = game.IsValid ? (double)game.Width / game.Height : captureAspect;

        if (gameAspect != captureAspect)
        {
            double ratio = gameAspect / captureAspect;
            // 比例提示无法确定 GPU 的拉伸方式。保留原框，覆盖居中适配与居中裁切两种位置。
            Expand(Math.Min(1, ratio), Math.Min(1, 1 / ratio));
            Expand(Math.Max(1, ratio), Math.Max(1, 1 / ratio));
        }

        // 4:3 等较窄画面给居中的横幅留出横向空间，并兼顾上下偏移。
        const double narrowRatio = 0.8;
        double narrowestRatio = Math.Min(captureAspect, gameAspect) / referenceAspect;
        if (defaultBand && narrowestRatio <= narrowRatio)
        {
            Expand(1 / narrowestRatio, narrowestRatio);
            Expand(1, 1 / narrowestRatio);
        }

        void Expand(double scaleX, double scaleY)
        {
            double cx = profile.RefWidth / 2d, cy = profile.RefHeight / 2d;
            left = Math.Min(left, cx + (area.X - cx) * scaleX);
            top = Math.Min(top, cy + (area.Y - cy) * scaleY - topMargin);
            right = Math.Max(right, cx + (area.X + area.W - cx) * scaleX);
            bottom = Math.Max(bottom, cy + (area.Y + area.H - cy) * scaleY);
        }

        // 采到原生窄比例时，横向也按帧高归一化，避免把 4:3 字形撑成 16:9。
        int bandRefWidth = defaultBand && captureAspect / referenceAspect <= narrowRatio
            ? Math.Max(1, (int)Math.Round(captureAspect * profile.RefHeight))
            : profile.RefWidth;
        double widthScale = (double)bandRefWidth / profile.RefWidth;
        int x = (int)Math.Clamp(Math.Floor(left * widthScale), 0, bandRefWidth - 1);
        int y = (int)Math.Clamp(Math.Floor(top), 0, profile.RefHeight - 1);
        var band = new BandDef
        {
            X = x,
            Y = y,
            W = (int)Math.Clamp(Math.Ceiling(right * widthScale), x + 1, bandRefWidth) - x,
            H = (int)Math.Clamp(Math.Ceiling(bottom), y + 1, profile.RefHeight) - y,
        };
        return new PictureLayout(capture, game, source, band, bandRefWidth);
    }
}
