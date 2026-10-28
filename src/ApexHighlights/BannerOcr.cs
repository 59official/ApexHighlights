using System.Diagnostics;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace ApexHighlights;

/// <summary>Apex 屏幕中部的横幅种类，由 OCR 读出的文案判定。</summary>
public enum BannerKind
{
    /// <summary>没读出来 / 不像横幅</summary>
    Unknown = 0,
    /// <summary>KNOCKED DOWN &lt;名字&gt; —— 我击倒别人</summary>
    Knock,
    /// <summary>RE-KNOCKED &lt;名字&gt; —— 同一目标被再次击倒</summary>
    ReKnock,
    /// <summary>ASSIST, KNOCKED DOWN &lt;名字&gt; —— 我助攻队友击倒</summary>
    AssistKnock,
    /// <summary>ENEMY SQUAD ELIMINATED —— 敌方小队全灭</summary>
    Elim,
    /// <summary>ASSIST, ELIMINATION —— 我助攻淘汰</summary>
    AssistElim,
}

/// <summary>
/// 用 Windows 自带的 OCR 读横幅文案，把 NCC 只能回答「像不像」的问题升级成
/// 「这是哪一种」。
///
/// 为什么必须要有它（2026-10-28 实测结论）：NCC 只能给一个相似度，而 Apex 的
/// 五种横幅用的是同一套字体、同一字号、同一行高，NCC 分布严重重叠 —— 实测
/// <c>RE-KNOCKED 白奈雪子</c> 的得分是 0.61/0.63，比最低的真击倒 0.694 还低，
/// 任何阈值都分不开它们。只有读出文字才分得清。
///
/// 代价（本机实测，1280x120 条带）：21 ms/次、私有内存 +13 MB、**零显存**。
/// 而且只在 NCC 已命中后跑，一局 3–10 次 ≈ 0.13 秒 CPU，对录制毫无影响。
///
/// 线程模型：调用方必须是同一个后台线程（本程序是 "clip" 线程）。OcrEngine
/// 不保证线程安全，所以这里不加锁，靠调用点保证串行。
/// </summary>
public sealed class BannerOcr
{
    private OcrEngine? _engine;
    private string _err = "";

    /// <summary>识别次数（用于 Stats 诊断）。</summary>
    public int Count { get; private set; }
    /// <summary>累计识别耗时（毫秒）。</summary>
    public long TotalMs { get; private set; }
    /// <summary>引擎语言标签，比如 zh-Hans-CN。</summary>
    public string EngineName { get; private set; } = "未初始化";
    public string Err => _err;

    /// <summary>读一张条带图并判定横幅种类。任何异常都吞掉并返回 Unknown（检测不该拖垮录制）。</summary>
    public (BannerKind Kind, string Text) Classify(string bmpPath)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (_engine is null)
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine is null)
                {
                    if (_err.Length == 0) _err = "系统没有可用的 OCR 语言包";
                    return (BannerKind.Unknown, "");
                }
                EngineName = _engine.RecognizerLanguage.LanguageTag;
            }

            var text = Recognize(bmpPath);
            Count++;
            TotalMs += sw.ElapsedMilliseconds;
            return (Parse(text), text);
        }
        catch (Exception ex)
        {
            if (_err.Length == 0) _err = $"{ex.GetType().Name}: {ex.Message}";
            return (BannerKind.Unknown, "");
        }
    }

    private string Recognize(string path)
    {
        // 同步等异步：调用方在普通后台线程上，没有同步上下文，不会死锁。
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        using var bmp = decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();
        var r = _engine!.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
        return r.Text.Replace('\n', ' ').Trim();
    }

    /// <summary>
    /// 只留 A-Z 再做关键词匹配。OCR 会把 <c>KNOCKED DOWN</c> 读成
    /// <c>KNOCKED DQWN</c> / <c>(NOCKED DOWN</c> / <c>KNn KED DOWN</c> /
    /// 甚至只剩 <c>DOWN</c>，标点和空格的位置完全不可靠，所以先归一化。
    /// 玩家名和数字反而经常读错（中英混杂、数字误读），因此判种类只看关键词，
    /// 绝不用名字做判断依据。
    /// </summary>
    public static BannerKind Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return BannerKind.Unknown;

        var n = new StringBuilder(text.Length);
        foreach (var ch in text.ToUpperInvariant())
            if (ch is >= 'A' and <= 'Z') n.Append(ch);
        var u = n.ToString();
        if (u.Length == 0) return BannerKind.Unknown;

        // 顺序有意义：RE-KNOCKED 也含 KNOCK，必须抢先判定。
        if (u.Contains("REKNOCK") || u.Contains("REKNO")) return BannerKind.ReKnock;

        // 助攻：OCR 经常把开头的 A 和 S 吞掉，实测出现过 SSIST, / rIST, / \SSIST,
        // 所以只匹配尾段 SIST / RIST（ASSIST 的后半），不能用 ASSIST 全词。
        bool assist = u.Contains("SIST") || u.Contains("RIST");
        if (assist && u.Contains("ELIMIN")) return BannerKind.AssistElim;
        if (assist && (u.Contains("KNOCK") || u.Contains("NOCK") || u.Contains("DOWN")))
            return BannerKind.AssistKnock;

        if (u.Contains("ELIMIN")) return BannerKind.Elim;
        if (u.Contains("KNOCK") || u.Contains("NOCK")) return BannerKind.Knock;
        // 兜底：OCR 把 KNOCKED 吃掉只剩 DOWN 的情况（实测 hit_00295 就是
        // "DOWN 心碎小丁 13S DAMAGED"）。带 DOWN 又没别的关键词，基本就是击倒横幅。
        if (u.Contains("DOWN")) return BannerKind.Knock;
        return BannerKind.Unknown;
    }

    /// <summary>给人看的中文名。</summary>
    public static string Label(BannerKind k) => k switch
    {
        BannerKind.Knock => "击倒",
        BannerKind.ReKnock => "再次击倒",
        BannerKind.AssistKnock => "助攻击倒",
        BannerKind.Elim => "小队淘汰",
        BannerKind.AssistElim => "助攻淘汰",
        _ => "未识别",
    };
}
