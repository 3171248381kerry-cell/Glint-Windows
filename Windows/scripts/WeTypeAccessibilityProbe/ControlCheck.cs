using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;



internal static class ControlCheck
{
    internal static void DesktopCheck()
    {
        string? calling = DesktopName(GetCurrentThreadId());
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.Class(hwnd) != "WeType.NativeCandidate.Direct2D.v2") return true;
            uint thread = Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            if (process.ProcessName != "wetype_renderer") return true;
            string? target = DesktopName(thread);
            Console.WriteLine(JsonSerializer.Serialize(new { step = "desktop", querySucceeded = calling is not null && target is not null, sameDesktopName = calling is not null && calling == target }));
            return false;
        }, 0);
    }

    private static string? DesktopName(uint thread)
    {
        nint desktop = GetThreadDesktop(thread);
        if (desktop == 0) return null;
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : null;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder information, int length, out int needed);
}
