using Vortice.MediaFoundation;

namespace ApexHighlights;

/// <summary>用系统解码链路读一遍 mp4，验证我们写出来的文件确实能被 MF 解析。</summary>
internal static class Mp4Check
{
    public static int Run(string path)
    {
        MediaFactory.MFStartup(false);

        var v = Read(path, (int)SourceReaderIndex.FirstVideoStream, "视频");
        var a = Read(path, (int)SourceReaderIndex.FirstAudioStream, "音频");

        if (v.Samples == 0) return 4;
        if (a.Samples == 0)
        {
            Console.WriteLine("[!] 没有音频样本（要么没录音轨，要么音轨写坏了）");
            return 7;
        }
        return 0;
    }

    private readonly record struct Info(int Samples, long LastTs, long Bytes);

    /// <summary>
    /// 每条流各开一个读取器。
    ///
    /// 不能共用一个：把视频流读到 EndOfStream 之后，再切到音频流读，
    /// 读取器会直接回 EOS（它已经处于结束状态），音频就报 0 样本。
    /// 这个坑很坑人——文件其实好好的，`--audio-check` 同一片能解出满波形。
    /// </summary>
    private static Info Read(string path, int stream, string label)
    {
        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        try
        {
            // 只选这一条流；不存在的流这里会失败
            reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
            try { reader.SetStreamSelection(stream, true); }
            catch
            {
                Console.WriteLine($"[{label}] 这个流不存在");
                return new Info(0, -1, 0);
            }

            int n = 0;
            long lastTs = -1, bytes = 0;
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

            IMFMediaType? mt = null;
            try
            {
                mt = reader.GetCurrentMediaType(stream);
                var sub = mt.GetGUID(MediaTypeAttributeKeys.Subtype);
                string extra = "";
                try { extra += $" 采样率={mt.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond)}"; } catch { }
                try { extra += $" 声道={mt.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels)}"; } catch { }
                // 尺寸只在视频轨有意义；音频轨上这个 key 不存在，读出来是垃圾值
                if (label == "视频")
                    try
                    {
                        // MF_MT_FRAME_SIZE 是打包的 UInt64：(宽 << 32) | 高
                        var fs = mt.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                        extra += $" 尺寸={fs >> 32}x{fs & 0xFFFF}";
                    }
                    catch { }
                Console.WriteLine($"[{label}] 样本 {n}，末时间戳 {lastTs}（{lastTs / 1e7:F2}s），字节 {bytes}，类型 {sub}{extra}");
            }
            catch (Exception e) { Console.WriteLine($"[{label}] 样本 {n}，读类型失败 {e.Message}"); }
            finally { mt?.Dispose(); }

            return new Info(n, lastTs, bytes);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{label}] 读取失败: {e.Message}");
            return new Info(0, -1, 0);
        }
        finally { reader.Dispose(); }
    }
}
