using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace ApexHighlights;

/// <summary>
/// 关掉 Windows 11 画在录制窗口外面的那条黄框。
///
/// 这条框是系统（DWM）给「正在被采集」的窗口画的提示，不是我们画的，也不在录下来的画面里
/// （WGC 只抓窗口内容）。想让它消失只有一条官方通道：
/// 包清单里声明 graphicsCaptureWithoutBorder 能力 → 运行时向用户要一次授权 →
/// 用户同意后把 GraphicsCaptureSession.IsBorderRequired 设成 false。
///
/// 所以前提是程序有「包身份」——绿色 exe 没有包清单，RequestAccessAsync 会返回
/// NotDeclaredByApp，这里就安静地放弃（黄框照旧，别的功能一点不受影响）。
/// 便携版的用户拿到的就是这种状态，这是设计如此，不是 bug。
/// </summary>
internal static class BorderlessAccess
{
    private const int Unknown = 0, Allowed = 1, Denied = 2;

    private static int _state = Unknown;

    /// <summary>
    /// 要不要现在就给一条采集会话关框。返回 false 就什么都不做（框留着）。
    /// 只在第一次调用时问系统，之后走缓存 —— 用户点过一次「允许」就不再打扰。
    /// </summary>
    public static bool TryEnable(GraphicsCaptureSession session)
    {
        // 先在最外层判版本再往下走：IsBorderRequired / GraphicsCaptureAccess 都是
        // Windows 11 22H2 才有的东西，在 Windows 10 上连碰都不要碰（类型/激活都可能炸）。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            if (_state == Unknown)
            {
                _state = Denied;
                Log.Write("[capture] Windows 10 没有关框 API，黄框会一直在（系统画的，程序关不掉）");
            }
            return false;
        }

        return TryEnableWin11(session);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]   // 别让 Win10 的 JIT 顺手把 Win11 的类型也加载了
    private static bool TryEnableWin11(GraphicsCaptureSession session)
    {
        if (!EnsureAllowed()) return false;
        try
        {
            session.IsBorderRequired = false;
            Log.Write("[capture] 已关掉录制窗口的黄框（包身份 + 用户已授权无边框采集）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("[capture] 关黄框失败（不影响录制）: " + ex.Message);
            _state = Denied;
            return false;
        }
    }

    private static bool EnsureAllowed()
    {
        if (_state != Unknown) return _state == Allowed;

        try
        {
            // 这一步会弹一次系统授权框（只有第一次），用户点过同意之后直接返回 Allowed
            var task = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            if (!task.Wait(15000))
            {
                Log.Write("[capture] 等无边框采集授权超时，黄框保留");
                _state = Denied;
                return false;
            }

            switch (task.Result)
            {
                case AppCapabilityAccessStatus.Allowed:
                    _state = Allowed;
                    Log.Write("[capture] 无边框采集授权：已允许");
                    return true;

                case AppCapabilityAccessStatus.NotDeclaredByApp:
                    // 绿色版（没有包身份）走这里：能力根本没声明过，系统连问都不会问
                    Log.Write("[capture] 这个版本没有包身份，黄框关不掉（便携版正常现象）");
                    _state = Denied;
                    return false;

                case AppCapabilityAccessStatus.DeniedByUser:
                    Log.Write("[capture] 用户拒绝过无边框采集授权，黄框保留（可在 Windows 设置里改）");
                    _state = Denied;
                    return false;

                default:
                    Log.Write($"[capture] 无边框采集授权状态 {task.Result}，黄框保留");
                    _state = Denied;
                    return false;
            }
        }
        catch (Exception ex)
        {
            // 没装包身份时这里也可能直接抛（类根本没在系统里注册），当作「不支持」
            Log.Write("[capture] 问无边框采集授权出错，黄框保留: " + ex.Message);
            _state = Denied;
            return false;
        }
    }
}
