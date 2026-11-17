using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace Glint.Windows.Platform;

/// <summary>MSAA fallback restricted to positively identified native IME candidate HWNDs.</summary>
internal static class LegacyCandidateReader
{
    private static bool IsCandidateWindow(nint hwnd)
    {
        string cls = NativeMethods.WindowClass(hwnd);
        if (cls is "WeType.NativeCandidate.Direct2D.v2" or "Microsoft.IME.UIManager.CandidateWindow.Host" or "IME_Candidate_Window") return true;
        // 上次诊断把 CoreWindow 的 msaa.available=false 当成未查询。这里只探测输入宿主自己的可见窗口。
        if (cls != "Windows.UI.Core.CoreWindow") return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName is "TextInputHost" or "InputApp";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static object Metadata(nint hwnd)
    {
        if (!IsCandidateWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return new { available = false };
        try
        {
            var accessible = GetClient(hwnd);
            if (accessible is null) return new { available = false };
            var roles = new List<int>();
            int children = accessible.accChildCount;
            for (int child = 1; child <= children && child <= 12; child++)
            {
                try { roles.Add(Convert.ToInt32(accessible.get_accRole(child))); }
                catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { }
            }
            return new { available = true, children, hasSelection = accessible.accSelection is not null, roles };
        }
        catch (COMException error) { return new { available = false, code = error.HResult }; }
    }

    internal static CandidateSnapshot? Read(nint hwnd, nint foreground, string identity, nint focusWindow)
    {
        if (!IsCandidateWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return null;
        try
        {
            var root = GetClient(hwnd);
            if (root is null) return null;
            object? selection = root.accSelection;
            Accessibility.IAccessible selected;
            object child;
            if (selection is int childId && childId > 0) { selected = root; child = childId; }
            else if (selection is Accessibility.IAccessible element) { selected = element; child = 0; }
            else return null;
            int state = Convert.ToInt32(selected.get_accState(child));
            if ((state & 0x2) == 0 || (state & (0x8000 | 0x10000)) != 0) return null; // selected, visible, onscreen
            // CoreWindow 也可能是触摸键盘。只接受列表项、菜单项或文本角色，避免把按钮当成候选。
            if (NativeMethods.WindowClass(hwnd) == "Windows.UI.Core.CoreWindow")
            {
                int role = Convert.ToInt32(selected.get_accRole(child));
                if (role is not (0x22 or 0x0C or 0x29)) return null;
            }
            string text = Regex.Replace(selected.get_accName(child) ?? "", @"^\s*\d{1,2}[.、\s]*", "").Trim();
            if (text.Length is 0 or > 120 || text.Any(char.IsControl) || !text.Any(c => c is >= '\u3400' and <= '\u9fff')) return null;
            root.accLocation(out int x, out int y, out int width, out int height, 0);
            if (width is <= 0 or > 5000 || height is <= 0 or > 1200 || NativeMethods.GetForegroundWindow() != foreground) return null;
            return new CandidateSnapshot(text, new Rect(x, y, width, height), foreground, 0, "Windows 原生候选 / MSAA") { FocusIdentity = identity, FocusWindow = focusWindow };
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { return null; }
    }

    private static Accessibility.IAccessible? GetClient(nint hwnd)
    {
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        return AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref iid, out var accessible) >= 0 ? accessible : null;
    }
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible);
}
