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
        var r = Decode(path, out var profile);
        if (r < 0) return 6;

        Console.WriteLine($"解码样本 {_samples}，时长 {_secs:F2}s，PCM {_bytes} 字节");
        Console.WriteLine($"峰值 {_peak}（{_peakDb:F1} dBFS）  RMS {_rms:F0}（{_rmsDb:F1} dBFS）  有声样点数 {_nonzero}");
        Console.WriteLine($"末时间戳 {_lastTs}（{_lastTs / 1e7:F2}s）");

        if (profile is { Count: > 0 })
        {
            // 每秒一格：`#` 越满越响。用来确认声音在片段里出现在**该出现的位置**
            // ——静音补齐如果写错了时间轴，声音会整体平移或前半段被吃掉，一眼就能看出来。
            Console.WriteLine("每秒 RMS（1 格 = 1 秒，从片头开始）：");
            foreach (var (sec, db) in profile)
            {
                int bars = db <= -60 ? 0 : Math.Clamp((int)((db + 60) / 3), 1, 20);
                Console.WriteLine($"  {sec,3}s {db,6:F1} dBFS {new string('#', bars)}{new string('.', 20 - bars)}");
            }
        }

        if (_peak < 500) { Console.WriteLine("[X] 音轨基本是静音"); return 8; }
        Console.WriteLine("[ok] 音轨有真实波形");
        return 0;
    }

    private static long _samples, _bytes, _lastTs, _nonzero;
    private static int _peak;
    private static double _rms, _peakDb, _rmsDb, _secs;

    /// <summary>解码整条音轨，返回每秒 RMS 曲线；失败返回 -1。</summary>
    private static int Decode(string path, out List<(int Sec, double Db)> profile)
    {
        profile = new List<(int, double)>();
        MediaFactory.MFStartup(false);
        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);

        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        try { reader.SetStreamSelection((int)SourceReaderIndex.FirstAudioStream, true); }
        catch
        {
            Console.WriteLine("[!] 这个文件没有音轨（--no-audio 录的？）");
            reader.Dispose();
            return -1;
        }

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
            return -1;
        }
        finally { pcmOut?.Dispose(); }

        // 逐秒累加能量
        var sumSqPerSec = new List<double>();
        var cntPerSec = new List<long>();
        double sumSq = 0;
        long nSamplesGlobal = 0;
        long posInSec = 0;

        while (true)
        {
            var s = reader.ReadSample((int)SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
                out _, out var flags, out var ts);
            if (flags.HasFlag(SourceReaderFlag.EndOfStream)) { s?.Dispose(); break; }
            if (s is null) continue;
            _samples++;
            _lastTs = ts;
            try
            {
                var buf = s.ConvertToContiguousBuffer();
                buf.Lock(out IntPtr p, out _, out int cur);
                _bytes += cur;
                unsafe
                {
                    short* sp = (short*)p;
                    int n = cur / 2;
                    for (int i = 0; i < n; i++)
                    {
                        int v = sp[i];
                        int a = v < 0 ? -v : v;
                        if (a > _peak) _peak = a;
                        if (a > 150) _nonzero++;
                        double sq = (double)v * v;
                        sumSq += sq; nSamplesGlobal += 1;
                        int sec = (int)(posInSec / 2 / 48000);
                        while (sumSqPerSec.Count <= sec) { sumSqPerSec.Add(0); cntPerSec.Add(0); }
                        sumSqPerSec[sec] += sq;
                        cntPerSec[sec]++;
                        posInSec++;
                    }
                }
                buf.Unlock();
                buf.Dispose();
            }
            catch (Exception e) { Console.WriteLine($"[!] 取样失败: {e.Message}"); }
            s.Dispose();
        }
        reader.Dispose();

        _rms = nSamplesGlobal > 0 ? Math.Sqrt(sumSq / nSamplesGlobal) : 0;
        _peakDb = _peak > 0 ? 20 * Math.Log10(_peak / 32768.0) : -999;
        _rmsDb = _rms > 0 ? 20 * Math.Log10(_rms / 32768.0) : -999;
        _secs = nSamplesGlobal / 2.0 / 48000.0;

        for (int i = 0; i < sumSqPerSec.Count; i++)
        {
            double r = cntPerSec[i] > 0 ? Math.Sqrt(sumSqPerSec[i] / cntPerSec[i]) : 0;
            profile.Add((i, r > 0.5 ? 20 * Math.Log10(r / 32768.0) : -999));
        }
        return 0;
    }
}
