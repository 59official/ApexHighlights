using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ApexHighlights;

/// <summary>离线自检（GPU 直通）：合成 BGRA → VideoProcessor 转 NV12 → NVENC（surface 直喂）→ 分片环形 → 拼 mp4。</summary>
internal static class GpuTest
{
    public static int Run(int w, int h, int frames, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var ringDir = Path.Combine(outDir, "ring");
        var clipPath = Path.Combine(outDir, "clip.mp4");

        ID3D11Device dev; ID3D11DeviceContext ctx;
        var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out dev, out ctx);
        if (r.Failure) { Console.WriteLine($"[X] D3D11CreateDevice 0x{(int)r:X8}"); return 2; }

        Console.WriteLine($"[1] GPU 直通 {w}x{h} @ {frames} 帧");
        var converter = new GpuConverter(dev, ctx, w, h);
        var enc = new EncoderChain(w, h, 20_000_000, 60, 60, dev);
        Console.WriteLine($"[2] 选中 {enc.Name}（每 60 帧换编码器拿 IDR）");

        var ring = new SegmentRing(ringDir, w, h, 120);
        Console.WriteLine("[2a] ring ok");

        // BGRA 输入：dynamic 上传纹理填数据 → default 纹理（VideoProcessor 输入）
        // 注意：Staging 不能带 CPUAccessFlags.Write，CPU 写入必须用 Dynamic + WriteDiscard
        var staging = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
        });
        Console.WriteLine("[2a] staging ok");
        var src = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget, CPUAccessFlags = CpuAccessFlags.None,
        });
        Console.WriteLine("[2b] src ok");
        ID3D11Texture2D dst = null!;
        foreach (var bf in new[] { BindFlags.RenderTarget, BindFlags.ShaderResource, BindFlags.Decoder, BindFlags.UnorderedAccess, BindFlags.None })
        {
            try
            {
                dst = dev.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = bf, CPUAccessFlags = CpuAccessFlags.None,
                });
                Console.WriteLine($"[2c] dst ok bind={bf}");
                break;
            }
            catch (Exception e) { Console.WriteLine($"[2c] dst bind={bf} 失败 {e.GetType().Name}"); }
        }

        if (dst is null) { Console.WriteLine("[X] 建不出 NV12 输出纹理"); return 2; }

        int packets = 0, idrs = 0;
        long bytes = 0;
        var swConv = new Stopwatch();
        var swAll = Stopwatch.StartNew();

        for (int f = 0; f < frames; f++)
        {
            Fill(staging, ctx, w, h, f);
            ctx.CopyResource(src, staging);
            if (f == 0) Console.WriteLine("[2d] copy ok");

            swConv.Start();
            try { converter.Convert(src, dst); }
            catch (Exception e) { Console.WriteLine($"[X] Convert 帧{f}: {e.Message}"); return 4; }
            swConv.Stop();
            if (f == 0)
            {
                Console.WriteLine("[3a] Convert ok");
                DumpNv12Stats(dev, ctx, dst, w, h);
            }

            int fi = f;
            try { enc.SubmitSurface(f * 166_667L, dst, (data, pts) =>
            {
                packets++;
                if (fi % 60 == 0)
                {
                    var types = string.Join("", Mp4.SplitAnnexB(data).Select(n => n.Type.ToString()));
                    Console.WriteLine($"    f{fi} types={types}");
                }
                bytes += data.Length;
                if (fi < 5 || fi == 60) Console.WriteLine($"    f{fi} pkt={data.Length}B");
                foreach (var n in Mp4.SplitAnnexB(data))
                    if (n.Type == 5) idrs++;
                ring.Append(pts / 10, data);
            }); }
            catch (Exception e) { Console.WriteLine($"[X] SubmitSurface 帧{f}: {e.Message}"); return 5; }
        }
        swAll.Stop();
        ring.Flush();

        Console.WriteLine($"[3x] err={enc.SubmitErr}  d3d[{NvEncoder.D3DInfo}]");
        Console.WriteLine($"[3] 编码包 {packets}（IDR {idrs}）  推入 {enc.Pushed} 丢 {enc.Dropped}");
        Console.WriteLine($"[3b] 换编码器 {enc.Rotations} 次，累计阻塞 {enc.RotateMs} ms");
        Console.WriteLine($"[4] GPU 转换 {swConv.Elapsed.TotalMilliseconds / frames:F2} ms/帧   总 {swAll.Elapsed.TotalMilliseconds / frames:F2} ms/帧");
        Console.WriteLine($"[5] 分片 {ring.Segments.Count} 个，磁盘 {ring.DiskBytes() / 1024.0 / 1024.0:F1} MB，覆盖 {ring.Coverage()}");

        if (ring.Segments.Count == 0) { Console.WriteLine("[X] 一段都没落盘"); return 3; }

        var clip = ring.BuildClip(0, long.MaxValue);
        File.WriteAllBytes(clipPath, clip);
        Console.WriteLine($"[7] 写出 {clipPath}  {clip.Length / 1024.0 / 1024.0:F1} MB  (init {ring.Init!.Length} 字节)");
        Console.WriteLine($"[9] MF 回读 clip.mp4: {(Mp4Check.Run(clipPath, requireAudio: false) == 0 ? "ok" : "失败")}");
        Console.WriteLine($"[8] 码率 {(bytes * 8.0 / Math.Max(1, frames) * 60 / 1e6):F1} Mbps");

        staging.Dispose(); src.Dispose(); dst.Dispose(); ring.Dispose(); enc.Dispose(); converter.Dispose(); ctx.Dispose(); dev.Dispose();
        return 0;
    }

    /// <summary>把 NV12 纹理回读出来统计 Y 的分布，确认 VideoProcessor 真的写进去了（不是全黑）。</summary>
    private static unsafe void DumpNv12Stats(ID3D11Device dev, ID3D11DeviceContext ctx, ID3D11Texture2D nv12, int w, int h)
    {
        using var st = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        ctx.CopyResource(st, nv12);
        var map = ctx.Map(st, 0, MapMode.Read);
        try
        {
            var p = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            int min = 255, max = 0; long sum = 0; int n = 0;
            for (int y = 0; y < h; y += 7)
                for (int x = 0; x < w; x += 7)
                {
                    int v = p[(long)y * pitch + x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v; n++;
                }
            Console.WriteLine($"[3b] NV12 Y: min={min} max={max} avg={sum / Math.Max(1, n)}  pitch={pitch}");
        }
        finally { ctx.Unmap(st, 0); }
    }

    private static unsafe void Fill(ID3D11Texture2D staging, ID3D11DeviceContext ctx, int w, int h, int f)
    {
        var map = ctx.Map(staging, 0, MapMode.WriteDiscard);
        var p = (byte*)map.DataPointer;
        int pitch = (int)map.RowPitch;
        int bar = (f * 23) % w;
        for (int y = 0; y < h; y++)
        {
            byte baseV = (byte)(40 + y * 120 / h);
            var row = p + (long)y * pitch;
            for (int x = 0; x < w; x++)
            {
                int i = x * 4;
                int v = baseV + (x >> 3) % 60;
                if (Math.Abs(x - bar) < 60) v += 140;
                v += (x ^ y) & 7;
                row[i] = (byte)(v * 0.9);
                row[i + 1] = (byte)Math.Min(255, v);
                row[i + 2] = (byte)Math.Min(255, v * 0.7);
                row[i + 3] = 255;
            }
        }
        ctx.Unmap(staging, 0);
    }
}
