using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Glint.Windows.Platform;

/// <summary>Opt-in diagnostic: metadata for WinEvents from the WeType candidate window only.</summary>
internal sealed class WeTypeEventDiagnostics : IDisposable
{
    private readonly ConcurrentQueue<object> events = new();
    private readonly ManualResetEventSlim started = new();
    private readonly Thread thread;
    private Dispatcher? dispatcher;
    private WinEventProc? callback;
    internal string Status { get; private set; } = "starting";
    internal object[] Snapshot() => events.ToArray();

    internal WeTypeEventDiagnostics()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Glint WeType event diagnostics" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Wait(2000);
    }

    private void Run()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        callback = OnEvent;
        nint hook = SetWinEventHook(0x8000, 0x8018, 0, callback, 0, 0, 0);
        Status = hook == 0 ? "registration failed" : "listening";
        started.Set();
        if (hook == 0) return;
        Dispatcher.Run();
        UnhookWinEvent(hook);
        GC.KeepAlive(callback);
    }

    private void OnEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint eventThread, uint time)
    {
        if (hwnd == 0 || NativeMethods.WindowClass(hwnd) != "WeType.NativeCandidate.Direct2D.v2") return;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!string.Equals(process.ProcessName, "wetype_renderer", StringComparison.OrdinalIgnoreCase)) return;
            int? role = null, state = null;
            int result = AccessibleObjectFromEvent(hwnd, objectId, childId, out var accessible, out var child);
            if (result >= 0 && accessible is not null)
            {
                try { role = Convert.ToInt32(accessible.get_accRole(child)); } catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { }
                try { state = Convert.ToInt32(accessible.get_accState(child)); } catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { }
            }
            events.Enqueue(new { eventId, objectId, childId, result, role, state });
            while (events.Count > 128) events.TryDequeue(out _);
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        thread.Join(2000);
    }

    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint eventThread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromEvent(nint hwnd, int objectId, int childId, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
}
