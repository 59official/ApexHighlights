using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// ICodecAPI 的最小封装。唯一用途：给编码器设 GOP 长度。
/// NVENC 不认 MF_MT_MAX_KEYFRAME_SPACING，而我们的分片环形必须以 IDR 对齐。
/// GUID 靠 IsSupported 自证：写错就是 S_FALSE，不会静默通过。
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    // 画质相关：码率控制模式 / 质量优先度 / 峰值码率
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid MaxBitRate = new("9651e76e-850a-46d3-b52b-f166310c0d09");

    private readonly IntPtr _self;      // 接口指针（this），不是 vtable
    private readonly void** _vt;

    private CodecApi(IntPtr self, void** vt) { _self = self; _vt = vt; }

    public static CodecApi? TryGet(IntPtr unk)
    {
        if (Marshal.QueryInterface(unk, IID_ICodecAPI, out var p) < 0 || p == IntPtr.Zero) return null;
        return new CodecApi(p, (void**)Marshal.ReadIntPtr(p));
    }

    public bool IsSupported(Guid g)
    {
        var gg = g;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, int>)_vt[3])(_self.ToPointer(), &gg) == 0;
    }

    public bool SetUInt(Guid g, uint v)
    {
        var gg = g;
        var va = new Variant { vt = 19, u32 = v };          // VT_UI4
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, Variant*, int>)_vt[9])(_self.ToPointer(), &gg, &va) == 0;
    }

    public uint? GetUInt(Guid g)
    {
        var gg = g;
        var va = new Variant { vt = 19 };
        int hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, Variant*, int>)_vt[8])(_self.ToPointer(), &gg, &va);
        return hr == 0 ? va.u32 : null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint u32;
    }

    public void Dispose() { }
}
