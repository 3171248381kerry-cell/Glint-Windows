using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Glint.Windows.Platform;

/// <summary>
/// 豆包输入法的候选窗没有辅助功能子项，输入线程也不公布候选列表。
/// 候选栏出现时，调用输入法自己的只读候选接口，只取当前高亮的一行。
/// </summary>
internal static class DoubaoCandidateBridge
{
    private const string PipeName = @"\\.\pipe\ObricIme\oime-server";
    private const int Capacity = 0x4000;
    private static readonly nint PipeNamePointer = Marshal.StringToHGlobalAnsi(PipeName);
    private static readonly BlockingCollection<ReadRequest> Requests = new(1);
    private static int workerStarted;
    private static nint module;
    private static nint buffer;
    private static GetCandidateList? read;

    internal static (nint Handle, Rect Bounds)? FindWindow()
    {
        (nint Handle, Rect Bounds)? found = null;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.WindowClass(hwnd) != "OimeDirectUIWindow") return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals("ImeService", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
            int width = rect.right - rect.left, height = rect.bottom - rect.top;
            // 常驻的小浮标不是候选栏。候选栏是那条又宽又扁的窗口。
            if (width is >= 300 and < 2400 && height is >= 36 and < 800)
            {
                rect = NativeMethods.VisibleWindowRect(hwnd, rect);
                found = (hwnd, CandidateRowBounds(new Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top), NativeMethods.GetDpiForWindow(hwnd)));
            }
            return found is null;
        }, 0);
        return found;
    }

    // The default Doubao candidate window includes 24 DIP of transparent and
    // composition-area inset before the visible candidate row begins.
    internal static Rect CandidateRowBounds(Rect window, uint dpi)
    {
        double inset = 24.0 * (dpi == 0 ? 96 : dpi) / 96.0;
        if (window.IsEmpty || window.Width <= inset) return window;
        return new Rect(window.Left + inset, window.Top, window.Width - inset, window.Height);
    }

    internal static CandidateSnapshot? TryRead(Rect bounds, nint foreground, string identity, nint focusWindow)
    {
        EnsureWorker();
        var request = new ReadRequest();
        if (!Requests.TryAdd(request) || !request.Done.Wait(120) ||
            request.Payload is null || !TrySelect(request.Payload, request.Selection, out string text)) return null;
        if (NativeMethods.GetForegroundWindow() != foreground) return null;
        return new CandidateSnapshot(text, bounds, foreground, 0, "豆包输入法")
        {
            FocusIdentity = identity,
            FocusWindow = focusWindow,
            InputLine = NativeCandidateReader.RecentInputLine
        };
    }

    private static void EnsureWorker()
    {
        if (Interlocked.Exchange(ref workerStarted, 1) != 0) return;
        var thread = new Thread(StaLoop)
        {
            IsBackground = true,
            Name = "Glint Doubao candidate"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void StaLoop()
    {
        foreach (var request in Requests.GetConsumingEnumerable())
        {
            try
            {
                if (EnsureReader())
                {
                    if (buffer == 0) buffer = Marshal.AllocHGlobal(Capacity);
                    int length = read!(PipeNamePointer, buffer, Capacity, out int selection, out _, out _);
                    if (length is > 0 and <= Capacity && selection >= 0)
                    {
                        var bytes = new byte[length];
                        Marshal.Copy(buffer, bytes, 0, length);
                        request.Payload = Encoding.UTF8.GetString(bytes);
                        request.Selection = selection;
                    }
                }
            }
            catch (Exception error) when (error is SEHException or COMException or InvalidOperationException) { }
            finally { request.Done.Set(); }
        }
    }

    private sealed class ReadRequest
    {
        internal readonly ManualResetEventSlim Done = new(false);
        internal string? Payload;
        internal int Selection = -1;
    }

    /// <summary>候选正文按行排列，序号是当前高亮。空行、控制字符和不含汉字的行都不采用。</summary>
    internal static bool TrySelect(string payload, int index, out string text)
    {
        text = "";
        if (index < 0 || string.IsNullOrEmpty(payload)) return false;
        int start = 0;
        int current = 0;
        for (int i = 0; i <= payload.Length; i++)
        {
            if (i != payload.Length && payload[i] != '\n') continue;
            if (current == index)
            {
                int count = i - start;
                if (count > 0 && payload[start + count - 1] == '\r') count--;
                if (count is 0 or > 120) return false;
                foreach (char character in payload.AsSpan(start, count))
                    if (char.IsControl(character)) return false;
                if (!payload.AsSpan(start, count).ContainsAnyInRange('\u3400', '\u9fff')) return false;
                text = payload.Substring(start, count);
                return true;
            }
            current++;
            start = i + 1;
        }
        return false;
    }

    internal static void Check()
    {
        if (!TrySelect("你好\n拟好\n", 0, out string first) || first != "你好")
            throw new InvalidOperationException("豆包候选没有取到第一项");
        if (!TrySelect("你好\n拟好\n", 1, out string second) || second != "拟好")
            throw new InvalidOperationException("豆包候选没有跟随高亮序号");
        if (TrySelect("你好\n拟好\n", 2, out _))
            throw new InvalidOperationException("豆包候选接受了不存在的序号");
        if (TrySelect("hello\n", 0, out _))
            throw new InvalidOperationException("豆包候选接受了不含汉字的项");
        if (TrySelect("你\n好", 0, out _) is false)
            throw new InvalidOperationException("豆包候选拒绝了单个汉字");
    }

    private static bool EnsureReader()
    {
        if (read is not null) return true;
        string? path = LocateLibrary();
        if (path is null) return false;
        module = LoadLibrary(path);
        if (module == 0) return false;
        nint procedure = GetProcAddress(module, "RpcPipe_GetCandidateListUtf8");
        if (procedure == 0) return false;
        read = Marshal.GetDelegateForFunctionPointer<GetCandidateList>(procedure);
        return true;
    }

    private static string? LocateLibrary()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("ImeService"))
            {
                using (process)
                {
                    string? directory = Path.GetDirectoryName(process.MainModule?.FileName);
                    string candidate = directory is null ? "" : Path.Combine(directory, "doubaoime-rpc-new.dll");
                    if (candidate.Length > 0 && File.Exists(candidate)) return candidate;
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DoubaoIME", "versions");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Select(directory => Path.Combine(directory, "doubaoime-rpc-new.dll"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetCandidateList(nint context, nint buffer, int capacity, out int selection, out int reserved, out int state);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibrary(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);
}
