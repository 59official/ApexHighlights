namespace ApexHighlights;

/// <summary>
/// 回顾库的筛选条件。三件事：种类、时间下界、文件名片段。
/// 做成一个值而不是三个参数，是因为它要跨模块传（窗口 → 查询 → 自检）。
/// </summary>
internal readonly record struct ClipFilter(ClipKind? Kind, DateTime Since, string Text)
{
    public bool IsEmpty => Kind is null && Since == DateTime.MinValue && Text.Length == 0;
}

/// <summary>
/// 库筛选：下拉索引 → 条件 → 结果。
///
/// 以前这套判定写死在 LibraryForm.ApplyFilter 里，UiTest 为了自检又抄了一遍，
/// 注释还写着「改一处要同时改」—— 那就说明位置放错了。现在窗口只负责把下拉
/// 索引交出来，判定和 UiTest 共用同一条代码路径。
/// </summary>
internal static class ClipQuery
{
    /// <summary>「种类」下拉的索引 → 种类。0 是全部，其余按 <see cref="ClipKinds.All"/> 的顺序。</summary>
    public static ClipKind? KindByIndex(int index)
        => index >= 1 && index <= ClipKinds.All.Length ? ClipKinds.All[index - 1] : null;

    /// <summary>「时间」下拉的索引 → 下界。</summary>
    public static DateTime SinceByIndex(int index, DateTime now) => index switch
    {
        1 => now.Date,
        2 => now.AddDays(-7),
        3 => now.AddDays(-30),
        _ => DateTime.MinValue,
    };

    public static ClipFilter FromUi(int kindIndex, int timeIndex, string? text, DateTime now)
        => new(KindByIndex(kindIndex), SinceByIndex(timeIndex, now), (text ?? "").Trim());

    public static List<ClipInfo> Apply(IEnumerable<ClipInfo> all, ClipFilter f)
    {
        IEnumerable<ClipInfo> q = all;
        if (f.Kind is not null) q = q.Where(c => c.Kind == f.Kind.Value);
        if (f.Since > DateTime.MinValue) q = q.Where(c => c.CreatedLocal >= f.Since);
        if (f.Text.Length > 0) q = q.Where(c => c.Name.Contains(f.Text, StringComparison.OrdinalIgnoreCase));
        return q.ToList();
    }
}
