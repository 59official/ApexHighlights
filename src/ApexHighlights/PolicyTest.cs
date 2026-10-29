using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApexHighlights;

/// <summary>
/// CapturePolicy 的自检：默认档、老配置迁移、写盘只动自己的字段。
///
/// 这三条都是「用户看不出错、但录出来全是错」的地方：
/// 默认档写错 = 新装的用户第一段录出来就没声音；迁移写错 = 老用户升级后音轨忽然变了；
/// 写盘写全份 = 从托盘改个音轨把阈值和配额一起抹掉。
/// </summary>
internal static class PolicyTest
{
    public static int Run()
    {
        int fail = 0;
        var dir = Path.Combine(Path.GetTempPath(), "apex-policy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // ① 全新配置：只录游戏 + 滑出提示
            var fresh = CapturePolicy.Load(Path.Combine(dir, "none.json"));
            fail += Check("新配置默认「只录游戏」", fresh.Audio == AudioMode.GameOnly);
            fail += Check("新配置默认「滑出提示」", fresh.Notice == NoticeMode.Toast);
            fail += Check("新配置有音轨", fresh.AnyAudio);

            // ② 老配置（Audio/Mic 两个布尔）折算成语义相同的档
            var legacyPath = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacyPath, """{ "Audio": true, "Mic": false, "BitrateMbps": 30 }""");
            var legacy = CapturePolicy.Load(legacyPath);
            fail += Check("老配置 (true,false) → 系统声音（不含麦克风）", legacy.Audio == AudioMode.SystemOnly);
            fail += Check("老配置其它字段还在（码率 30）", legacy.BitrateMbps == 30);

            File.WriteAllText(legacyPath, """{ "Audio": false, "Mic": true }""");
            fail += Check("老配置 (false,true) → 系统声音 + 麦克风", CapturePolicy.Load(legacyPath).Audio == AudioMode.SystemAndMic);

            File.WriteAllText(legacyPath, """{ "Audio": false, "Mic": false }""");
            fail += Check("老配置 (false,false) → 不录声音", CapturePolicy.Load(legacyPath).Audio == AudioMode.None);

            // ③ 写回：只动自己的字段，别人的不动；老的 Audio/Mic 顺手清掉
            var cfg = Path.Combine(dir, "config.json");
            File.WriteAllText(cfg, """{ "Threshold": 0.66, "QuotaGb": 55, "MergeSec": 5, "Audio": true, "Mic": false }""");
            var p = CapturePolicy.Load(cfg);
            p.Audio = AudioMode.SystemAndMic;
            p.Notice = NoticeMode.Beep;
            p.BitrateMbps = 40;
            p.Save(cfg);

            var root = JsonNode.Parse(File.ReadAllText(cfg))!.AsObject();
            fail += Check("写盘后阈值还在", root["Threshold"]!.GetValue<double>() == 0.66);
            fail += Check("写盘后配额还在", root["QuotaGb"]!.GetValue<int>() == 55);
            fail += Check("写盘后合并窗口还在", root["MergeSec"]!.GetValue<int>() == 5);
            fail += Check("写盘写入 AudioMode", root["AudioMode"]?.GetValue<string>() == "SystemAndMic");
            fail += Check("写盘写入 Notice", root["Notice"]?.GetValue<string>() == "Beep");
            fail += Check("写盘删掉老字段 Audio", root["Audio"] is null);
            fail += Check("写盘删掉老字段 Mic", root["Mic"] is null);

            var reread = CapturePolicy.Load(cfg);
            fail += Check("读回来还是系统声音 + 麦克风", reread.Audio == AudioMode.SystemAndMic);
            fail += Check("读回来提示是「只响一声」", reread.Notice == NoticeMode.Beep);

            // ④ 写坏的枚举值不能把程序带崩
            File.WriteAllText(cfg, """{ "AudioMode": "NoSuchMode", "Notice": 99 }""");
            var bad = CapturePolicy.Load(cfg);
            fail += Check("不认识的 AudioMode 退回默认", bad.Audio == AudioMode.GameOnly);
            fail += Check("不认识的 Notice 退回默认", bad.Notice == NoticeMode.Toast);

            // ⑤ 菜单上四档/三档齐全，且默认档排第一
            fail += Check("音轨四档", CapturePolicy.AudioModes.Length == 4);
            fail += Check("音轨默认档排第一", CapturePolicy.AudioModes[0] == AudioMode.GameOnly);
            fail += Check("提示三档", CapturePolicy.NoticeModes.Length == 3);
            fail += Check("提示默认档排第一", CapturePolicy.NoticeModes[0] == NoticeMode.Toast);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "[ok] 录制参数自检全过" : $"[X] 录制参数自检挂了 {fail} 条");
        return fail == 0 ? 0 : 1;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"    {(ok ? "ok" : "NG")}  {what}");
        return ok ? 0 : 1;
    }
}
