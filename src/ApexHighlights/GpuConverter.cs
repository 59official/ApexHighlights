using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ApexHighlights;

/// <summary>
/// GPU 直通：用 D3D11 VideoProcessor 把 WGC 的 BGRA 帧在 GPU 上转成 NV12，
/// 供 NVENC MFT 直接消费（整帧不落 CPU）。检测条带仍由 Capture 单独小区域回读。
/// </summary>
internal sealed class GpuConverter : IDisposable
{
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VideoDevice _vd;
    private readonly ID3D11VideoContext _vc;
    private readonly ID3D11VideoProcessorEnumerator _enum;
    private readonly ID3D11VideoProcessor _proc;

    // 输入 full-range RGB（BGRA 0-255），输出 BT.709 有限范围（16-235）
    private readonly VideoProcessorColorSpace _inCs = new() { RGB_Range = 0 };
    private readonly VideoProcessorColorSpace _outCs = new() { YCbCr_Matrix = 1, Nominal_Range = 1 };

    public GpuConverter(ID3D11Device dev, ID3D11DeviceContext ctx, int w, int h, int fps = 60)
    {
        _ctx = ctx;
        _vd = dev.QueryInterface<ID3D11VideoDevice>();
        _vc = ctx.QueryInterface<ID3D11VideoContext>();

        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational { Numerator = (uint)Math.Max(1, fps), Denominator = 1 },
            InputWidth = (uint)w,
            InputHeight = (uint)h,
            OutputFrameRate = new Rational { Numerator = (uint)Math.Max(1, fps), Denominator = 1 },
            OutputWidth = (uint)w,
            OutputHeight = (uint)h,
            Usage = VideoUsage.PlaybackNormal,
        };
        var r = _vd.CreateVideoProcessorEnumerator(desc, out _enum);
        if (r.Failure || _enum is null) throw new InvalidOperationException($"CreateVideoProcessorEnumerator 0x{(int)r:X8}");
        r = _vd.CreateVideoProcessor(_enum, 0, out _proc);
        if (r.Failure || _proc is null) throw new InvalidOperationException($"CreateVideoProcessor 0x{(int)r:X8}");
    }

    public void Convert(ID3D11Texture2D srcBgra, ID3D11Texture2D dstNv12)
    {
        var inDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,   // 0 = 用资源自身格式（BGRA）
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        var outDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };

        var rIn = _vd.CreateVideoProcessorInputView(srcBgra, _enum, inDesc, out var inView);
        if (rIn.Failure || inView is null) throw new InvalidOperationException($"InputView 0x{(int)rIn:X8}");
        using (inView)
        {
            var rOut = _vd.CreateVideoProcessorOutputView(dstNv12, _enum, outDesc, out var outView);
            if (rOut.Failure || outView is null) throw new InvalidOperationException($"OutputView 0x{(int)rOut:X8}");
            using (outView)
            {
                _vc.VideoProcessorSetStreamFrameFormat(_proc, 0, VideoFrameFormat.Progressive);
                _vc.VideoProcessorSetStreamColorSpace(_proc, 0, _inCs);
                _vc.VideoProcessorSetOutputColorSpace(_proc, _outCs);

                var stream = new VideoProcessorStream
                {
                    Enable = true,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    InputSurface = inView,
                };
                var rBlt = _vc.VideoProcessorBlt(_proc, outView, 0, new[] { stream });
                if (rBlt.Failure) throw new InvalidOperationException($"VideoProcessorBlt 0x{(int)rBlt:X8}");
            }
        }
        // 把 Blt 推到 GPU 命令缓冲，确保编码器（经 D3D Manager 读同一设备）看到转换结果
        _ctx.Flush();
    }

    public void Dispose()
    {
        _proc?.Dispose();
        _enum?.Dispose();
        _vc?.Dispose();
        _vd?.Dispose();
    }
}
