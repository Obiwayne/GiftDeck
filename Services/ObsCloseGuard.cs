using System.Runtime.InteropServices;
using System.Windows;

namespace GiftDeck.Services;

// While OBS is open for editing, its X should put it back in the tray, not quit it (quitting drops the
// connection and takes ~15 s). OBS has no "close to tray" setting, so GiftDeck watches the mouse while
// the OBS window is out: a click on that window's close button is swallowed and hides OBS instead.
// Only the close button of OBS's own main window is touched; everything else passes straight through.
public static class ObsCloseGuard
{
    static IntPtr _hook, _obsWindow;
    static readonly LowLevelMouseProc Proc = HookProc; // kept alive: Windows calls it for as long as the hook exists
    static bool _swallowingDown;

    // Call on the UI thread (the hook needs a thread with a message loop).
    public static void Watch(IntPtr obsWindow)
    {
        _obsWindow = obsWindow;
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, Proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("Couldn't watch OBS's close button (error " + Marshal.GetLastWin32Error() + ")");
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _obsWindow = IntPtr.Zero;
        _swallowingDown = false;
    }

    static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _obsWindow != IntPtr.Zero)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool onClose = OverCloseButton(info.pt);
                if (msg == WM_LBUTTONDOWN && onClose) { _swallowingDown = true; return (IntPtr)1; }
                if (msg == WM_LBUTTONUP && _swallowingDown)
                {
                    _swallowingDown = false;
                    if (onClose)
                    {
                        Log.Write("OBS's close button: putting OBS back in the tray instead");
                        Stop();
                        _ = Task.Run(() => Hub.Engine.HideWindow());
                    }
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool OverCloseButton(POINT pt)
    {
        var under = WindowFromPoint(pt);
        if (under == IntPtr.Zero || GetAncestor(under, GA_ROOT) != _obsWindow) return false;
        var lp = (IntPtr)(((pt.y & 0xFFFF) << 16) | (pt.x & 0xFFFF));
        if (SendMessageTimeout(_obsWindow, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 50, out var hit) == IntPtr.Zero) return false;
        return (long)hit == HTCLOSE;
    }

    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const uint WM_NCHITTEST = 0x0084, SMTO_ABORTIFHUNG = 0x0002, GA_ROOT = 2;
    const long HTCLOSE = 20;

    delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
