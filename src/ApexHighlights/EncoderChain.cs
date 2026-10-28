using System.Diagnostics;
using Vortice.Direct3D11;

namespace ApexHighlights;

/// <summary>
/// 分段编码器：NVENC 不认 CODECAPI_AVEncMPVGOPSize（实测无效），所以用「双缓冲重建」拿 IDR。
/// 每 <see cref="_framesPerSegment"/> 帧换一台预建好的编码器，重建在后台线程做，
/// 切换本身只是换指针 —— 实测单次重建 80–90ms，前台不会卡。
/// </summary>
public sealed class EncoderChain : IDisposable
{
    private readonly int _w, _h, _bitrate;
    private readonly int _framesPerSegment, _fps;
    private readonly ID3D11Device? _d3d;
    private NvEncoder _cur;
    private Task<NvEncoder>? _next;
    private int _count;
    private bool _disposed;

    public long RotateMs;
    public int Rotations;
    public string SubmitErr => _cur.SurfaceErr;
    public int Dropped => _cur.Dropped;
    public int Pushed => _cur.Pushed;
    public string Name => _cur.Name;

    public EncoderChain(int w, int h, int bitrate, int framesPerSegment = 60, int fps = 60, ID3D11Device? d3d = null)
    {
        _w = w; _h = h; _bitrate = bitrate;
        _framesPerSegment = framesPerSegment; _fps = fps; _d3d = d3d;
        _cur = new NvEncoder(w, h, bitrate, framesPerSegment, fps, d3d);
        if (_framesPerSegment > 0) StartRefill();
    }

    private void StartRefill()
    {
        if (_disposed) return;
        _next ??= Task.Run(() => new NvEncoder(_w, _h, _bitrate, _framesPerSegment, _fps, _d3d));
    }

    public void Submit(long pts, byte[] nv12, Action<byte[], long> onPacket)
    {
        if (_framesPerSegment > 0 && _count >= _framesPerSegment) { if (!_cur.Reinit()) Rotate(); else _count = 0; }
        _cur.Submit(pts, nv12, onPacket);
        _count++;
    }

    /// <summary>GPU 直通：提交一帧已在 GPU 上的 NV12 纹理。</summary>
    public void SubmitSurface(long pts, ID3D11Texture2D nv12Tex, Action<byte[], long> onPacket)
    {
        if (_framesPerSegment > 0 && _count >= _framesPerSegment) { if (!_cur.Reinit()) Rotate(); else _count = 0; }
        _cur.SubmitSurface(pts, nv12Tex, onPacket);
        _count++;
    }

    private void Rotate()
    {
        var sw = Stopwatch.StartNew();
        var t = _next;
        _next = null;
        var fresh = t is null ? new NvEncoder(_w, _h, _bitrate, _framesPerSegment, _fps, _d3d) : t.GetAwaiter().GetResult();
        _cur.Dispose();
        _cur = fresh;
        _count = 0;
        sw.Stop();
        RotateMs += sw.ElapsedMilliseconds;
        Rotations++;
        StartRefill();
    }

    public void Dispose()
    {
        _disposed = true;
        _cur.Dispose();
        try { _next?.Wait(500); } catch { }
        _next?.Result?.Dispose();
    }
}
