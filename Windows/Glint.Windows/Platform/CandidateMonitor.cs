using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Glint.Windows.Platform;

/// <summary>Bounds are physical desktop pixels, as returned by UI Automation.</summary>
public sealed record CandidateSnapshot(
    string Text,
    Rect Bounds,
    nint ForegroundWindow,
    int Revision,
    string Source = "Windows 输入法")
{
    internal string FocusIdentity { get; init; } = "";
    internal nint FocusWindow { get; init; }
    internal Rect? InputLine { get; init; }
}

/// <summary>
/// Reads only explicitly identified IME candidate containers. No text/value pattern content,
/// keyboard hook, clipboard, screenshots, or ordinary application lists are accessed.
/// All UIA calls run on a dedicated MTA worker, never the WPF dispatcher.
/// </summary>
public sealed class CandidateMonitor : IDisposable
{
    private readonly Func<int> dwellMilliseconds;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private CandidateSnapshot? current;
    private int revision;
    private bool runRequested;
    private bool disposed;

    public CandidateMonitor(Func<int> dwellMs) => dwellMilliseconds = dwellMs;

    /// <summary>Raised on the worker thread. null means the candidate session ended.</summary>
    public event Action<CandidateSnapshot?>? CandidateChanged;
    public event Action<string>? StatusChanged;
    /// <summary>Raised immediately on candidate changes, before the dwell period.</summary>
    public event Action? CandidateInvalidated;
    public CandidateSnapshot? CurrentSnapshot => Volatile.Read(ref current);

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            runRequested = true;
            if (cancellation is not null) return;
            var tokenSource = new CancellationTokenSource();
            cancellation = tokenSource;
            var thread = new Thread(() => Run(tokenSource.Token))
            {
                IsBackground = true,
                Name = "Glint IME candidate monitor"
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            runRequested = false;
            cancellation?.Cancel();
            // A single worker is retained until it acknowledges cancellation. UIA providers
            // can stall; repeatedly starting threads would otherwise leak stalled workers.
        }
        Volatile.Write(ref current, null);
    }

    private void Run(CancellationToken token)
    {
        CandidateSnapshot? pending = null;
        var pendingSince = Stopwatch.GetTimestamp();
        var published = false;
        var sessionExists = false;
        var absentSince = 0L;
        string? previousStatus = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                CandidateSnapshot? observed = null;
                var status = "等待中文输入法候选";
                try
                {
                    observed = CandidateReader.ReadCurrent();
                    if (observed is not null) status = "正在读取输入法候选";
                    else status = NativeCandidateReader.DiagnosticStage switch
                    {
                        var stage when stage?.StartsWith("candidate container") == true => "未找到输入法公开的候选列表",
                        "selected candidate" => "输入法未公开当前高亮候选",
                        "selection" => "当前选区无法确认为输入法组合区",
                        "we type candidate" => "正在连接微信输入法候选",
                        "doubao candidate" => "正在读取豆包输入法候选",
                        _ => "等待安全输入焦点与中文候选"
                    };
                }
                catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException
                    or COMException or UnauthorizedAccessException or ArgumentException)
                {
                    status = "等待可访问的输入法候选";
                }
                if (token.IsCancellationRequested) break;
                if (status != previousStatus)
                {
                    previousStatus = status;
                    StatusChanged?.Invoke(status);
                }
                if (observed is null)
                {
                    // 微软拼音第一次打开候选窗时，高亮会先闪一次空读。空读持续一会儿才结束会话。
                    if (pending is null && !sessionExists) absentSince = 0;
                    else if (absentSince == 0) absentSince = Stopwatch.GetTimestamp();
                    else if (Stopwatch.GetElapsedTime(absentSince).TotalMilliseconds >= 80)
                    {
                        pending = null;
                        published = false;
                        absentSince = 0;
                        Volatile.Write(ref current, null);
                        if (sessionExists)
                        {
                            sessionExists = false;
                            CandidateChanged?.Invoke(null);
                        }
                    }
                }
                else
                {
                    absentSince = 0;
                    sessionExists = true;
                    if (!CandidateReader.SameCandidate(pending, observed))
                    {
                        pending = observed;
                        pendingSince = Stopwatch.GetTimestamp();
                        published = false;
                        Volatile.Write(ref current, null);
                        CandidateInvalidated?.Invoke();
                    }
                    else
                    {
                        // Geometry can move without starting a new translation revision.
                        pending = observed;
                    }
                    var dwell = Services.AppSettings.SnapDwell(dwellMilliseconds());
                    if (!published && Stopwatch.GetElapsedTime(pendingSince).TotalMilliseconds >= dwell)
                    {
                        published = true;
                        var snapshot = pending with { Revision = Interlocked.Increment(ref revision) };
                        Volatile.Write(ref current, snapshot);
                        CandidateChanged?.Invoke(snapshot);
                    }
                    else if (published && current is { } existing && (existing.Bounds != observed.Bounds || existing.InputLine != observed.InputLine))
                    {
                        var snapshot = observed with { Revision = existing.Revision };
                        Volatile.Write(ref current, snapshot);
                        CandidateChanged?.Invoke(snapshot);
                    }
                }
                if (token.WaitHandle.WaitOne(observed is null ? 30 : 20)) break;
            }
        }
        finally
        {
            WeTypeCandidateBridge.Allow(false);
            Volatile.Write(ref current, null);
            CandidateChanged?.Invoke(null);
            lock (gate)
            {
                cancellation?.Dispose();
                cancellation = null;
                if (runRequested && !disposed) Start();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Stop();
        }
    }
}

internal static class CandidateReader
{
    private static readonly HashSet<string> MicrosoftImeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "InputApp", "ChsIME", "ChsIMEHost", "ChtIME"
    };
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "mintty"
    };

    internal static bool SameCandidate(CandidateSnapshot? first, CandidateSnapshot? second) =>
        first is not null && second is not null && first.Text == second.Text &&
        first.ForegroundWindow == second.ForegroundWindow && first.FocusIdentity == second.FocusIdentity;

    internal static CandidateSnapshot? ReadCurrent()
    {
        var modern = NativeCandidateReader.ReadCurrent();
        if (modern is not null) return modern;
        var originalStage = NativeCandidateReader.DiagnosticStage;
        if (!TryGetSafeFocus(out var foreground, out var focusIdentity, out var focusWindow)) return null;
        NativeCandidateReader.DiagnosticStage = originalStage;
        var handles = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            var className = NativeMethods.WindowClass(hwnd);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == Environment.ProcessId) return true;
            // A native IME host class, or Microsoft's input host process, is required
            // before searching a subtree. Ordinary app windows are never traversed.
            if (className is "Microsoft.IME.UIManager.CandidateWindow.Host" or "IME_Candidate_Window" ||
                MicrosoftImeProcesses.Contains(ProcessName(processId))) handles.Add(hwnd);
            return handles.Count < 12;
        }, 0);

        foreach (var hwnd in handles)
        {
            var root = AutomationElement.FromHandle(hwnd);
            var container = FindCandidateContainer(root);
            if (container is null || container.Current.IsOffscreen) continue;
            var selected = FindSelectedCandidate(container);
            if (selected is null) continue;
            // Reading Name is deliberately confined to the selected IME candidate.
            var text = selected.Current.Name?.Trim();
            if (!IsCandidateText(text)) continue;
            var bounds = container.Current.BoundingRectangle;
            if (!IsUsableBounds(bounds)) bounds = selected.Current.BoundingRectangle;
            if (!IsUsableBounds(bounds)) continue;
            try
            {
                var parent = TreeWalker.ControlViewWalker.GetParent(selected);
                for (int depth = 0; parent is not null && depth < 6; depth++)
                {
                    if (parent.Current.ControlType == ControlType.List)
                    {
                        bounds = HudPlacement.AlignToList(bounds, parent.Current.BoundingRectangle);
                        break;
                    }
                    if (Automation.Compare(parent, container)) break;
                    parent = TreeWalker.ControlViewWalker.GetParent(parent);
                }
            }
            catch (ElementNotAvailableException) { }
            if (NativeMethods.GetForegroundWindow() != foreground) return null;
            return new CandidateSnapshot(text!, bounds, foreground, 0, "微软拼音 / 标准候选")
            {
                FocusIdentity = focusIdentity,
                FocusWindow = focusWindow,
                InputLine = NativeCandidateReader.RecentInputLine
            };
        }
        return null;
    }

    internal static bool TryGetSafeFocus(out nint foreground, out string identity, out nint focusWindow)
    {
        return NativeCandidateReader.TryGetSafeFocus(out foreground, out identity, out focusWindow);
    }

    private static bool LegacySafeFocus(out nint foreground, out string identity, out nint focusWindow)
    {
        foreground = NativeMethods.GetForegroundWindow();
        identity = "";
        focusWindow = 0;
        if (foreground == 0) return false;
        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId || !NativeMethods.IsOrdinaryProcess(processId) ||
            TerminalProcesses.Contains(ProcessName(processId))) return false;
        var windowClass = NativeMethods.WindowClass(foreground);
        if (windowClass is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") return false;
        var focus = AutomationElement.FocusedElement;
        if (focus is null) return false;
        var info = focus.Current;
        // A UIA provider must positively identify the focus as a safe, active element.
        if (info.IsPassword || !info.IsEnabled || info.IsOffscreen || info.ProcessId != processId) return false;
        if (focus.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
        {
            // Compare endpoints only; never request the selected text. Windows' managed
            // UIA API cannot reliably distinguish every IME's marked range, so ambiguous
            // non-empty selections are skipped conservatively.
            foreach (var selection in ((TextPattern)textPattern).GetSelection())
                if (selection.CompareEndpoints(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End) != 0)
                    return false;
        }
        var gui = new NativeMethods.GUITHREADINFO { size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref gui) || gui.focus == 0) return false;
        focusWindow = gui.focus;
        identity = string.Join('.', focus.GetRuntimeId());
        return identity.Length > 0 && NativeMethods.GetForegroundWindow() == foreground;
    }

    private static AutomationElement? FindCandidateContainer(AutomationElement root)
    {
        foreach (var node in BoundedTree(root, 7, 160))
            if (node.Current.AutomationId == "IME_Candidate_Window") return node;
        return null;
    }

    private static AutomationElement? FindSelectedCandidate(AutomationElement container)
    {
        if (container.TryGetCurrentPattern(SelectionPattern.Pattern, out var selectionObject))
        {
            var selected = ((SelectionPattern)selectionObject).Current.GetSelection();
            if (selected.Length == 1 && IsSelected(selected[0])) return selected[0];
        }
        AutomationElement? match = null;
        foreach (var node in BoundedTree(container, 5, 64))
        {
            if (!IsSelected(node)) continue;
            if (match is not null) return null;
            match = node;
        }
        return match;
    }

    private static bool IsSelected(AutomationElement element) => !element.Current.IsOffscreen &&
        element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
        ((SelectionItemPattern)pattern).Current.IsSelected;

    private static IEnumerable<AutomationElement> BoundedTree(AutomationElement root, int maxDepth, int maximum)
    {
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        var walker = TreeWalker.RawViewWalker;
        var visited = 0;
        var budget = Stopwatch.StartNew();
        while (pending.Count > 0 && visited++ < maximum && budget.ElapsedMilliseconds < 250)
        {
            var (element, depth) = pending.Dequeue();
            yield return element;
            if (depth >= maxDepth) continue;
            var child = walker.GetFirstChild(element);
            var siblings = 0;
            while (child is not null && siblings++ < 40 && pending.Count < maximum && budget.ElapsedMilliseconds < 250)
            {
                pending.Enqueue((child, depth + 1));
                child = walker.GetNextSibling(child);
            }
        }
    }

    private static bool IsUsableBounds(Rect bounds) => !bounds.IsEmpty &&
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        bounds.Width is > 0 and < 5000 && bounds.Height is > 0 and < 1200;

    private static bool IsCandidateText(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= 120 && !text.Any(char.IsControl) &&
        text.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }
}
