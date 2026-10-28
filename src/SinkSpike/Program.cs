using System;
using System.Diagnostics;
using System.IO;
using Vortice.MediaFoundation;

internal static class P
{
    static int W = 2560, H = 1440, FPS = 60;
    static byte[] NV12;

    static int Main(string[] args)
    {
        int frames = args.Length > 0 ? int.Parse(args[0]) : 180;
        if (args.Length > 2) { var wh = args[2].Split('x'); W = int.Parse(wh[0]); H = int.Parse(wh[1]); }
        NV12 = new byte[W * H * 3 / 2];
        for (int i = 0; i < W * H; i++) NV12[i] = (byte)((i * 7 + i / W) % 255);
        for (int i = W * H; i < NV12.Length; i++) NV12[i] = 128;
        string outPath = args.Length > 1 ? args[1] : @"D:\played\captures\sink.mp4";
        try
        {
            MediaFactory.MFStartup(false);
            if (File.Exists(outPath)) File.Delete(outPath);

            var wattrs = MediaFactory.MFCreateAttributes(4);
            try { wattrs.Set(Attr.EnableHardwareTransforms, true); } catch { }
            var writer = MediaFactory.MFCreateSinkWriterFromURL(outPath, null, wattrs);
            Console.WriteLine("[step] writer ok");

            var outType = MediaFactory.MFCreateMediaType();
            outType.Set(Attr.MajorType, MediaTypeGuids.Video);
            outType.Set(Attr.SubType, VideoFormatGuids.H264);
            outType.Set(Attr.AvgBitrate, 20_000_000u);
            outType.Set(Attr.FrameSize, Pack64(W, H));
            outType.Set(Attr.FrameRate, Pack32(FPS, 1));
            outType.Set(Attr.InterlaceMode, 2u);
            outType.Set(Attr.PixelAspectRatio, Pack32(1, 1));
            outType.Set(Attr.Mpeg2Profile, 100u);
            int sid;
            try { sid = writer.AddStream(outType); Console.WriteLine($"[step] AddStream ok sid={sid}"); }
            catch (Exception e) { Console.WriteLine($"[X] AddStream: {e.Message}"); return 3; }

            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(Attr.MajorType, MediaTypeGuids.Video);
            bool rawMode = Environment.GetEnvironmentVariable("RAW") == "1";
            inType.Set(Attr.SubType, rawMode ? VideoFormatGuids.H264 : VideoFormatGuids.NV12);
            if (rawMode) { inType.Set(Attr.AvgBitrate, 20_000_000u); }
            inType.Set(Attr.FrameSize, Pack64(W, H));
            inType.Set(Attr.FrameRate, Pack32(FPS, 1));
            inType.Set(Attr.PixelAspectRatio, Pack32(1, 1));
            inType.Set(Attr.InterlaceMode, 2u);
            Dump("in ", inType); Dump("out", outType);
            var encAttrs = MediaFactory.MFCreateAttributes(2);
            try { writer.SetInputMediaType(sid, inType, encAttrs); Console.WriteLine("[step] SetInputMediaType ok"); }
            catch (Exception e) { Console.WriteLine($"[X] SetInputMediaType: {e.Message}"); return 4; }

            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                var s = MediaFactory.MFCreateSample();
                var b = MediaFactory.MFCreateMemoryBuffer(NV12.Length);
                b.Lock(out IntPtr p, out _, out _);
                System.Runtime.InteropServices.Marshal.Copy(NV12, 0, p, NV12.Length);
                b.Unlock();
                b.CurrentLength = NV12.Length;
                s.AddBuffer(b);
                s.SampleTime = f * (10_000_000L / FPS);
                s.SampleDuration = 10_000_000L / FPS;
                try { writer.WriteSample(sid, s); } catch (Exception e) { Console.WriteLine($"[X] WriteSample#{f}: {e.Message}"); return 5; }
                s.Dispose(); b.Dispose();
            }
            try { writer.Finalize(); } catch (Exception e) { Console.WriteLine($"[X] Finalize: {e.Message}"); return 6; }
            sw.Stop();
            var fi = new FileInfo(outPath);
            Console.WriteLine($"[ok] {frames} 帧 {sw.Elapsed.TotalSeconds:F2}s => {frames / sw.Elapsed.TotalSeconds:F1} fps, 输出 {fi.Length} 字节 ({fi.Length * 8.0 / 1e6:F2} Mbit)");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[X] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    static void Dump(string tag, IMFMediaType t)
    {
        Console.WriteLine($"[{tag}] major={G(t, Attr.MajorType)} sub={G(t, Attr.SubType)} size={U64(t, Attr.FrameSize):X} rate={U64(t, Attr.FrameRate):X} bitrate={U32(t, Attr.AvgBitrate)} il={U32(t, Attr.InterlaceMode)} par={U64(t, Attr.PixelAspectRatio):X}");
    }
    static string G(IMFMediaType t, Guid g) { try { return t.GetGUID(g).ToString(); } catch { return "-"; } }
    static ulong U64(IMFMediaType t, Guid g) { try { return t.GetUInt64(g); } catch { return 0; } }
    static string U32(IMFMediaType t, Guid g) { try { return t.GetUInt32(g).ToString(); } catch (Exception e) { return "ERR"; } }

    static ulong Pack64(int a, int b) => ((ulong)(uint)b << 32) | (uint)a;
    static ulong Pack32(int n, int d) => ((ulong)(uint)d << 32) | (uint)n;
}

internal static class Attr
{
    public static readonly Guid TranscodeContainertype = new("1bba6f49-c1f7-4be2-b9d1-b2f3a0a4e4d9");
    public static readonly Guid ReadwriteEnableHardwareTransforms = new("5c3d1b4e-4d6b-4cef-9f0a-4e8b0d5c0b1a");
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid TranscodeContainertypeMp4 = new("1bba6f49-c1f7-4be2-b9d1-b2f3a0a4e4d9");
    public static readonly Guid EnableHardwareTransforms = new("3e8f2b1c-9d1a-4b0e-8f5c-0a1b2c3d4e5f");
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
}
