using Vortice.MediaFoundation;

namespace ApexHighlights;

/// <summary>用系统解码链路读一遍 mp4，验证我们写出来的文件确实能被 MF 解析。</summary>
internal static class Mp4Check
{
    public static int Run(string path)
    {
        MediaFactory.MFStartup(false);
        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);

        var v = Read(reader, (int)SourceReaderIndex.FirstVideoStream, "视频");
        var a = Read(reader, (int)SourceReaderIndex.FirstAudioStream, "音频");
        reader.Dispose();

        if (v.Samples == 0) return 4;
        if (!a.Present) { Console.WriteLine("[!] 没有音轨"); return 0; }
        if (a.Samples == 0) { Console.WriteLine("[!] 有音轨但没有样本"); return 7; }
        return 0;
    }

    private readonly record struct Info(bool Present, int Samples, long LastTs, long Bytes);

    private static Info Read(IMFSourceReader reader, int stream, string label)
    {
        bool present;
        try { present = reader.GetStreamSelection(stream); }
        catch { present = false; }

        // 有些读取器对不存在的光标返回 false 而不是抛异常，所以还得试着读一次
        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        try { reader.SetStreamSelection(stream, true); }
        catch { return new Info(false, 0, -1, 0); }

        int n = 0;
        long lastTs = -1, bytes = 0;
        try
        {
            while (true)
            {
                var sample = reader.ReadSample(stream, SourceReaderControlFlag.None, out _, out var flags, out var ts);
                if (flags.HasFlag(SourceReaderFlag.EndOfStream)) { sample?.Dispose(); break; }
                if (sample is null) continue;
                n++;
                lastTs = ts;
                try { bytes += sample.TotalLength; } catch { }
                sample.Dispose();
            }
        }
        catch (Exception e) { Console.WriteLine($"[{label}] 读取失败: {e.Message}"); return new Info(present, 0, -1, 0); }

        IMFMediaType? mt = null;
        try
        {
            mt = reader.GetCurrentMediaType(stream);
            var sub = mt.GetGUID(MediaTypeAttributeKeys.Subtype);
            string extra = "";
            try { extra += $" 采样率={mt.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond)}"; } catch { }
            try { extra += $" 声道={mt.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels)}"; } catch { }
            try { extra += $" 尺寸={mt.GetUInt64(MediaTypeAttributeKeys.FrameSize)}"; } catch { }
            Console.WriteLine($"[{label}] 样本 {n}，末时间戳 {lastTs}（{lastTs / 1e7:F2}s），字节 {bytes}，类型 {sub}{extra}");
        }
        catch (Exception e) { Console.WriteLine($"[{label}] 样本 {n}，读类型失败 {e.Message}"); }
        finally { mt?.Dispose(); }

        return new Info(present || n > 0, n, lastTs, bytes);
    }
}
