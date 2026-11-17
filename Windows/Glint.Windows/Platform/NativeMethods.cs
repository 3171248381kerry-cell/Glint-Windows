using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace Glint.Windows.Platform;

internal static class NativeMethods
{
    internal const uint InputKeyboard = 1;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint KeyeventfUnicode = 0x0004;
    internal const ushort VkEscape = 0x1B;
    internal const uint ModNoRepeat = 0x4000;
    internal const int WmHotkey = 0x0312;

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    internal const int GwlStyle = -16;
    internal const int EsPassword = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    // GetWindowRect may include the invisible resize border. Prefer the compositor's
    // visible frame when it is a valid rectangle inside the original window.
    internal static RECT VisibleWindowRect(nint hwnd, RECT window)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out var frame, Marshal.SizeOf<RECT>()) == 0 &&
            frame.left >= window.left && frame.top >= window.top &&
            frame.right <= window.right && frame.bottom <= window.bottom &&
            frame.right > frame.left && frame.bottom > frame.top)
            return frame;
        return window;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref POINT point);

    /// <summary>微信候选栏旁的原文行。组字窗口、插入符，或同一线程里贴着候选栏的单行焦点。</summary>
    internal static bool TryGetWeChatTextLine(Rect candidate, out Rect line)
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out uint _);
        Rect closest = Rect.Empty;
        double distance = double.MaxValue;
        void Consider(Rect box)
        {
            if (!IsBesideCandidate(candidate, box)) return;
            double delta = Math.Abs(box.Top + box.Height / 2 - (candidate.Top + candidate.Height / 2));
            if (delta < distance) { distance = delta; closest = box; }
        }
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            string cls = WindowClass(hwnd);
            if (cls.StartsWith("WeType.", StringComparison.Ordinal) || cls.Contains("Candidate", StringComparison.Ordinal)) return true;
            if (GetWindowThreadProcessId(hwnd, out uint pid) != thread || pid == (uint)Environment.ProcessId || !GetWindowRect(hwnd, out var rect)) return true;
            int width = rect.right - rect.left, height = rect.bottom - rect.top;
            if (width is <= 0 or > 1200 || height is < 8 or > 80) return true;
            Consider(new Rect(rect.left, rect.top, width, height));
            return true;
        }, 0);
        if (TryGetForegroundCaret(out var caret)) Consider(caret);
        var gui = new GUITHREADINFO { size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref gui) && gui.focus != 0 && GetWindowRect(gui.focus, out var focus))
        {
            int width = focus.right - focus.left, height = focus.bottom - focus.top;
            if (width is > 20 and < 2400 && height is >= 8 and <= 80)
                Consider(new Rect(focus.left, focus.top, width, height));
        }
        if (distance < 360) { line = closest; return true; }
        line = Rect.Empty;
        return false;
    }

    private static bool IsBesideCandidate(Rect candidate, Rect line)
    {
        if (line.Height is < 8 or > 80 || line.Width > 1600) return false;
        if (line.Right < candidate.Left - 80 || line.Left > candidate.Right + 240) return false;
        double mid = line.Top + line.Height / 2;
        bool above = mid < candidate.Top + 8 && candidate.Top - line.Bottom < 280;
        bool below = mid > candidate.Bottom - 8 && line.Top - candidate.Bottom < 280;
        return above || below;
    }

    /// <summary>插入符、当前线程的组字窗口，或足够小的焦点控件。用来判断输入行在候选栏的哪一侧。</summary>
    internal static bool TryGetInputLine(Rect candidate, out Rect line)
    {
        if (TryGetForegroundCaret(out line)) return true;
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out uint _);
        Rect closest = Rect.Empty;
        double distance = double.MaxValue;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || WindowClass(hwnd) != "MSCTFIME Composition") return true;
            if (GetWindowThreadProcessId(hwnd, out uint _) != thread || !GetWindowRect(hwnd, out var rect)) return true;
            int width = rect.right - rect.left, height = rect.bottom - rect.top;
            if (width is <= 0 or > 1200 || height is < 8 or > 160) return true;
            var box = new Rect(rect.left, rect.top, width, height);
            double delta = Math.Abs(box.Top + box.Height / 2 - (candidate.Top + candidate.Height / 2));
            if (delta < distance) { distance = delta; closest = box; }
            return true;
        }, 0);
        if (distance < 600) { line = closest; return true; }
        var gui = new GUITHREADINFO { size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref gui) && gui.focus != 0 && GetWindowRect(gui.focus, out var focus))
        {
            int width = focus.right - focus.left, height = focus.bottom - focus.top;
            if (width is > 20 and < 2400 && height is >= 8 and <= 72)
            {
                line = new Rect(focus.left, focus.top, width, height);
                return true;
            }
        }
        line = Rect.Empty;
        return false;
    }

    /// <summary>前台线程的插入符，转成屏幕像素。读不到或范围不像一行文字时返回 false。</summary>
    internal static bool TryGetForegroundCaret(out Rect caret)
    {
        caret = Rect.Empty;
        var info = new GUITHREADINFO { size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(0, ref info) || info.caret == 0) return false;
        int width = info.caretRect.right - info.caretRect.left;
        int height = info.caretRect.bottom - info.caretRect.top;
        if (width is < 0 or > 800 || height is < 8 or > 160) return false;
        var origin = new POINT { x = info.caretRect.left, y = info.caretRect.top };
        var end = new POINT { x = info.caretRect.right, y = info.caretRect.bottom };
        if (!ClientToScreen(info.caret, ref origin) || !ClientToScreen(info.caret, ref end)) return false;
        if (end.y <= origin.y) return false;
        caret = new Rect(origin.x, origin.y, Math.Max(0, end.x - origin.x), end.y - origin.y);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    internal static string WindowClass(nint hwnd)
    {
        var builder = new StringBuilder(256);
        return GetClassName(hwnd, builder, builder.Capacity) == 0 ? "" : builder.ToString();
    }

    internal static bool ModifiersAreReleased() =>
        (GetAsyncKeyState(0x10) & 0x8000) == 0 && // Shift
        (GetAsyncKeyState(0x11) & 0x8000) == 0 && // Control
        (GetAsyncKeyState(0x12) & 0x8000) == 0 && // Alt
        (GetAsyncKeyState(0x5B) & 0x8000) == 0 &&
        (GetAsyncKeyState(0x5C) & 0x8000) == 0;

    /// <summary>Reject elevated, protected and unknown-integrity foreground processes.</summary>
    internal static bool IsOrdinaryProcess(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) return false;
        using (token)
        {
            if (!GetTokenInformation(token, 20, out var elevated, sizeof(uint), out _) || elevated != 0) return false;
            _ = GetTokenInformationBuffer(token, 25, 0, 0, out var size); // TokenIntegrityLevel
            if (size is < 1 or > 4096) return false;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformationBuffer(token, 25, buffer, size, out _)) return false;
                var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                if (sid == 0 || !IsValidSid(sid)) return false;
                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == 0) return false;
                var count = Marshal.ReadByte(countPointer);
                if (count == 0) return false;
                var authority = GetSidSubAuthority(sid, (uint)(count - 1));
                // SECURITY_MANDATORY_MEDIUM_RID = 0x2000. Medium-plus/UIAccess,
                // high, system and protected-process levels are deliberately excluded.
                return authority != 0 && (uint)Marshal.ReadInt32(authority) <= 0x2000;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, uint length, out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int informationClass, nint information, uint length, out uint returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);

    internal static INPUT Key(ushort virtualKey, bool keyUp = false) => new()
    {
        type = InputKeyboard,
        data = new INPUTUNION
        {
            keyboard = new KEYBDINPUT { virtualKey = virtualKey, flags = keyUp ? KeyeventfKeyup : 0 }
        }
    };

    internal static INPUT Unicode(char character, bool keyUp = false) => new()
    {
        type = InputKeyboard,
        data = new INPUTUNION
        {
            keyboard = new KEYBDINPUT
            {
                scanCode = character,
                flags = KeyeventfUnicode | (keyUp ? KeyeventfKeyup : 0)
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint type;
        internal INPUTUNION data;
    }

    // Including MOUSEINPUT gives INPUT its required 40-byte size on x64 (28 on x86).
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] internal KEYBDINPUT keyboard;
        [FieldOffset(0)] internal MOUSEINPUT mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort virtualKey;
        internal ushort scanCode;
        internal uint flags;
        internal uint time;
        internal nuint extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx;
        internal int dy;
        internal uint mouseData;
        internal uint flags;
        internal uint time;
        internal nuint extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        internal uint size;
        internal uint flags;
        internal nint active, focus, capture, menuOwner, moveSize, caret;
        internal RECT caretRect;
    }
}
