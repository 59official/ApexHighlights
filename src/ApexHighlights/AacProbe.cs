using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace ApexHighlights;

/// <summary>探测：这台机器上有没有可用的 AAC 编码器 MFT，它的输入/输出类型长什么样。</summary>
internal static class AacProbe
{
    public static int Run()
    {
        MediaFactory.MFStartup(false);

        var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };

        MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, 0x7, inInfo, outInfo, out var ptrs, out var count);
        Console.WriteLine($"[1] AudioEncoder(PCM→AAC) 数量 {count}");
        var arr = new IntPtr[count];
        if (count > 0) Marshal.Copy(ptrs, arr, 0, (int)count);

        for (int i = 0; i < arr.Length; i++)
        {
            var act = new IMFActivate(arr[i]);
            string name = "?";
            try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "?"; } catch { }
            Console.WriteLine($"    [{i}] {name}");
            try
            {
                var t = act.ActivateObject<IMFTransform>();
                DumpTypes(t, "      ");
                t.Dispose();
            }
            catch (Exception ex) { Console.WriteLine($"      activate 失败: {ex.Message}"); }
            act.Dispose();
        }

        // 不限输入子类型，看看还有别的什么
        MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, 0x7, null, null, out var p2, out var c2);
        Console.WriteLine($"[2] 所有 AudioEncoder {c2}");
        var a2 = new IntPtr[c2];
        if (c2 > 0) Marshal.Copy(p2, a2, 0, (int)c2);
        for (int i = 0; i < a2.Length; i++)
        {
            var act = new IMFActivate(a2[i]);
            string name = "?";
            try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "?"; } catch { }
            Console.WriteLine($"    [{i}] {name}");
            act.Dispose();
        }
        return count > 0 ? 0 : 6;
    }

    private static void DumpTypes(IMFTransform t, string pad)
    {
        for (int i = 0; i < 24; i++)
        {
            IMFMediaType? mt = null;
            try { mt = t.GetOutputAvailableType(0, i); } catch { break; }
            if (mt is null) break;
            try
            {
                var sub = mt.GetGUID(MediaTypeAttributeKeys.Subtype);
                string extra = "";
                try { extra += $" rate={mt.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond)}"; } catch { }
                try { extra += $" ch={mt.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels)}"; } catch { }
                try { extra += $" bits={mt.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample)}"; } catch { }
                try { extra += $" avg={mt.GetUInt32(MediaTypeAttributeKeys.AvgBitrate)}"; } catch { }
                try
                {
                    var ud = mt.GetBlob(MediaTypeAttributeKeys.UserData);
                    extra += $" userdata={ud.Length}B [{string.Join(" ", ud.Take(8).Select(b => b.ToString("X2")))}]";
                }
                catch { }
                Console.WriteLine($"{pad}out[{i}] {sub}{extra}");
            }
            finally { mt.Dispose(); }
        }
    }
}
