using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace ApexHighlights;

/// <summary>
/// 把 mp4 里的音轨解码成 PCM 再量一遍电平。
/// 双轨存在不等于有声音——之前那次 ASC 写错也能读出“音频样本 149”，
/// 所以必须真解码出 PCM 看有没有波形。
/// </summary>
internal static class AudioCheck
{
    public static int Run(string path)
    {
        MediaFactory.MFStartup(false);
        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);

        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true);

        // 让 source reader 把 AAC 解成 PCM，这样读出来的就是波形
        IMFMediaType? pcmOut = null;
        try
        {
            pcmOut = MediaFactory.MFCreateMediaType();
            pcmOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            pcmOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            pcmOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u);
            pcmOut.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            pcmOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            pcmOut.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
            reader.SetCurrentMediaType((int)SourceReaderIndex.FirstAudioStream, pcmOut);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[!] 设置 PCM 输出失败: {e.Message}");
            pcmOut?.Dispose();
            reader.Dispose();
            return 6;
        }
        finally { pcmOut?.Dispose(); }

        long samples = 0, bytes = 0;
        int peak = 0;
        double sumSq = 0;
        long nSamples = 0;
        long lastTs = -1;
        int nonzero = 0;

        while (true)
        {
            var s = reader.ReadSample((int)SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
                out _, out var flags, out var ts);
            if (flags.HasFlag(SourceReaderFlag.EndOfStream)) { s?.Dispose(); break; }
            if (s is null) continue;
            samples++;
            lastTs = ts;
            try
            {
                var buf = s.ConvertToContiguousBuffer();
                buf.Lock(out IntPtr p, out _, out int cur);
                bytes += cur;
                unsafe
                {
                    short* sp = (short*)p;
                    int n = cur / 2;
                    for (int i = 0; i < n; i++)
                    {
                        int v = sp[i];
                        int a = v < 0 ? -v : v;
                        if (a > peak) peak = a;
                        if (a > 150) nonzero++;
                        sumSq += (double)v * v;
                        nSamples++;
                    }
                }
                buf.Unlock();
                buf.Dispose();
            }
            catch (Exception e) { Console.WriteLine($"[!] 取样失败: {e.Message}"); }
            s.Dispose();
        }
        reader.Dispose();

        double rms = nSamples > 0 ? Math.Sqrt(sumSq / nSamples) : 0;
        double peakDb = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -999;
        double rmsDb = rms > 0 ? 20 * Math.Log10(rms / 32768.0) : -999;
        double secs = nSamples / 2.0 / 48000.0;

        Console.WriteLine($"解码样本 {samples}，时长 {secs:F2}s，PCM {bytes} 字节");
        Console.WriteLine($"峰值 {peak}（{peakDb:F1} dBFS）  RMS {rms:F0}（{rmsDb:F1} dBFS）  有声样点数 {nonzero}");
        if (lastTs < 0) lastTs = 0;
        Console.WriteLine($"末时间戳 {lastTs}（{lastTs / 1e7:F2}s）");

        if (peak < 500) { Console.WriteLine("[X] 音轨基本是静音"); return 8; }
        Console.WriteLine("[ok] 音轨有真实波形");
        return 0;
    }
}
