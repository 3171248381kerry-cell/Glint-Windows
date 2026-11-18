using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using U = Glint.Windows.Interop.UIA;

namespace Glint.Windows.Platform;

/// <summary>
/// 监听输入法可访问性事件，不是键盘事件。只有确认属于标准候选窗口时，才在回调线程
/// 读取当前高亮的文本和位置；诊断和日志不保存这段文字，也不读取输入框正文。
/// </summary>
internal static class ImeAutomationEvents
{
    private static readonly Guid ElementIid = typeof(U.IUIAutomationElement).GUID;
    private static readonly ConcurrentQueue<Source> Sources = new();
    private static readonly ManualResetEventSlim Stop = new(false);
    private static readonly ThreadLocal<U.IUIAutomation> CallbackClients = new(() => CreateClient(250));
    private static long sequence;
    private static long received;
    private static int started;
    private static long nextAttemptTicks;
    private static Sink? activeSink;
    private static LiveCandidate? live;
    private static U.IUIAutomation? eventClient;
    private static U.IUIAutomationElement? highlightedItem;
    private static U.IUIAutomationElement? watchedItem;
    private static U.IUIAutomationElement? openWindow;
    private static nint openForeground;
    private static bool menuWasVisible;
    private static U.IUIAutomationElement? watchedWindow;
    private static NameSink? nameSink;
    private static DispatcherTimer? retryTimer;
    private static int openGeneration;
    private static int retryGeneration;
    private static int retriesLeft;
    private static string openDiagnostic = "idle";
    internal static string OpenDiagnostic => Volatile.Read(ref openDiagnostic);
    private static long microsoftCandidateFocusEvents;
    internal static long MicrosoftCandidateFocusEvents => Interlocked.Read(ref microsoftCandidateFocusEvents);

    /// <summary>候选窗第一次出现时，高亮项通常是创建出来就已经选中的，不会再发选中事件。</summary>
    internal static bool ShouldReadOpenMenu(int eventId, bool candidateWindow) =>
        eventId == 20003 && candidateWindow;

    internal sealed class MenuSession
    {
        private int closed;
        internal bool IsClosed => Volatile.Read(ref closed) != 0;
        internal void Close() => Volatile.Write(ref closed, 1);
    }

    internal sealed record LiveCandidate(string Text, Rect Bounds, nint Foreground, bool FromImeHost, MenuSession Session);
    internal static LiveCandidate? Live => Volatile.Read(ref live);
    internal static event Action<MenuSession>? MenuClosed;

    internal static string Status { get; private set; } = "not started";
    internal static string LastDrop { get; private set; } = "";
    internal static long Count => Interlocked.Read(ref sequence);
    internal static long Received => Interlocked.Read(ref received);
    internal static Source[] Snapshot() => Sources.ToArray();

    internal static object[] RecentMetadata(int maximum = 12) =>
        Sources.ToArray().TakeLast(Math.Clamp(maximum, 1, 32)).Select(source => source.Metadata).ToArray();

    internal static void Start()
    {
        if (Stop.IsSet) return;
        if (Stopwatch.GetTimestamp() < Interlocked.Read(ref nextAttemptTicks)) return;
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
        // UIA 会把回调封送回注册线程。该线程必须是 STA 并泵消息，否则事件到不了托管对象。
        var thread = new Thread(Run) { IsBackground = true, Name = "Glint IME accessibility events" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static void Shutdown() => Stop.Set();

    private static void Run()
    {
        U.IUIAutomation? client = null;
        U.IUIAutomationElement? root = null;
        var sink = new Sink();
        var focusSink = new FocusSink();
        activeSink = sink;
        var registered = new List<int>();
        bool registeredFocus = false;
        try
        {
            client = CreateClient();
            root = client.GetRootElement();
            var cache = client.CreateCacheRequest();
            cache.AutomationElementMode = U.AutomationElementMode.AutomationElementMode_Full;
            foreach (int id in new[] { 20003, 20007, 20010, 20012 })
            {
                client.AddAutomationEventHandler(id, root, U.TreeScope.TreeScope_Subtree, cache, sink);
                registered.Add(id);
            }
            try
            {
                var focusCache = client.CreateCacheRequest();
                focusCache.AutomationElementMode = U.AutomationElementMode.AutomationElementMode_Full;
                focusCache.AddProperty(30002); // ProcessId
                focusCache.AddProperty(30003); // ControlType
                client.AddFocusChangedEventHandler(focusCache, focusSink);
                registeredFocus = true;
            }
            catch (COMException) { } // Menu and selection notifications remain available.
            eventClient = client;
            Status = "listening";
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            ThreadPool.UnsafeRegisterWaitForSingleObject(Stop.WaitHandle, (_, _) => dispatcher.BeginInvoke(() => frame.Continue = false), null, Timeout.Infinite, true);
            Dispatcher.PushFrame(frame);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            Status = "event subscription failed: " + error.HResult;
            Interlocked.Exchange(ref nextAttemptTicks, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5);
            Interlocked.Exchange(ref started, 0);
        }
        finally
        {
            if (client is not null && root is not null)
            {
                foreach (int id in registered)
                    try { client.RemoveAutomationEventHandler(id, root, sink); } catch (COMException) { }
                if (registeredFocus)
                    try { client.RemoveFocusChangedEventHandler(focusSink); } catch (COMException) { }
            }
            EndCandidate();
            eventClient = null;
            while (Sources.TryDequeue(out var source)) source.ReleaseStream();
            if (ReferenceEquals(activeSink, sink)) activeSink = null;
            GC.KeepAlive(sink);
            GC.KeepAlive(focusSink);
            GC.KeepAlive(nameSink);
        }
    }

    private static U.IUIAutomation CreateClient(uint timeout = 700)
    {
        var client = (U.IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E22AD333-B25F-460C-83D0-0581107395C9"), true)!)!;
        if (client is U.IUIAutomation2 settings) { settings.ConnectionTimeout = timeout; settings.TransactionTimeout = timeout; }
        return client;
    }

    internal sealed class Source
    {
        private readonly object gate = new();
        private nint stream;
        private U.IUIAutomationElement? element;

        internal Source(long sequence, int eventId, nint foreground, int processId, object metadata, nint stream)
        {
            Sequence = sequence;
            EventId = eventId;
            Foreground = foreground;
            ProcessId = processId;
            Metadata = metadata;
            this.stream = stream;
        }

        internal long Sequence { get; }
        internal int EventId { get; }
        internal nint Foreground { get; }
        internal int ProcessId { get; }
        internal object Metadata { get; }

        /// <summary>在当前 MTA 线程解组一次。事件线程上的 RCW 不能直接跨线程使用。</summary>
        internal U.IUIAutomationElement? ElementOnThisThread()
        {
            lock (gate)
            {
                if (element is not null) return element;
                if (stream == 0) return null;
                nint local = stream;
                stream = 0;
                Guid iid = ElementIid;
                int hr = CoGetInterfaceAndReleaseStream(local, ref iid, out object? resolved);
                if (hr < 0 || resolved is not U.IUIAutomationElement item) return null;
                element = item;
                return element;
            }
        }

        internal void ReleaseStream()
        {
            lock (gate)
            {
                if (stream == 0) return;
                // Eviction and shutdown run on the STA that marshaled the element.
                CoReleaseMarshalData(stream);
                Marshal.Release(stream);
                stream = 0;
            }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Sink : U.IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(U.IUIAutomationElement sender, int eventId)
        {
            if (Stop.IsSet || sender is null) return;
            Interlocked.Increment(ref received);
            try
            {
                if (eventId == 20007 && Value(sender, 30011) as string == "IME_Candidate_Window" &&
                    (openWindow is null || IsSameElement(sender, openWindow)))
                    EndCandidate();
                if (Describe(sender, eventId) is not { } described) return;
                if (described.Close)
                {
                    if (openWindow is { } tracked && CandidateWindowOf(sender) is { } closed && IsSameElement(closed, tracked))
                        EndCandidate();
                }
                else if (described.Text is not null && described.HasBounds)
                {
                    var item = HighlightItem(sender);
                    highlightedItem = item;
                    PublishCandidate(described.Text, described.Bounds, described.Foreground, described.FromImeHost);
                    if (openWindow is null && CandidateWindowOf(item) is { } window)
                        TrackOpenWindow(window);
                    Watch(item);
                }
                else if (ShouldReadOpenMenu(eventId, described.CandidateWindow))
                    BeginOpen(sender);
                nint marshaled = MarshalElement(sender);
                var source = new Source(Interlocked.Increment(ref sequence), eventId, described.Foreground, described.ProcessId, described.Metadata, marshaled);
                Sources.Enqueue(source);
                while (Sources.Count > 64)
                    if (Sources.TryDequeue(out var old)) old.ReleaseStream();
            }
            catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
            { LastDrop = error.GetType().Name + ":" + error.HResult; }
        }
    }

    private readonly record struct Description(nint Foreground, int ProcessId, object Metadata, string? Text, Rect Bounds, bool HasBounds, bool Close, bool CandidateWindow, bool Offscreen, bool FromImeHost);

    private static Description? Describe(U.IUIAutomationElement sender, int eventId)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        int processId = Convert.ToInt32(Value(sender, 30002) ?? 0);
        string automationId = Sanitize(Value(sender, 30011) as string);
        nint hwnd = new(Convert.ToInt64(Value(sender, 30020) ?? 0L));
        string hwndClass = NativeMethods.WindowClass(hwnd);
        string elementClass = Sanitize(Value(sender, 30012) as string);
        bool knownIme = IsKnownImeProcess(processId);
        bool imeClass = IsImeClass(hwndClass) || IsImeClass(elementClass);
        bool selection = eventId is 20010 or 20012;
        bool candidateWindow = automationId == "IME_Candidate_Window";
        object? selectionContainer = null;
        string containerId = "";
        string patternType = "";
        if (selection)
        {
            object? pattern = ValuePattern(sender);
            patternType = pattern?.GetType().Name ?? "null";
            var item = As<U.IUIAutomationSelectionItemPattern>(pattern);
            U.IUIAutomationElement? container = item is null ? null : As<U.IUIAutomationElement>(item.CurrentSelectionContainer);
            container ??= As<U.IUIAutomationElement>(Value(sender, 30161));
            if (container is not null)
            {
                containerId = Sanitize(Value(container, 30011) as string);
                selectionContainer = Meta(container);
                if (containerId == "IME_Candidate_Window") candidateWindow = true;
            }
        }
        bool keep = candidateWindow || knownIme || imeClass || (selection && processId == (int)foregroundPid);
        if (!keep)
        {
            LastDrop = "event=" + eventId + " pid=" + processId + " fg=" + foregroundPid + " id=" + automationId + " pattern=" + patternType + " container=" + containerId;
            return null;
        }
        var ancestors = new List<object>();
        U.IUIAutomationElement? candidateElement = candidateWindow ? sender : null;
        U.IUIAutomationElement? candidateList = null;
        try
        {
            var parent = CallbackClients.Value!.ControlViewWalker.GetParentElement(sender);
            for (int depth = 0; parent is not null && depth < 6; depth++)
            {
                ancestors.Add(Meta(parent));
                if (candidateList is null && Value(parent, 30003) is int controlType && controlType == 50008)
                    candidateList = parent;
                if (Sanitize(Value(parent, 30011) as string) == "IME_Candidate_Window")
                {
                    candidateWindow = true;
                    candidateElement = parent;
                }
                parent = CallbackClients.Value.ControlViewWalker.GetParentElement(parent);
            }
        }
        catch (COMException) { }
        string? text = null;
        var bounds = new Rect();
        bool hasBounds = false;
        // 微软拼音的候选树只能从事件节点向上看到，ElementFromHandle 拿到的 CoreWindow 是空的。
        // 必须在回调线程读取高亮，跨线程解组这个 XAML 元素会失败。
        if (candidateWindow && selection)
        {
            text = ReadCandidateText(sender);
            hasBounds = candidateElement is not null && TryBounds(candidateElement, out bounds);
            if (hasBounds && candidateList is not null && TryBounds(candidateList, out var listBounds))
                bounds = HudPlacement.AlignToList(bounds, listBounds);
            if (!hasBounds) hasBounds = TryBounds(sender, out bounds);
        }
        bool close = eventId == 20007 && candidateWindow;
        return new Description(foreground, processId, new
        {
            eventId,
            processId,
            candidateWindow,
            captured = text is not null,
            closed = close,
            sender = Meta(sender),
            ancestors,
            selectionContainer
        }, text, bounds, hasBounds, close, candidateWindow, Value(sender, 30022) is true, knownIme);
    }

    private static void BeginOpen(U.IUIAutomationElement sender)
    {
        var window = CandidateWindowOf(sender);
        if (window is null) { Volatile.Write(ref openDiagnostic, "open: no candidate window"); return; }
        TrackOpenWindow(window);
    }

    private static void TrackOpenWindow(U.IUIAutomationElement window)
    {
        Volatile.Write(ref openDiagnostic, "open: searching");
        openWindow = window;
        openForeground = NativeMethods.GetForegroundWindow();
        menuWasVisible = false;
        int generation = ++openGeneration;
        WatchWindow(window);
        TryPublishOpenWindow(window);
        ArmRetry(generation);
    }

    private static void EndCandidate()
    {
        var previous = Interlocked.Exchange(ref live, null);
        if (previous is not null)
        {
            previous.Session.Close();
            MenuClosed?.Invoke(previous.Session);
        }
        openGeneration++;
        openWindow = null;
        openForeground = 0;
        menuWasVisible = false;
        retryTimer?.Stop();
        Unwatch();
        UnwatchWindow();
        highlightedItem = null;
        Volatile.Write(ref openDiagnostic, "closed");
    }

    private static void ArmRetry(int generation)
    {
        // The initial candidate may not expose SelectionItem until after the user
        // finishes typing. Keep looking while this menu is open, without guessing
        // that the first item is selected. EndCandidate cancels the timer.
        retriesLeft = 125;
        retryGeneration = generation;
        if (retryTimer is null)
        {
            retryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
            retryTimer.Tick += (_, _) =>
            {
                if (retryGeneration != openGeneration || openWindow is not { } window) { retryTimer.Stop(); return; }
                if (openForeground != NativeMethods.GetForegroundWindow()) { EndCandidate(); return; }
                if (menuWasVisible && Live is { } current && current.Foreground == openForeground)
                {
                    if (IsMenuOffscreen(window)) EndCandidate();
                    return;
                }
                if (retriesLeft-- <= 0) { EndCandidate(); return; }
                TryPublishOpenWindow(window);
            };
        }
        retryTimer.Stop();
        retryTimer.Start();
    }

    private static bool TryPublishOpenWindow(U.IUIAutomationElement window)
    {
        try
        {
            var foreground = openForeground;
            if (foreground == 0 || NativeMethods.GetForegroundWindow() != foreground || Value(window, 30022) is not false)
            { Volatile.Write(ref openDiagnostic, "window offscreen or foreground changed"); return false; }
            menuWasVisible = true;
            var item = FindSelectedItem(window);
            if (item is null) { Volatile.Write(ref openDiagnostic, "no selected item"); return false; }
            var text = ReadCandidateText(item);
            if (string.IsNullOrEmpty(text)) { Volatile.Write(ref openDiagnostic, "selected item has no candidate text"); return false; }
            bool hasBounds = TryBounds(window, out var bounds) || TryBounds(item, out bounds);
            if (!hasBounds) { Volatile.Write(ref openDiagnostic, "no bounds"); return false; }
            if (ListAncestor(item) is { } list && TryBounds(list, out var listBounds))
                bounds = HudPlacement.AlignToList(bounds, listBounds);
            highlightedItem = item;
            Watch(item);
            if (NativeMethods.GetForegroundWindow() != foreground) return false;
            PublishCandidate(text, bounds, foreground, IsKnownImeProcess(Convert.ToInt32(Value(window, 30002) ?? 0)));
            Volatile.Write(ref openDiagnostic, "published");
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { Volatile.Write(ref openDiagnostic, "open error: " + error.GetType().Name); return false; }
    }

    private static void PublishCandidate(string text, Rect bounds, nint foreground, bool fromImeHost)
    {
        var session = Live is { } current && current.Foreground == foreground ? current.Session : new MenuSession();
        Volatile.Write(ref live, new LiveCandidate(text, bounds, foreground, fromImeHost, session));
    }

    private static bool IsMenuOffscreen(U.IUIAutomationElement window)
    {
        try { return window.GetCurrentPropertyValue(30022) is true; }
        catch (COMException error) { return error.HResult == unchecked((int)0x80040201); } // UIA_E_ELEMENTNOTAVAILABLE
    }

    private static bool IsSameElement(U.IUIAutomationElement first, U.IUIAutomationElement second)
    {
        if (ReferenceEquals(first, second)) return true;
        try { return CallbackClients.Value!.CompareElements(first, second) != 0; }
        catch (COMException) { return false; }
    }

    private static U.IUIAutomationElement? CandidateWindowOf(U.IUIAutomationElement node)
    {
        var current = node;
        for (int depth = 0; current is not null && depth < 6; depth++)
        {
            if (Sanitize(Value(current, 30011) as string) == "IME_Candidate_Window") return current;
            try { current = CallbackClients.Value!.ControlViewWalker.GetParentElement(current); }
            catch (COMException) { return null; }
        }
        return null;
    }

    private static U.IUIAutomationElement? FindSelectedItem(U.IUIAutomationElement root)
    {
        // The XAML Pinyin panel exposes its highlighted item as keyboard-focused
        // even when it does not implement either of the UIA selection patterns.
        if (FindFocusedMicrosoftItem(root) is { } focused) return focused;
        foreach (var walker in new[] { CallbackClients.Value!.ControlViewWalker, CallbackClients.Value.RawViewWalker })
        {
            var pending = new Queue<(U.IUIAutomationElement Node, int Depth)>();
            pending.Enqueue((root, 0));
            for (int seen = 0; pending.Count > 0 && seen++ < 48;)
            {
                var (node, depth) = pending.Dequeue();
                if (depth > 0 && Value(node, 30079) is true && Value(node, 30022) is false) return node;
                if (depth >= 6) continue;
                U.IUIAutomationElement? child;
                try { child = walker.GetFirstChildElement(node); }
                catch (COMException) { continue; }
                for (int i = 0; child is not null && i < 16; i++)
                {
                    pending.Enqueue((child, depth + 1));
                    try { child = walker.GetNextSiblingElement(child); }
                    catch (COMException) { break; }
                }
            }
        }
        return null;
    }

    private static U.IUIAutomationElement? FindFocusedMicrosoftItem(U.IUIAutomationElement window)
    {
        if (!IsMicrosoftImeProcess(Convert.ToInt32(Value(window, 30002) ?? 0))) return null;
        var walker = CallbackClients.Value!.ControlViewWalker;
        var panel = walker.GetFirstChildElement(window);
        for (int i = 0; panel is not null && i < 12; i++, panel = walker.GetNextSiblingElement(panel))
        {
            if (!IsMicrosoftCandidatePanel(panel)) continue;
            var item = walker.GetFirstChildElement(panel);
            for (int j = 0; item is not null && j < 16; j++, item = walker.GetNextSiblingElement(item))
                if (IsFocusedCandidateItem(item)) return item;
            break;
        }
        return null;
    }

    private static bool IsMicrosoftCandidatePanel(U.IUIAutomationElement panel) =>
        Value(panel, 30011) as string == "TEMPLATE_PART_CandidatePanel" && Value(panel, 30003) is 50008;

    private static bool IsFocusedCandidateItem(U.IUIAutomationElement item) =>
        Value(item, 30003) is 50007 && Value(item, 30022) is false && Value(item, 30008) is true;

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class FocusSink : U.IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(U.IUIAutomationElement sender)
        {
            if (Stop.IsSet || sender is null) return;
            try
            {
                if (sender.GetCachedPropertyValue(30003) is not 50007 ||
                    !IsMicrosoftImeProcess(Convert.ToInt32(sender.GetCachedPropertyValue(30002))) ||
                    !IsFocusedCandidateItem(sender)) return;
                var panel = CallbackClients.Value!.ControlViewWalker.GetParentElement(sender);
                if (panel is null || !IsMicrosoftCandidatePanel(panel) || CandidateWindowOf(panel) is not { } window) return;
                Interlocked.Increment(ref microsoftCandidateFocusEvents);
                TrackOpenWindow(window);
            }
            catch (Exception error) when (error is COMException or InvalidCastException) { }
        }
    }

    private static U.IUIAutomationElement? ListAncestor(U.IUIAutomationElement item)
    {
        var current = item;
        for (int depth = 0; current is not null && depth < 6; depth++)
        {
            if (Convert.ToInt32(Value(current, 30003) ?? 0) == 50008) return current;
            try { current = CallbackClients.Value!.ControlViewWalker.GetParentElement(current); }
            catch (COMException) { return null; }
        }
        return null;
    }

    private static void WatchWindow(U.IUIAutomationElement window)
    {
        if (eventClient is null || (watchedWindow is { } current && IsSameElement(current, window))) return;
        UnwatchWindow();
        try
        {
            nameSink ??= new NameSink();
            var cache = eventClient.CreateCacheRequest();
            cache.AutomationElementMode = U.AutomationElementMode.AutomationElementMode_Full;
            eventClient.AddPropertyChangedEventHandler(window, U.TreeScope.TreeScope_Subtree, cache, nameSink, new[] { 30005, 30022 });
            watchedWindow = window;
            Unwatch();
        }
        catch (Exception error) when (error is COMException or ArgumentException) { }
    }

    private static void UnwatchWindow()
    {
        if (eventClient is not null && watchedWindow is not null && nameSink is not null)
            try { eventClient.RemovePropertyChangedEventHandler(watchedWindow, nameSink); } catch (COMException) { }
        watchedWindow = null;
    }

    private static void Watch(U.IUIAutomationElement item)
    {
        if (eventClient is null || watchedWindow is not null ||
            (watchedItem is { } current && IsSameElement(current, item))) return;
        Unwatch();
        try
        {
            nameSink ??= new NameSink();
            var cache = eventClient.CreateCacheRequest();
            cache.AutomationElementMode = U.AutomationElementMode.AutomationElementMode_Full;
            eventClient.AddPropertyChangedEventHandler(item, U.TreeScope.TreeScope_Subtree, cache, nameSink, new[] { 30005 });
            watchedItem = item;
        }
        catch (Exception error) when (error is COMException or ArgumentException) { }
    }

    private static void Unwatch()
    {
        if (eventClient is not null && watchedItem is not null && nameSink is not null)
            try { eventClient.RemovePropertyChangedEventHandler(watchedItem, nameSink); } catch (COMException) { }
        watchedItem = null;
    }

    private static void RefreshHighlightedItem()
    {
        var item = highlightedItem;
        var current = Volatile.Read(ref live);
        if (item is null || current is null) return;
        try
        {
            var text = ReadCandidateText(item, includePatterns: false);
            if (string.IsNullOrEmpty(text) || text == current.Text) return;
            Volatile.Write(ref live, current with { Text = text });
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            Unwatch();
            highlightedItem = null;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class NameSink : U.IUIAutomationPropertyChangedEventHandler
    {
        public void HandlePropertyChangedEvent(U.IUIAutomationElement sender, int propertyId, object newValue)
        {
            if (Stop.IsSet || sender is null) return;
            try
            {
                if (propertyId == 30022)
                {
                    if (newValue is true && menuWasVisible && Live is { } current && current.Foreground == openForeground &&
                        openWindow is { } window && IsSameElement(sender, window) && IsMenuOffscreen(window))
                        EndCandidate();
                    return;
                }
                if (propertyId != 30005) return;
                if (openWindow is not null && TryPublishOpenWindow(openWindow)) return;
                RefreshHighlightedItem();
            }
            catch (Exception error) when (error is COMException or InvalidCastException) { }
        }
    }

    private static U.IUIAutomationElement HighlightItem(U.IUIAutomationElement sender)
    {
        var current = sender;
        for (int depth = 0; current is not null && depth < 6; depth++)
        {
            if (Convert.ToInt32(Value(current, 30003) ?? 0) == 50007) return current;
            try { current = CallbackClients.Value!.ControlViewWalker.GetParentElement(current); }
            catch (COMException) { break; }
        }
        return sender;
    }

    private static string? ReadCandidateText(U.IUIAutomationElement node, bool includePatterns = true)
    {
        var leaves = new List<string>();
        CollectLeaves(node, CallbackClients.Value!.ControlViewWalker, leaves, 0);
        if (leaves.Count <= 1) CollectLeaves(node, CallbackClients.Value.RawViewWalker, leaves, 0);
        return ChooseCandidatePhrase(
            Value(node, 30005) as string,
            Value(node, 30159) as string,
            includePatterns ? LegacyName(node) : null,
            includePatterns ? ItemText(node) : null,
            leaves);
    }

    /// <summary>
    /// 微软拼音常把词组拆成多个文本节点，Name 有时只有第一个字。拼回同一高亮项里的汉字，不跨到其他候选。
    /// </summary>
    internal static string? ChooseCandidatePhrase(string? name, string? description, string? legacyName, string? itemText, IReadOnlyList<string> leaves)
    {
        var leafPhrase = JoinLeaves(leaves);
        var textPhrase = HanOnly(itemText);
        if (textPhrase is not null && leafPhrase is not null &&
            (!textPhrase.StartsWith(leafPhrase, StringComparison.Ordinal) || textPhrase.Length > leafPhrase.Length + 4))
            textPhrase = null;
        string? best = null;
        foreach (var candidate in new[] { HanOnly(name), HanOnly(description), HanOnly(legacyName), leafPhrase, textPhrase })
        {
            if (string.IsNullOrEmpty(candidate) || candidate!.Length > 12) continue;
            if (candidate.Length > (best?.Length ?? 0)) best = candidate;
        }
        return best;
    }

    internal static string? JoinLeaves(IReadOnlyList<string> leaves)
    {
        var phrase = "";
        foreach (var leaf in leaves)
        {
            var han = HanOnly(leaf);
            if (string.IsNullOrEmpty(han)) continue;
            if (phrase.Length == 0 || han.StartsWith(phrase, StringComparison.Ordinal)) phrase = han;
            else if (!phrase.Contains(han, StringComparison.Ordinal)) phrase += han;
        }
        return phrase.Length == 0 ? null : phrase;
    }

    internal static string? HanOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var han = new string(value.Where(character => character is >= '\u3400' and <= '\u9fff').ToArray());
        return han.Length == 0 ? null : han;
    }

    private static void CollectLeaves(U.IUIAutomationElement node, U.IUIAutomationTreeWalker walker, List<string> leaves, int depth)
    {
        if (depth > 5 || leaves.Count > 24) return;
        U.IUIAutomationElement? child = null;
        try { child = walker.GetFirstChildElement(node); }
        catch (COMException) { return; }
        if (child is null)
        {
            if (Value(node, 30005) is string name && name.Length > 0) leaves.Add(name);
            return;
        }
        for (int i = 0; child is not null && i < 12 && leaves.Count <= 24; i++)
        {
            CollectLeaves(child, walker, leaves, depth + 1);
            try { child = walker.GetNextSiblingElement(child); }
            catch (COMException) { return; }
        }
    }

    private static string? LegacyName(U.IUIAutomationElement node)
    {
        try
        {
            return As<U.IUIAutomationLegacyIAccessiblePattern>(Pattern(node, 10018)) is { } legacy ? legacy.CurrentName : null;
        }
        catch (COMException) { return null; }
    }

    private static string? ItemText(U.IUIAutomationElement node)
    {
        try
        {
            return As<U.IUIAutomationTextPattern>(Pattern(node, 10014)) is { } text ? text.DocumentRange?.GetText(32) : null;
        }
        catch (COMException) { return null; }
    }

    private static bool TryBounds(U.IUIAutomationElement node, out Rect bounds)
    {
        bounds = new Rect();
        if (Value(node, 30001) is not double[] rectangle || rectangle.Length != 4) return false;
        if (!rectangle.All(double.IsFinite) || rectangle[2] is <= 0 or > 5000 || rectangle[3] is <= 0 or > 1200) return false;
        bounds = new Rect(rectangle[0], rectangle[1], rectangle[2], rectangle[3]);
        return true;
    }

    private static object Meta(U.IUIAutomationElement node) => new
    {
        type = Value(node, 30003),
        id = Sanitize(Value(node, 30011) as string),
        cls = Sanitize(Value(node, 30012) as string),
        framework = Sanitize(Value(node, 30024) as string),
        offscreen = Value(node, 30022) is true,
        hwndClass = NativeMethods.WindowClass(new nint(Convert.ToInt64(Value(node, 30020) ?? 0L)))
    };

    private static object? Value(U.IUIAutomationElement node, int property)
    {
        try { return node.GetCurrentPropertyValue(property); }
        catch (COMException) { return null; }
    }

    private static object? ValuePattern(U.IUIAutomationElement node) => Pattern(node, 10010);

    private static object? Pattern(U.IUIAutomationElement node, int id)
    {
        try { return node.GetCurrentPattern(id); }
        catch (COMException) { return null; }
    }

    private static T? As<T>(object? value) where T : class
    {
        if (value is null) return null;
        if (value is T typed) return typed;
        nint unknown = 0;
        try
        {
            unknown = Marshal.GetIUnknownForObject(value);
            return Marshal.GetTypedObjectForIUnknown(unknown, typeof(T)) as T;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { return null; }
        finally { if (unknown != 0) Marshal.Release(unknown); }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Any(character => character > 127)) return "(non-ascii)";
        return value.Length <= 64 ? value : value[..64];
    }

    private static bool IsImeClass(string className) =>
        className.Contains("IME", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Candidate", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("WeType", StringComparison.Ordinal);

    private static bool IsKnownImeProcess(int processId) => IsImeProcess(processId, includeWeType: true);
    private static bool IsMicrosoftImeProcess(int processId) => IsImeProcess(processId, includeWeType: false);

    private static bool IsImeProcess(int processId, bool includeWeType)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            return name is "TextInputHost" or "InputApp" or "ChsIME" or "ChsIMEHost" or "ChtIME" || includeWeType && name == "wetype_renderer";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static nint MarshalElement(U.IUIAutomationElement element)
    {
        nint unknown = Marshal.GetIUnknownForObject(element);
        try
        {
            Guid iid = ElementIid;
            return CoMarshalInterThreadInterfaceInStream(ref iid, unknown, out nint stream) >= 0 ? stream : 0;
        }
        finally { Marshal.Release(unknown); }
    }

    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid riid, nint unknown, out nint stream);

    [DllImport("ole32.dll")]
    private static extern int CoGetInterfaceAndReleaseStream(nint stream, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    [DllImport("ole32.dll")]
    private static extern int CoReleaseMarshalData(nint stream);
}
