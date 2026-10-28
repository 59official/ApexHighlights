using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using D3D11Device = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace ApexHighlights;

/// <summary>
/// WinRT / WGC 互操作。全部走 vtable 直调：CsWinRT 注册了全局 ComWrappers，
/// 经典 COM 的 RCW 转换和 Marshal.GetDelegateForFunctionPointer 都会炸，详见 tools/WgcTest。
/// </summary>
internal static unsafe class Native
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface(ref Guid iid); }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    public static GraphicsCaptureItem CreateItem(IntPtr hwnd)
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
                Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");   // IGraphicsCaptureItem 的 ABI IID
                var p = CallCreateForWindow(factory, hwnd, &itemIid);
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(p);
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    public static ID3D11Texture2D? TextureOf(Direct3D11CaptureFrame frame)
    {
        var surface = frame.Surface;
        if (surface is null) return null;
        var self = WinRT.MarshalInspectable<object>.FromManaged(surface);
        var iid = typeof(ID3D11Texture2D).GUID;
        var hr = GetTextureFromSurface(self, ref iid, out var p);
        return hr < 0 || p == IntPtr.Zero ? null : new ID3D11Texture2D(p);
    }

    public static D3D11Device ToWinRTDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var raw);
        if (hr != 0) throw new COMException("CreateDirect3D11DeviceFromDXGIDevice 失败", hr);
        return WinRT.MarshalInterface<D3D11Device>.FromAbi(raw);
    }

    public static IntPtr ResolveWindow(string fragment)
    {
        if (fragment.Equals("self", StringComparison.OrdinalIgnoreCase)) return GetConsoleWindow();
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var t = TitleOf(h);
            if (t.Length > 0 && t.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 找游戏窗口，**允许窗口处于最小化**。
    /// 与 <see cref="ResolveWindow"/> 的区别只在 <c>IsIconic</c> 这一条：采集必须用可见窗口
    /// （WGC 对最小化窗口出不了帧），但「游戏还在不在」这个判断不能用可见性 ——
    /// 用户 alt-tab 到别的程序、或把游戏最小化到任务栏，都不代表他退出了游戏，
    /// 那种情况下我们要继续保持采集会话，等他切回去接着录。
    /// </summary>
    public static IntPtr ResolveWindowAny(string fragment)
    {
        if (fragment.Equals("self", StringComparison.OrdinalIgnoreCase)) return GetConsoleWindow();
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var t = TitleOf(h);
            if (t.Length > 0 && t.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 托盘模式下把控制台窗口藏起来。
    /// 双击 exe 时 Windows 会先给一个黑色控制台窗口，那东西常驻着很难看；
    /// 但整个程序是 Console 类型（--lib-list 等自检命令要靠 stdout），
    /// 所以不能改成 WinExe，只能运行时藏窗口 —— 输出流不受影响，日志照写。
    /// </summary>
    public static void HideConsole() => SetConsoleVisible(false);

    /// <summary>
    /// 显示/隐藏控制台窗口。
    /// 注意：只有用经典 conhost 托管时这个句柄才是那个看得见的黑框
    /// （双击 .bat 就是这种）；如果是从 Windows Terminal 里的 cmd 启动，
    /// 可见的是 WT 自己的窗口，GetConsoleWindow() 拿到的是隐藏的伪窗口，这里就无效。
    /// </summary>
    public static void SetConsoleVisible(bool visible)
    {
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, visible ? 5 : 0);   // SW_SHOW / SW_HIDE
    }

    /// <summary>控制台现在是不是可见（没有控制台时为 false）。</summary>
    public static bool ConsoleVisible
    {
        get
        {
            var h = GetConsoleWindow();
            return h != IntPtr.Zero && IsWindowVisible(h);
        }
    }

    /// <summary>
    /// 把 CreateForWindow 的 HRESULT 翻成能直接照着做的提示。
    /// 0x80070005 最常见的成因不是代码问题，而是 exe 所在目录带着 Low 完整性标签
    /// （icacls 会显示 `Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)`）：
    /// 进程令牌随之降到 Low，而目标游戏窗口是 Medium，WGC 拒绝跨级别捕获。
    /// 实测过同一个 exe 从 Medium 目录跑正常、从 Low 目录跑必然 E_ACCESSDENIED。
    /// </summary>
    private static string HintFor(int hr)
    {
        var hex = $"CreateForWindow 失败 0x{hr:X8}";
        if (hr != unchecked((int)0x80070005)) return hex;
        return hex + "\n" +
               "  拒绝访问。请先检查 exe 所在目录的完整性标签：\n" +
               "    icacls \"<目录>\" | findstr /i \"Mandatory\"\n" +
               "  如果显示 Low Mandatory Level，把它改回 Medium 即可：\n" +
               "    icacls \"<目录>\" /setintegritylevel Medium\n" +
               "  （Low 目录会让进程令牌降到 Low，而游戏窗口是 Medium，WGC 不允许跨级别捕获。）";
    }

    private static IntPtr CallCreateForWindow(IntPtr self, IntPtr hwnd, Guid* iid)
    {
        var vtbl = (IntPtr*)Marshal.ReadIntPtr(self);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
        IntPtr result;
        var hr = fn(self, hwnd, iid, &result);
        if (hr < 0) throw new COMException(HintFor(hr));
        return result;
    }

    private static int GetTextureFromSurface(IntPtr self, ref Guid iid, out IntPtr result)
    {
        Guid g = iid;
        var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        var vtbl = (IntPtr*)Marshal.ReadIntPtr(self);
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        IntPtr access;
        var hrQi = qi(self, &accessIid, &access);
        if (hrQi < 0) { result = IntPtr.Zero; return hrQi; }
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

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
}
