using System.Text;

namespace ApexHighlights;

internal static class SpectatorHud
{
    public const int Width = 1280;
    public const int Height = 120;

    /// <summary>Only called on text from the top HUD, away from the event banner and player names.</summary>
    internal static bool HasSpectatorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var compact = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (char.IsLetterOrDigit(ch)) compact.Append(char.ToUpperInvariant(ch));
        string words = compact.ToString()
            .Replace("實況", "实况").Replace("觀戰", "观战").Replace("總結", "总结")
            .Replace("恢復", "恢复").Replace("復活", "复活");

        if (words.Contains("等待恢复") || words.Contains("等待复活") || words.Contains("等待重生") ||
            words.Contains("WAITINGFORRESPAWN") || words.Contains("WAITINGTORESPAWN") ||
            words.Contains("AWAITINGRESPAWN") || words.Contains("WAITINGFORRECOVERY")) return true;

        bool ranked = words.Contains("排位") || words.Contains("RANKED");
        bool live = words.Contains("实况") || words.Contains("SPECTATE");
        bool watch = words.Contains("观战") || words.Contains("SPECTATING");
        bool summary = words.Contains("总结") || words.Contains("SUMMARY") || words.Contains("DEATHRECAP");

        // 两个相邻导航标签也能认出死亡后的页签，兼容倒计时不在和 OCR 漏读单个标签。
        return (ranked && summary) || ((live || watch) && (ranked || summary)) || (live && watch);
    }
}
