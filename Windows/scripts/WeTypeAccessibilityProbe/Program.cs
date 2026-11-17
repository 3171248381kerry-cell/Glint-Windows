using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using U = Glint.Windows.Interop.UIA;

// 独立探针，仅在微信输入法可见候选窗内查询接口和文字样式，不读取 Name/Value/GetText。
internal static class Program
{
    private static U.IUIAutomation client = null!;
    private static readonly Dictionary<string, int> Observations = new();
    private static readonly Stopwatch Timer = new();
    private static int visibleSamples;
    private static readonly HashSet<uint> TokenSeen = new();

    [MTAThread]
    private static int Main(string[] args)
    {
        // UIA 使用物理屏幕坐标；在创建任何窗口或 COM 对象前关闭 DPI 坐标虚拟化。
        bool dpiSet = Native.SetProcessDpiAwarenessContext(new nint(-4));
        Console.WriteLine(JsonSerializer.Serialize(new { step = "dpi", perMonitorRequested = dpiSet, awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext()) }));
        if (args.Contains("--access-only"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { source = "probe", token = AccessDiagnostics.Inspect(Environment.ProcessId) }));
            foreach (string name in new[] { "wetype_renderer", "wetype_server", "Notepad", "Glint" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) Console.WriteLine(JsonSerializer.Serialize(new { source = name, token = AccessDiagnostics.Inspect(process.Id) }));
            ControlCheck.DesktopCheck();
            return 0;
        }
        int seconds = args.Length > 0 && int.TryParse(args[0], out int value) ? Math.Clamp(value, 5, 90) : 45;
        client = (U.IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E22AD333-B25F-460C-83D0-0581107395C9"), true)!)!;
        if (client is U.IUIAutomation2 settings) { settings.ConnectionTimeout = 200; settings.TransactionTimeout = 200; }
        Timer.Start();
        Record(new { step = "probeToken", token = AccessDiagnostics.Inspect(Environment.ProcessId) });
        ControlCheck.DesktopCheck();
        while (Timer.Elapsed.TotalSeconds < seconds)
        {
            CheckNotepadPoint();
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd) || Native.Class(hwnd) is not ("WeType.NativeCandidate.Decoration.v2" or "WeType.NativeCandidate.Direct2D.v2")) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    if (process.ProcessName != "wetype_renderer") return true;
                    if (Native.Class(hwnd) == "WeType.NativeCandidate.Direct2D.v2") Probe(hwnd, pid);
                    Native.EnumChildWindows(hwnd, (child, _) =>
                    {
                        if (Native.IsWindowVisible(child) && Native.Class(child) == "WeType.NativeCandidate.Direct2D.v2") Probe(child, pid);
                        return true;
                    }, 0);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or COMException)
                { Record(new { step = "window", hr = error.HResult }); }
                return true;
            }, 0);
            Thread.Sleep(350);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { summary = true, visibleSamples, elapsedMs = Timer.ElapsedMilliseconds, shapes = Observations.Select(x => new { metadata = x.Key, samples = x.Value }) }));
        return visibleSamples > 0 ? 0 : 2;
    }

    private static void Probe(nint hwnd, uint pid)
    {
        if (TokenSeen.Add(pid)) Record(new { step = "candidateToken", token = AccessDiagnostics.Inspect((int)pid) });
        // 点位只来自已确认的候选窗矩形；命中对象必须证明属于该 HWND 及进程。
        if (!Native.GetWindowRect(hwnd, out var rect) || rect.right <= rect.left || rect.bottom <= rect.top) return;
        visibleSamples++;
        try { Inspect(client.ElementFromHandle(hwnd), "window", hwnd, pid); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException) { Record(new { step = "ElementFromHandle", hr = error.HResult }); }
        foreach (double fraction in new[] { .1, .3, .5, .7, .9 })
        {
            var point = new U.tagPOINT { x = rect.left + (int)((rect.right - rect.left) * fraction), y = rect.top + (rect.bottom - rect.top) / 2 };
            nint hit = Native.WindowFromPoint(new Native.Point { x = point.x, y = point.y });
            if (hit != hwnd && !Native.IsChild(hwnd, hit)) continue;
            try { Inspect(client.ElementFromPoint(point), "point", hwnd, pid); }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException) { Record(new { step = "ElementFromPoint", hr = error.HResult }); }
            int hr = Native.AccessibleObjectFromPoint(new Native.Point { x = point.x, y = point.y }, out var accessible, out var child);
            if (hr >= 0 && accessible is not null)
            {
                // WindowFromAccessibleObject 再次核验归属，不沿用仅凭屏幕坐标的假设。
                Native.WindowFromAccessibleObject(accessible, out nint owner);
                if (owner == hwnd || Native.IsChild(hwnd, owner)) InspectAccessible(accessible, child, "msaaPoint");
            }
            else Record(new { step = "AccessibleObjectFromPoint", hr });
        }
        var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
        int clientHr = Native.AccessibleObjectFromWindow(hwnd, 0xfffffffc, ref iid, out var root);
        if (clientHr >= 0 && root is not null) InspectAccessible(root, 0, "msaaClient");
        else Record(new { step = "AccessibleObjectFromWindow", hr = clientHr });
    }

    private static void CheckNotepadPoint()
    {
        // 只查询记事本标题栏点位的结构属性作同级权限对照，不读取输入框正文。
        nint hwnd = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName != "Notepad" || !Native.GetWindowRect(hwnd, out var rect)) return;
            var point = new Native.Point { x = (rect.left + rect.right) / 2, y = rect.top + 12 };
            nint hit = Native.WindowFromPoint(point);
            Native.GetWindowThreadProcessId(hit, out uint hitPid);
            if (hitPid != pid) return;
            try
            {
                var element = client.ElementFromPoint(new U.tagPOINT { x = point.x, y = point.y });
                Record(new { step = "NotepadTitle.ElementFromPoint", hr = 0, sameProcess = Convert.ToUInt32(element.GetCurrentPropertyValue(30002)) == pid });
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            { Record(new { step = "NotepadTitle.ElementFromPoint", hr = error.HResult }); }
        }
        catch (ArgumentException) { }
    }

    private static void Inspect(U.IUIAutomationElement element, string source, nint hwnd, uint pid)
    {
        if (Convert.ToUInt32(element.GetCurrentPropertyValue(30002)) != pid) return;
        var ancestor = element; bool belongs = false;
        for (int depth = 0; ancestor is not null && depth < 12; depth++)
        {
            nint owner = new(Convert.ToInt64(ancestor.GetCurrentPropertyValue(30020)));
            if (owner == hwnd || Native.IsChild(hwnd, owner)) { belongs = true; break; }
            ancestor = client.RawViewWalker.GetParentElement(ancestor);
        }
        if (!belongs) return;
        var types = new List<object>();
        foreach (int id in new[] { 10014, 10024, 10029, 10025, 10018, 10001, 10010 })
        {
            try
            {
                object? pattern = element.GetCurrentPattern(id);
                types.Add(new { patternId = id, available = pattern is not null, hr = 0 });
                // UIA 40008 对应前景色。只查询颜色支持情况，不输出颜色值或文字。
                U.IUIAutomationTextRange? range = pattern switch
                {
                    U.IUIAutomationTextPattern2 text2 => text2.DocumentRange,
                    U.IUIAutomationTextPattern text => text.DocumentRange,
                    _ => null
                };
                if (range is not null)
                {
                    object color = range.GetAttributeValue(40008);
                    Record(new { step = "TextRange.ForegroundColor", source, numericColor = color is int, hr = 0 });
                }
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException) { types.Add(new { patternId = id, available = false, hr = error.HResult }); }
        }
        Record(new { step = "UIA", source, controlType = element.GetCurrentPropertyValue(30003), children = element.FindAll((U.TreeScope)2, client.CreateTrueCondition()).Length, patterns = types });
    }

    private static void InspectAccessible(Accessibility.IAccessible accessible, object child, string source)
    {
        try
        {
            int hr = Query(accessible, typeof(IServiceProvider).GUID, out object? provider);
            int? serviceHr = null;
            if (hr >= 0 && provider is IServiceProvider service)
            {
                var sid = new Guid("f8b80ada-2c44-48d0-89be-5ff23c9cd875");
                serviceHr = service.QueryService(ref sid, ref sid, out nint result);
                if (result != 0) Marshal.Release(result);
            }
            Record(new { step = "MSAA", source, children = accessible.accChildCount, role = Convert.ToInt32(accessible.get_accRole(child)), state = Convert.ToInt32(accessible.get_accState(child)), serviceProviderHr = hr, accessibleExServiceHr = serviceHr, childIsSelf = child is int number && number == 0 });
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException) { Record(new { step = "MSAA", source, hr = error.HResult }); }
    }

    private static int Query(object value, Guid iid, out object? result)
    {
        result = null; nint unknown = Marshal.GetIUnknownForObject(value), pointer = 0;
        try { int hr = Marshal.QueryInterface(unknown, in iid, out pointer); if (hr >= 0) result = Marshal.GetObjectForIUnknown(pointer); return hr; }
        finally { if (pointer != 0) Marshal.Release(pointer); Marshal.Release(unknown); }
    }

    private static void Record(object metadata)
    {
        string key = JsonSerializer.Serialize(metadata);
        if (Observations.TryGetValue(key, out int count)) Observations[key] = count + 1;
        else if (Observations.Count < 128) { Observations[key] = 1; Console.WriteLine(key); }
    }

    // Windows SDK servprov.h；IAccessibleEx 的服务查询遵循微软文档。
    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out nint result); }
}

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    internal delegate bool Callback(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int x, y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int left, top, right, bottom; }
    internal static string Class(nint hwnd) { var text = new System.Text.StringBuilder(128); GetClassName(hwnd, text, text.Capacity); return text.ToString(); }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(Callback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint hwnd, Callback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int size);
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromPoint(Point point, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromWindow(nint hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible);
    [DllImport("oleacc.dll")] internal static extern int WindowFromAccessibleObject(Accessibility.IAccessible accessible, out nint hwnd);
}
