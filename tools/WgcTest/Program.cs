// WgcTest —— 用 Windows Graphics Capture (WGC) 抓一次目标窗口，判断能不能出画面。
//
// 用法:
//   dotnet WgcTest.dll self         抓自己的控制台窗口（阳性对照，必须成功）
//   dotnet WgcTest.dll              抓标题含 "Apex Legends" 的窗口
//   dotnet WgcTest.dll <标题片段>    抓第一个标题匹配的可见顶层窗口
//   dotnet WgcTest.dll --list       只列出所有可见顶层窗口
//
// 退出码: 0=能抓且有画面  2=没找到窗口  3=CaptureItem 失败  4=D3D11 失败
//         10=拿不到任何帧  11=有帧但全黑

using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using D3D11Device = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace WgcTest;

internal static class Program
{
    private static readonly object Gate = new();
    private static int _frames;
    private static double _minLuma = 255, _maxLuma, _sumLuma;
    private static long _samples;
    private static int _winFrames;
    private static double _winMin = 255, _winMax, _winSum;
    private static long _winSamples;
    private static string _firstError = "";
    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _ctx;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--list") { ListWindows(); return 0; }

        var hwnd = ResolveWindow(args);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("[X] 没找到匹配窗口，用 --list 看看有哪些"); return 2; }
        Console.WriteLine($"[1] 目标窗口: \"{TitleOf(hwnd)}\"  hwnd=0x{hwnd:X}");

        GraphicsCaptureItem item;
        try { item = CreateItem(hwnd); }
        catch (Exception ex) { Console.WriteLine($"[X] CreateForWindow 失败: {ex.GetType().Name}: {ex.Message}"); return 3; }
        Console.WriteLine($"[2] CaptureItem OK  Size={item.Size.Width}x{item.Size.Height}");
        if (item.Size.Width == 0) { Console.WriteLine("[X] 尺寸为 0（窗口最小化了？）"); return 3; }

        try
        {
            var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device!, out _ctx!);
            Console.WriteLine($"[3] D3D11 设备 OK  featureLevel={_device!.FeatureLevel}  ({r})");
        }
        catch (Exception ex) { Console.WriteLine($"[X] D3D11 设备创建失败: {ex.Message}"); return 4; }

        using var dev = _device;
        using var ctx = _ctx;

        var winrtDevice = ToWinRTDevice(dev);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { }

        pool.FrameArrived += (s, _) => OnFrame(s);
        session.StartCapture();
        Console.WriteLine("[4] 会话已启动，每秒报一次；这期间把 Apex 切到前台看数字变化…");
        for (var sec = 1; sec <= 8; sec++)
        {
            Thread.Sleep(1000);
            lock (Gate)
            {
                var wa = _winSamples > 0 ? _winSum / _winSamples : 0;
                Console.WriteLine($"    t={sec}s  帧={_winFrames,3}  亮度 min={_winMin,3:F0} max={_winMax,3:F0} avg={wa,5:F1}  采样={_winSamples}");
                _winFrames = 0; _winMin = 255; _winMax = 0; _winSum = 0; _winSamples = 0;
            }
        }
        session.Dispose();
        pool.Dispose();

        lock (Gate)
        {
            Console.WriteLine($"[5] 收到帧: {_frames}");
            if (_firstError.Length > 0) Console.WriteLine($"[!] 读帧异常: {_firstError}");
            if (_frames == 0) { Console.WriteLine("\n[结论] 拿不到任何帧 → WGC 抓不到这个窗口。"); return 10; }

            var avg = _samples > 0 ? _sumLuma / _samples : 0;
            Console.WriteLine($"[6] 亮度 min={_minLuma:F0} max={_maxLuma:F0} avg={avg:F1}  采样点={_samples}");
            if (_maxLuma < 12 && avg < 4)
            {
                Console.WriteLine("\n[结论] 有帧但画面全黑 → 抓得到窗口，抓不到内容（典型是独占全屏）。");
                return 11;
            }
            Console.WriteLine("\n[结论] 能抓到，画面正常。WGC 可用。");
            return 0;
        }
    }

    private static void OnFrame(Direct3D11CaptureFramePool pool)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) return;
            using var tex = TextureOf(frame);
            if (tex is null) return;

            var sd = tex.Description;
            if (sd.Width == 0 || sd.Height == 0) return;
            sd.Usage = ResourceUsage.Staging;
            sd.BindFlags = BindFlags.None;
            sd.CPUAccessFlags = CpuAccessFlags.Read;
            sd.MiscFlags = ResourceOptionFlags.None;

            using var staging = _device!.CreateTexture2D(sd);
            _ctx!.CopyResource(staging, tex);
            var map = _ctx.Map(staging, 0, MapMode.Read);
            try
            {
                unsafe
                {
                    double lo = 255, hi = 0, sum = 0;
                    long n = 0;
                    var p = (byte*)map.DataPointer;
                    var rp = (int)map.RowPitch;
                    for (var y = 0; y < (int)sd.Height; y += 23)
                    {
                        var row = p + y * rp;
                        for (var x = 0; x < (int)sd.Width; x += 23)
                        {
                            var px = row + x * 4;
                            double lum = 0.114 * px[0] + 0.587 * px[1] + 0.299 * px[2];
                            if (lum < lo) lo = lum;
                            if (lum > hi) hi = lum;
                            sum += lum; n++;
                        }
                    }
                    lock (Gate)
                    {
                        _frames++; _samples += n;
                        _minLuma = Math.Min(_minLuma, lo);
                        _maxLuma = Math.Max(_maxLuma, hi);
                        _sumLuma += sum;
                        _winFrames++; _winSamples += n;
                        _winMin = Math.Min(_winMin, lo);
                        _winMax = Math.Max(_winMax, hi);
                        _winSum += sum;
                    }
                }
            }
            finally { _ctx!.Unmap(staging, 0); }
        }
        catch (Exception ex)
        {
            lock (Gate) { if (_firstError.Length == 0) _firstError = $"{ex.GetType().Name}: {ex.Message}"; }
        }
    }

    private static ID3D11Texture2D? TextureOf(Direct3D11CaptureFrame frame)
    {
        var surface = frame.Surface;
        if (surface is null) return null;
        var self = WinRT.MarshalInspectable<object>.FromManaged(surface);
        var iid = typeof(ID3D11Texture2D).GUID;
        var hr = GetTextureFromSurface(self, ref iid, out var p);
        if (Environment.GetEnvironmentVariable("WGC_DIAG") == "1")
        {
            Console.WriteLine($"[diag] surface self=0x{(long)self:X} getIface hr=0x{hr:X8} tex=0x{(long)p:X}");
            if (p != IntPtr.Zero)
            {
                unsafe
                {
                    var vt = (long*)((long*)p)[0];
                    Console.WriteLine($"[diag] texvtbl=0x{(long)vt:X}");
                    for (var i = 0; i < 12; i++) Console.WriteLine($"   t{i}=0x{vt[i]:X}");
                }
            }
        }
        return hr < 0 || p == IntPtr.Zero ? null : new ID3D11Texture2D(p);
    }

    private static D3D11Device ToWinRTDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var raw);
        if (hr != 0) throw new COMException("CreateDirect3D11DeviceFromDXGIDevice 失败", hr);
        return WinRT.MarshalInterface<D3D11Device>.FromAbi(raw);
    }

    private static IntPtr ResolveWindow(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("self", StringComparison.OrdinalIgnoreCase)) return GetConsoleWindow();
        var frag = args.Length > 0 ? args[0] : "Apex Legends";
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var t = TitleOf(h);
            if (t.Length > 0 && t.Contains(frag, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void ListWindows()
    {
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h))
            {
                var t = TitleOf(h);
                if (t.Length > 0) Console.WriteLine($"0x{h:X8}  {t}");
            }
            return true;
        }, IntPtr.Zero);
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---- WinRT 互操作 ----
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface(ref Guid iid); }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    private static unsafe GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(name, name.Length, out var hstr);
        try
        {
            Guid iid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hr = RoGetActivationFactory(hstr, ref iid, out var factory);
            if (hr < 0) throw new COMException($"RoGetActivationFactory 失败 0x{hr:X8}");
            try
            {
                if (Environment.GetEnvironmentVariable("WGC_DIAG") == "1") Diag(factory);
                // ABI IID of IGraphicsCaptureItem（不能用投影类型的 GUID）
                Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var p = CallCreateForWindow(factory, hwnd, &itemIid);
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(p);
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    private static unsafe void Diag(IntPtr factory)
    {
        Console.WriteLine($"[diag] factory=0x{(long)factory:X}");
        var obj = (long*)factory;
        for (var i = 0; i < 4; i++) Console.WriteLine($"   obj[{i}]=0x{obj[i]:X}");
        var vtbl = (long*)obj[0];
        Console.WriteLine($"[diag] vtbl=0x{(long)vtbl:X}");
        for (var i = 0; i < 6; i++) Console.WriteLine($"   slot{i}=0x{vtbl[i]:X}");

        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        Guid iu = new("00000000-0000-0000-C000-000000000046");
        IntPtr unk;
        var h1 = qi(factory, &iu, &unk);
        Console.WriteLine($"[diag] QI(IUnknown) hr=0x{h1:X8} p=0x{(long)unk:X}");
        if (h1 >= 0) Marshal.Release(unk);
        Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
        IntPtr self;
        var h2 = qi(factory, &interopIid, &self);
        Console.WriteLine($"[diag] QI(Interop) hr=0x{h2:X8} sameClick={(h2 >= 0 ? self == factory : false)}");
        if (h2 >= 0) Marshal.Release(self);
    }

    // 直接走 vtable，绕开 RCW/ComWrappers 的类型转换问题（这正是 WinRT 互操作在 C# 里最容易翻车的地方）
    private static unsafe IntPtr CallCreateForWindow(IntPtr self, IntPtr hwnd, Guid* iid)
    {
        var vtbl = (IntPtr*)Marshal.ReadIntPtr(self);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
        IntPtr result;
        var hr = fn(self, hwnd, iid, &result);
        if (hr < 0) throw new COMException($"CreateForWindow 失败 0x{hr:X8}");
        return result;
    }

    private static unsafe int GetTextureFromSurface(IntPtr self, ref Guid iid, out IntPtr result)
    {
        Guid g = iid;
        // 先正经 QI 拿到 IDirect3DDxgiInterfaceAccess（WinRT 对象可能用调整器 thunk，不能直接假定同一 vtable）
        var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        var vtbl = (IntPtr*)Marshal.ReadIntPtr(self);
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        IntPtr access;
        var hrQi = qi(self, &accessIid, &access);
        if (hrQi < 0)
        {
            Console.WriteLine($"[dbg] QI(DxgiAccess) 失败 0x{hrQi:X8}");
            result = IntPtr.Zero;
            return hrQi;
        }
        if (Environment.GetEnvironmentVariable("WGC_DIAG") == "1")
            Console.WriteLine($"[diag] access=0x{(long)access:X} same={(access == self)}");
        var avtbl = (IntPtr*)Marshal.ReadIntPtr(access);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)avtbl[3];
        IntPtr r;
        var hr = fn(access, &g, &r);
        Marshal.Release(access);
        result = r;
        return hr;
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // ---- Win32 ----
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
}
