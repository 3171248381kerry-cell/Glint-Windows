using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;

namespace Glint.Windows.Platform;

/// <summary>
/// 微信输入法的候选接口属于目标窗口所在线程。这里只在看到它的候选窗后，
/// 短暂进入该线程读取当前高亮，随后立即卸下窗口过程钩子。
/// </summary>
internal static class WeTypeCandidateBridge
{
    private const string MappingName = @"Local\Glint.WeTypeCandidate.v1";
    private const int HookCallWindowProcedure = 4;
    private static readonly object Gate = new();
    private static MemoryMappedFile? mapping;
    private static MemoryMappedViewAccessor? view;
    private static nint module;
    private static nint procedure;
    private static uint armedThread;
    private static long nextShuttle;
    private static long focusTick;
    private static bool focusOk;
    private static nint cachedForeground;
    private static string cachedIdentity = "";
    private static nint cachedFocus;

    internal static void Allow(bool allowed)
    {
        var accessor = View();
        if (accessor is null) return;
        accessor.Write(WeTypeSnapshot.AllowTick, allowed ? unchecked((uint)Environment.TickCount) : 0u);
        accessor.Flush();
    }

    internal static (nint Handle, Rect Bounds)? FindWindow()
    {
        (nint Handle, Rect Bounds)? found = null;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.WindowClass(hwnd) != "WeType.NativeCandidate.Direct2D.v2") return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals("wetype_renderer", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
            int width = rect.right - rect.left, height = rect.bottom - rect.top;
            if (width is > 0 and < 2400 && height is > 0 and < 900)
            {
                rect = NativeMethods.VisibleWindowRect(hwnd, rect);
                found = (hwnd, new Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top));
            }
            return found is null;
        }, 0);
        return found;
    }

    internal static bool TryGetFocus(out nint foreground, out string identity, out nint focusWindow)
    {
        foreground = NativeMethods.GetForegroundWindow();
        double cacheMs = focusOk ? 200 : 100;
        if (foreground == cachedForeground && foreground != 0 &&
            Stopwatch.GetElapsedTime(focusTick).TotalMilliseconds < cacheMs)
        {
            identity = cachedIdentity;
            focusWindow = cachedFocus;
            return focusOk;
        }
        focusOk = NativeCandidateReader.TryGetSafeFocus(allowImeSelection: true, out cachedForeground, out cachedIdentity, out cachedFocus)
            || WindowFocus(out cachedForeground, out cachedIdentity, out cachedFocus);
        focusTick = Stopwatch.GetTimestamp();
        foreground = cachedForeground;
        identity = cachedIdentity;
        focusWindow = cachedFocus;
        return focusOk && foreground != 0;
    }

    private static bool WindowFocus(out nint foreground, out string identity, out nint focusWindow)
    {
        foreground = NativeMethods.GetForegroundWindow();
        identity = "";
        focusWindow = 0;
        if (foreground == 0 || NativeCandidateReader.FocusedElementIsPassword()) return false;
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId || !NativeMethods.IsOrdinaryProcess(processId)) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (process.ProcessName is "WindowsTerminal" or "OpenConsole" or "conhost" or "cmd" or "powershell" or "pwsh" or "mintty") return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        var gui = new NativeMethods.GUITHREADINFO { size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref gui) || gui.focus == 0) return false;
        NativeMethods.GetWindowThreadProcessId(gui.focus, out uint focusProcess);
        if (focusProcess != processId && NativeMethods.GetAncestor(gui.focus, 2) != foreground) return false;
        string cls = NativeMethods.WindowClass(gui.focus);
        if (cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") return false;
        if ((NativeMethods.GetWindowLongPtr(gui.focus, NativeMethods.GwlStyle) & NativeMethods.EsPassword) != 0) return false;
        focusWindow = gui.focus;
        identity = "hwnd." + gui.focus.ToInt64();
        return NativeMethods.GetForegroundWindow() == foreground;
    }

    internal static CandidateSnapshot? TryRead(Rect bounds, nint foreground, string identity, nint focusWindow)
    {
        uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        if (threadId == 0 || processId == 0) return null;
        if (!Fresh(processId, threadId)) Shuttle(threadId, focusWindow != 0 ? focusWindow : foreground);
        if (!TryCopy(processId, threadId, out string text)) return null;
        if (NativeMethods.GetForegroundWindow() != foreground) return null;
        return new CandidateSnapshot(text, bounds, foreground, 0, "微信输入法")
        {
            FocusIdentity = identity,
            FocusWindow = focusWindow,
            InputLine = NativeCandidateReader.RecentInputLine
        };
    }

    internal static void CheckModule()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "WeTypeSink.dll");
        if (!File.Exists(path)) throw new InvalidOperationException("缺少微信输入法桥接模块");
        nint loaded = LoadLibrary(path);
        if (loaded == 0 || GetProcAddress(loaded, "GlintWeTypeCallWndProc") == 0)
            throw new InvalidOperationException("微信输入法桥接模块无法加载");
    }

    private static bool Fresh(uint processId, uint threadId)
    {
        var accessor = View();
        if (accessor is null) return false;
        var bytes = new byte[WeTypeSnapshot.Size];
        int read = accessor.ReadArray(0, bytes, 0, bytes.Length);
        if (read < bytes.Length) return false;
        uint age = unchecked((uint)Environment.TickCount - WeTypeSnapshot.ReadTick(bytes));
        int sequence = BitConverter.ToInt32(bytes, 0);
        uint snapshotProcess = BitConverter.ToUInt32(bytes, 4);
        uint snapshotThread = BitConverter.ToUInt32(bytes, 8);
        return (sequence & 1) == 0 && snapshotProcess == processId && snapshotThread == threadId && age < 300;
    }

    private static bool TryCopy(uint processId, uint threadId, out string text)
    {
        text = "";
        var accessor = View();
        if (accessor is null) return false;
        var bytes = new byte[WeTypeSnapshot.Size];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int read = accessor.ReadArray(0, bytes, 0, bytes.Length);
            if (read < bytes.Length) return false;
            if (WeTypeSnapshot.TryDecode(bytes, processId, threadId, out text)) return true;
        }
        return false;
    }

    private static void Shuttle(uint threadId, nint window)
    {
        long now = Stopwatch.GetTimestamp();
        if (armedThread == threadId && now < nextShuttle) return;
        armedThread = threadId;
        nextShuttle = now + Stopwatch.Frequency / 10;
        if (!EnsureModule()) return;
        nint hook = SetWindowsHookEx(HookCallWindowProcedure, procedure, module, threadId);
        if (hook == 0) return;
        try { _ = SendMessageTimeout(window, 0, 0, 0, 2, 80, out _); }
        finally { _ = UnhookWindowsHookEx(hook); }
    }

    private static bool EnsureModule()
    {
        if (procedure != 0) return true;
        string path = Path.Combine(AppContext.BaseDirectory, "WeTypeSink.dll");
        if (!File.Exists(path)) return false;
        module = LoadLibrary(path);
        if (module == 0) return false;
        procedure = GetProcAddress(module, "GlintWeTypeCallWndProc");
        return procedure != 0;
    }

    private static MemoryMappedViewAccessor? View()
    {
        lock (Gate)
        {
            if (view is not null) return view;
            try
            {
                mapping = MemoryMappedFile.CreateOrOpen(MappingName, 512, MemoryMappedFileAccess.ReadWrite);
                view = mapping.CreateViewAccessor(0, 512, MemoryMappedFileAccess.ReadWrite);
                return view;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, nint procedure, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
