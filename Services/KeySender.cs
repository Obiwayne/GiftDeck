using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace GiftDeck.Services;

// Sends keyboard shortcuts like "Ctrl+Shift+Z" to the foreground window.
public static class KeySender
{
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    // INPUT is a union; pad to the size of the largest member (MOUSEINPUT) on x64.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    static readonly Dictionary<string, Key> Aliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Key.LeftCtrl, ["control"] = Key.LeftCtrl, ["lctrl"] = Key.LeftCtrl, ["rctrl"] = Key.RightCtrl,
        ["shift"] = Key.LeftShift, ["lshift"] = Key.LeftShift, ["rshift"] = Key.RightShift,
        ["alt"] = Key.LeftAlt, ["lalt"] = Key.LeftAlt, ["ralt"] = Key.RightAlt,
        ["win"] = Key.LWin, ["windows"] = Key.LWin,
        ["esc"] = Key.Escape, ["enter"] = Key.Return, ["backspace"] = Key.Back, ["del"] = Key.Delete, ["ins"] = Key.Insert,
        ["pgup"] = Key.PageUp, ["pgdn"] = Key.PageDown, ["pagedown"] = Key.PageDown, ["pageup"] = Key.PageUp,
        ["spacebar"] = Key.Space, ["plus"] = Key.OemPlus, ["minus"] = Key.OemMinus, ["comma"] = Key.OemComma, ["period"] = Key.OemPeriod,
        ["0"] = Key.D0, ["1"] = Key.D1, ["2"] = Key.D2, ["3"] = Key.D3, ["4"] = Key.D4,
        ["5"] = Key.D5, ["6"] = Key.D6, ["7"] = Key.D7, ["8"] = Key.D8, ["9"] = Key.D9,
    };

    public static bool TryParse(string combo, out List<Key> keys, out string error)
    {
        keys = new List<Key>();
        error = null;
        if (string.IsNullOrWhiteSpace(combo)) { error = "No key set"; return false; }
        foreach (var raw in combo.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            Key k;
            if (!Aliases.TryGetValue(token, out k))
            {
                if (token.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && Enum.TryParse("NumPad" + token.Substring(6), true, out k)) { }
                else if (!Enum.TryParse(token, true, out k)) { error = "Unknown key: " + token; return false; }
            }
            keys.Add(k);
        }
        if (keys.Count == 0) { error = "No key set"; return false; }
        return true;
    }

    public static void Send(string combo, int holdMs)
    {
        if (!TryParse(combo, out var keys, out var error)) throw new Exception(error);
        var vks = keys.Select(k => (ushort)KeyInterop.VirtualKeyFromKey(k)).ToList();

        var down = vks.Select(vk => Make(vk, false)).ToArray();
        uint sent = SendInput((uint)down.Length, down, Marshal.SizeOf(typeof(INPUT)));
        if (sent != down.Length)
            throw new Exception($"Windows refused the key press (error {Marshal.GetLastWin32Error()})");
        Thread.Sleep(Math.Clamp(holdMs, 10, 5000));
        var up = vks.AsEnumerable().Reverse().Select(vk => Make(vk, true)).ToArray();
        SendInput((uint)up.Length, up, Marshal.SizeOf(typeof(INPUT)));
    }

    static INPUT Make(ushort vk, bool up) => new INPUT
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    // Builds a combo string from a key event, for the "press keys" capture box. Returns null for a lone modifier.
    public static string FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin || key == Key.None)
            return null;
        var sb = new StringBuilder();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(Friendly(key));
        return sb.ToString();
    }

    static string Friendly(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
        switch (key)
        {
            case Key.Return: return "Enter";
            case Key.Escape: return "Esc";
            case Key.Back: return "Backspace";
            case Key.OemPlus: return "Plus";
            case Key.OemMinus: return "Minus";
            case Key.OemComma: return "Comma";
            case Key.OemPeriod: return "Period";
        }
        return key.ToString();
    }
}

public static class WindowFocus
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    public static string ForegroundTitle()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool Focus(string titlePart)
    {
        if (string.IsNullOrWhiteSpace(titlePart)) return false;
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(512);
        // Spaces don't count, so "TheForest" finds "The Forest" and the other way round.
        var want = titlePart.Replace(" ", "");
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Clear();
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length > 0 && sb.ToString().Replace(" ", "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        if (GetForegroundWindow() == found) return true;
        if (IsIconic(found)) ShowWindow(found, 9);
        SetForegroundWindow(found);
        Thread.Sleep(150);
        if (GetForegroundWindow() == found) return true;
        // Windows blocks foreground changes from background processes; borrow the foreground thread's input queue.
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint myThread = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != myThread)
        {
            AttachThreadInput(myThread, fgThread, true);
            try { SetForegroundWindow(found); }
            finally { AttachThreadInput(myThread, fgThread, false); }
            Thread.Sleep(150);
        }
        return GetForegroundWindow() == found;
    }

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
