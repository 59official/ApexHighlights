namespace ApexHighlights;

/// <summary>
/// 横幅分类的多语言自检：拿真机上抄下来的横幅原文，确认简中/繁中/英文都能分出种类。
///
/// 为什么必须有：游戏语言是用户自己切的，切了以后横幅文字整段换掉，
/// 而分类错了不会崩、也不会报错——只会把「助攻击倒」写成「击倒」，用户在库里根本看不出来。
/// 这里的样本来自真实截图（2026-10-29 繁中局、简中局各一张）。
/// </summary>
internal static class OcrParseTest
{
    private static int _fail;

    public static int Run()
    {
        _fail = 0;

        // ① 繁中局（用户 2026-10-29 的截图）：OCR 原样输出里混着名字、数字、标点
        Case("繁中·击倒", "擊倒 一點不舒服 + 50 造成 25 點傷害", BannerKind.Knock);
        Case("繁中·击倒（OCR 把擊认成擎）", "擎倒 一點不舒服", BannerKind.Knock);
        Case("繁中·再次击倒", "再次擊倒 一點不舒服", BannerKind.ReKnock);
        Case("繁中·助攻击倒", "助攻 擊倒 一點不舒服", BannerKind.AssistKnock);
        Case("繁中·小队淘汰", "小隊淘汰", BannerKind.Elim);
        Case("繁中·助攻淘汰", "助攻淘汰", BannerKind.AssistElim);

        // ② 简中局（用户 2026-10-29 的截图）
        Case("简中·击倒", "击倒 cow + 50 造成 24 点伤害", BannerKind.Knock);
        Case("简中·再次击倒", "再次击倒 cow", BannerKind.ReKnock);
        Case("简中·助攻击倒", "助攻击倒 cow", BannerKind.AssistKnock);
        Case("简中·小队淘汰", "小队淘汰", BannerKind.Elim);
        Case("简中·助攻淘汰", "助攻淘汰", BannerKind.AssistElim);

        // ③ 英文局：沿用原来的关键词，别被中文那套带坏
        Case("英文·击倒", "KNOCKED DOWN Zephyr", BannerKind.Knock);
        Case("英文·再次击倒", "RE-KNOCKED Zephyr", BannerKind.ReKnock);
        Case("英文·助攻击倒", "ASSIST, KNOCKED DOWN Zephyr", BannerKind.AssistKnock);
        Case("英文·小队淘汰", "ENEMY SQUAD ELIMINATED", BannerKind.Elim);
        Case("英文·助攻淘汰", "ASSIST, ELIMINATION", BannerKind.AssistElim);

        // ④ 只有名字/伤害、没有动词的条带不能误判
        Case("噪声·伤害行", "造成 25 點傷害", BannerKind.Unknown);
        Case("噪声·队友名", "一点不舒服", BannerKind.Unknown);
        Case("噪声·空", "", BannerKind.Unknown);

        Console.WriteLine(_fail == 0 ? "[ok] 横幅分类三语自检全过" : $"[X] 横幅分类自检 {_fail} 条不过");
        return _fail == 0 ? 0 : 1;
    }

    private static void Case(string what, string text, BannerKind want)
    {
        var got = BannerOcr.Parse(text);
        if (got == want)
        {
            Console.WriteLine($"    ok  {what,-28} → {BannerOcr.Label(got)}");
            return;
        }
        Console.WriteLine($"    NG  {what,-28} → {BannerOcr.Label(got)}，应为 {BannerOcr.Label(want)}「{text}」");
        _fail++;
    }
}
