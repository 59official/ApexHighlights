namespace ApexHighlights;

/// <summary>
/// 视频编码。HEVC 同码率下更干净、体积更小，但需要系统装过「HEVC 视频扩展」
/// （或用 VLC / PotPlayer 这类自带解码的播放器）才能播；H.264 兼容性最好。
/// 默认取 <see cref="CapturePolicy.DefaultCodec"/>（当前是 HEVC，用户指定）。
/// </summary>
public enum VideoCodec
{
    H264 = 0,
    Hevc = 1,
}

public static class VideoCodecs
{
    public static readonly VideoCodec[] All = { VideoCodec.H264, VideoCodec.Hevc };

    public static string Label(VideoCodec c) => c == VideoCodec.Hevc ? "HEVC" : "H.264";

    /// <summary>给菜单/面板用的短说明（不含「需要扩展」这类提示，用户明确不要）。</summary>
    public static string Hint(VideoCodec c) => c == VideoCodec.Hevc
        ? "同码率下更干净、体积更小"
        : "兼容性最好";

    /// <summary>把配置文件里读到的值夹成合法值：不认识的（含旧版本写的数字）一律回默认。</summary>
    public static VideoCodec Normalize(VideoCodec c) =>
        System.Enum.IsDefined(typeof(VideoCodec), c) ? c : CapturePolicy.DefaultCodec;
}
