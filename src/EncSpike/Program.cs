using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace EncSpike;

internal static class Program
{
    private const int W = 2560, H = 1440, FPS = 60;
    private static readonly byte[] Nv12;
    private static IMFDXGIDeviceManager _mgr;

    static Program()
    {
        Nv12 = new byte[W * H * 3 / 2];
        for (int i = 0; i < W * H; i++) Nv12[i] = (byte)((i * 7) % 255);
        for (int i = W * H; i < Nv12.Length; i++) Nv12[i] = 128;
    }

    private static int Main()
    {
        try
        {
            MediaFactory.MFStartup(false);
            Console.WriteLine("[env] MFStartup ok");
            var dev = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 });
            _mgr = MediaFactory.MFCreateDXGIDeviceManager();
            Console.WriteLine($"[env] d3d11+manager -> {_mgr.ResetDevice(dev)}");

            var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
            var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x7, inInfo, outInfo, out var ptrs, out var count);
            Console.WriteLine($"[enum] {count} encoders");
            if (count == 0) return 2;

            var arr = new IntPtr[count];
            Marshal.Copy(ptrs, arr, 0, (int)count);

            for (int i = 0; i < arr.Length; i++)
            {
                var act = new IMFActivate(arr[i]);
                string name = TryStr(act, TransformAttributeKeys.MftFriendlyNameAttribute);
                IMFTransform t;
                try { t = act.ActivateObject<IMFTransform>(); }
                catch (Exception e) { Console.WriteLine($"[{i}] {name} activate fail {e.Message}"); act.Dispose(); continue; }
                act.Dispose();
                Console.WriteLine($"[{i}] try {name} ...");
                if (Run(t, out string err)) { Console.WriteLine($"[ok] {name}"); return 0; }
                Console.WriteLine($"    fail at {err}");
                t.Dispose();
            }
            Console.WriteLine("[x] all failed");
            return 5;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[X] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static bool Run(IMFTransform enc, out string step)
    {
        step = "unlock";
        try
        {
            var r = enc.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            string asyncFlag = "-";
            try { asyncFlag = enc.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync).ToString(); } catch { }
            Console.WriteLine($"    unlock={r.Success} async={asyncFlag}");
        }
        catch (Exception e) { Console.WriteLine($"    unlock: {e.Message}"); }

        try { enc.ProcessMessage((TMessageType)2, (UIntPtr)_mgr.NativePointer); }
        catch (Exception e) { Console.WriteLine($"    set-d3d-manager: {e.Message}"); }

        try
        {
            for (int i = 0; i < 10; i++)
            {
                IMFMediaType mt;
                try { mt = enc.GetOutputAvailableType(0, i); } catch { break; }
                if (mt == null) break;
                Console.WriteLine("    out-avail[" + i + "] " + mt.GetGUID(MediaTypeAttributeKeys.Subtype));
                mt.Dispose();
            }
        }
        catch (Exception e) { Console.WriteLine("    out-avail: " + e.Message); }

        IMFMediaType inType = null, outType = null;
        try
        {
            inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack64(W, H));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack32(FPS, 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack32(1, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);

            for (int i = 0; i < 32; i++)
            {
                IMFMediaType mt;
                try { mt = enc.GetOutputAvailableType(0, i); } catch { break; }
                if (mt == null) break;
                Guid sub = Guid.Empty;
                try { sub = mt.GetGUID(MediaTypeAttributeKeys.Subtype); } catch { }
                if (sub == VideoFormatGuids.H264) { outType = mt; break; }
                mt.Dispose();
            }
            if (outType == null) { Console.WriteLine("    no H264 output"); return false; }
            try { outType.Set(MediaTypeAttributeKeys.AvgBitrate, 20000000u); } catch { }
            try { outType.Set(MediaTypeAttributeKeys.FrameSize, Pack64(W, H)); } catch { }
            try { outType.Set(MediaTypeAttributeKeys.FrameRate, Pack32(FPS, 1)); } catch { }

            bool inFirst = Environment.GetEnvironmentVariable("IN_FIRST") == "1";
            if (inFirst)
            {
                enc.SetInputType(0, inType, 0);
                Console.WriteLine("    input ok");
                enc.SetOutputType(0, outType, 0);
                Console.WriteLine("    output ok");
            }
            else
            {
                enc.SetOutputType(0, outType, 0);
                Console.WriteLine("    output ok");
                enc.SetInputType(0, inType, 0);
                Console.WriteLine("    input ok");
            }
        }
        catch (Exception e) { Console.WriteLine("    types: " + e.Message); return false; }

        return Encode(enc);
    }

    private static bool Encode(IMFTransform enc)
    {
        var osi = enc.GetOutputStreamInfo(0);
        bool provides = (osi.Flags & 0x100) != 0;
        var gen = enc.QueryInterface<IMFMediaEventGenerator>();
        enc.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        enc.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        var sw = Stopwatch.StartNew();
        long total = 0; int samples = 0; int fed = 0, rejected = 0; string firstHex = "";

        for (int f = 0; f < 120; f++)
        {
            var sample = BuildSample(f);
            int guard = 0;
            while (true)
            {
                int et = WaitEvent(gen);
                if (et == 601)
                {
                    try { enc.ProcessInput(0, sample, 0); fed++; break; }
                    catch (Exception e) { rejected++; if (rejected < 3) Console.WriteLine("    reject " + e.Message); }
                }
                else if (et == 602)
                {
                    var got = Pull(enc, provides, osi.Size);
                    if (got != null)
                    {
                        samples++; total += got.TotalLength;
                        if (firstHex.Length == 0) firstHex = DumpFirst(got);
                        got.Dispose();
                    }
                }
                if (++guard > 400) { rejected++; break; }
            }
            sample.Dispose();
        }

        enc.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        for (int i = 0; i < 400; i++)
        {
            var got = Pull(enc, provides, osi.Size);
            if (got == null) break;
            samples++; total += got.TotalLength;
            if (firstHex.Length == 0) firstHex = DumpFirst(got);
            got.Dispose();
        }
        sw.Stop();
        Console.WriteLine($"    fed={fed} rejected={rejected} pkts={samples} bytes={total} ({(samples > 0 ? total * 8.0 / 1e6 : 0):F2} Mbit) {sw.Elapsed.TotalMilliseconds / 120:F2} ms/frame");
        Console.WriteLine($"    first {firstHex}");
        return samples > 0;
    }

    private static IMFSample BuildSample(int f)
    {
        // 每帧加一条移动的亮条，让码率接近真实动态画面
        int barX = (f * 37) % W;
        int luma = W * H;
        for (int y = 200; y < H - 200; y += 2)
        {
            int row = y * W + barX;
            for (int k = 0; k < 160 && row + k < luma; k++) Nv12[row + k] = 235;
        }
        var sample = MediaFactory.MFCreateSample();
        var buf = MediaFactory.MFCreateMemoryBuffer(Nv12.Length);
        buf.Lock(out IntPtr p, out _, out _);
        Marshal.Copy(Nv12, 0, p, Nv12.Length);
        buf.Unlock();
        buf.CurrentLength = Nv12.Length;
        sample.AddBuffer(buf);
        sample.SampleTime = (long)f * (10_000_000L / FPS);
        sample.SampleDuration = 10_000_000L / FPS;
        return sample;
    }

    private static int WaitEvent(IMFMediaEventGenerator gen)
    {
        for (int i = 0; i < 64; i++)
        {
            IMFMediaEvent ev;
            try { ev = gen.GetEvent(50); } catch { return -1; }
            if (ev == null) return -1;
            int t = (int)ev.EventType;
            ev.Dispose();
            if (t == 601 || t == 602) return t;
        }
        return -1;
    }

    private static void DrainEvents(IMFTransform enc)
    {
        IMFMediaEventGenerator gen;
        try { gen = enc.QueryInterface<IMFMediaEventGenerator>(); } catch { return; }
        for (int i = 0; i < 64; i++)
        {
            IMFMediaEvent ev;
            try { ev = gen.GetEvent(1); } catch { break; }
            if (ev == null) break;
            ev.Dispose();
        }
        gen.Dispose();
    }

    private static IMFSample Pull(IMFTransform enc, bool provides, int size)
    {
        var odb = new OutputDataBuffer();
        if (!provides)
        {
            var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(size, 1024));
            var s = MediaFactory.MFCreateSample();
            s.AddBuffer(mb);
            odb.Sample = s;
        }
        var res = enc.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
        if (!res.Success) { try { odb.Sample?.Dispose(); } catch { } return null; }
        return odb.Sample;
    }

    private static string DumpFirst(IMFSample s)
    {
        var b = s.ConvertToContiguousBuffer();
        b.Lock(out IntPtr p, out int max, out int cur);
        int n = Math.Min(cur, 20);
        var h = new byte[n];
        Marshal.Copy(p, h, 0, n);
        b.Unlock();
        b.Dispose();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(h[i].ToString("X2")).Append(' ');
        return sb.ToString();
    }

    private static string TryStr(IMFAttributes a, Guid g)
    {
        try { return a.GetString(g) ?? "?"; } catch { return "?"; }
    }

    private static ulong Pack64(int a, int b) => ((ulong)(uint)b << 32) | (uint)a;
    private static ulong Pack32(int num, int den) => ((ulong)(uint)den << 32) | (uint)num;
}
