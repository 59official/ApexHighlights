using System.Runtime.InteropServices;

namespace ApexHighlights;

/// <summary>
/// 用 Windows 外壳的缩略图来显示预览帧。
///
/// 为什么不自己解码：录制时 NVENC 已经占着 GPU 和一批 MFT 实例，
/// 再给列表里的每片起一个 source reader 解码首帧，既慢又会跟录制抢资源。
/// 外壳缩略图是 Explorer 自己用的那条路径，有缓存、跟录制完全不冲突。
/// </summary>
internal static class ShellThumb
{
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_THUMBNAILONLY = 0x08;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    /// <summary>取一张缩略图；失败返回 null（调用方显示占位文字，绝不抛）。</summary>
    public static Bitmap? Get(string path, int width, int height)
    {
        object? item = null;
        try
        {
            // 必须绝对路径：SHCreateItemFromParsingName 不走进程 CWD 解析，
            // 相对路径会直接失败，而且失败得很安静（返回 null 而不是抛）。
            // 这个坑让整个列表的缩略图空白过一次，所以在这里兜底转绝对。
            path = Path.GetFullPath(path);
            SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out item);
            if (item is not IShellItemImageFactory f) return null;
            int hr = f.GetImage(new SIZE { cx = width, cy = height },
                                 SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK,
                                 out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero)
            {
                // 没缩略图时退回「只缩放」的图标，至少给个能看的方块
                hr = f.GetImage(new SIZE { cx = width, cy = height }, SIIGBF_RESIZETOFIT, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
            }
            try { return Image.FromHbitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }
}
