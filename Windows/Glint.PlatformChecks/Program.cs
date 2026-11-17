using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Glint.Windows.Platform;

namespace Glint.PlatformChecks;

/// <summary>
/// Cross-process protocol fixture, not a real IME compatibility test. Its native host uses
/// the existing production IME class allowlist; production candidate rules are unchanged.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Contains("--fixture")) return RunFixture();
        try { RunChecks().GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }

    private static async Task RunChecks()
    {
        Check(Marshal.SizeOf<NativeMethods.INPUT>() == (nint.Size == 8 ? 40 : 28), "native INPUT structure layout");
        Check(NativeMethods.IsOrdinaryProcess((uint)Environment.ProcessId), "ordinary process integrity accepted");
        Check(!NativeMethods.IsOrdinaryProcess(0) && !NativeMethods.IsOrdinaryProcess(4), "unknown and protected process integrity rejected");
        using var monitor = new CandidateMonitor(() => 100);
        using var fixture = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!, "--fixture")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        var phases = new ConcurrentDictionary<string, bool>();
        var errors = new ConcurrentQueue<string>();
        var invalidations = 0;
        var endings = 0;
        monitor.CandidateInvalidated += () => Interlocked.Increment(ref invalidations);
        monitor.CandidateChanged += candidate => { if (candidate is null) Interlocked.Increment(ref endings); };
        fixture.OutputDataReceived += (_, args) => { if (args.Data is { } data) phases[data] = true; };
        fixture.ErrorDataReceived += (_, args) => { if (args.Data is { } data) errors.Enqueue(data); };
        monitor.Start();
        try
        {
            fixture.Start();
            fixture.BeginOutputReadLine();
            fixture.BeginErrorReadLine();
            await WaitUntil(() => phases.ContainsKey("phase:first") && monitor.CurrentSnapshot is not null, "first stable candidate");
            var first = monitor.CurrentSnapshot!;
            Check(first.Bounds.Width > 0 && first.Bounds.Height > 0, "physical candidate bounds");
            await WaitUntil(() => phases.ContainsKey("phase:changed") && monitor.CurrentSnapshot is { } candidate && candidate.Text != first.Text, "changed stable candidate");
            var second = monitor.CurrentSnapshot!;
            Check(second.Revision > first.Revision && invalidations >= 2, "candidate revision and immediate invalidation");
            var insertion = new TextInsertionService();
            Check(!await insertion.TryInsertAsync(first, "stale-test"), "stale candidate insertion rejected");

            await WaitUntil(() => phases.ContainsKey("phase:password") && monitor.CurrentSnapshot is null, "password focus suppression");
            Check(endings > 0, "unsafe focus ends candidate session");
            Check(!await insertion.TryInsertAsync(second, "password-test"), "password insertion rejected");
            await WaitUntil(() => phases.ContainsKey("phase:restored") && monitor.CurrentSnapshot is { } candidate && candidate.Revision > second.Revision, "safe focus recovery");
            var restored = monitor.CurrentSnapshot!;
            Check(await insertion.TryInsertAsync(restored, "glint-test"), "composition cancellation and Unicode insertion");
            await WaitUntil(() => phases.ContainsKey("inserted:True"), "fixture receives exact inserted text");
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(fixture.ExitCode == 0, "fixture exits cleanly");
            monitor.Stop();
            await Task.Delay(250);
            monitor.Start();
            await Task.Delay(250);
            monitor.Stop();
            Check(monitor.CurrentSnapshot is null, "monitor stop/start lifecycle");
            Console.WriteLine("PASS: cross-process IME protocol fixture completed; real Microsoft Pinyin remains a manual acceptance check.");
        }
        catch
        {
            foreach (var error in errors) Console.Error.WriteLine(error);
            throw;
        }
        finally
        {
            if (fixture.Id != 0 && !fixture.HasExited) fixture.Kill();
        }
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }

    private static async Task WaitUntil(Func<bool> condition, string name)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    private static int RunFixture()
    {
        var application = new Application();
        var editor = new TextBox { Height = 38, FontSize = 18, Margin = new Thickness(20) };
        var password = new PasswordBox { Height = 38, Margin = new Thickness(20) };
        var layout = new StackPanel();
        layout.Children.Add(new TextBlock { Text = "Glint 输入法协议自测窗口", Margin = new Thickness(20, 16, 20, 0) });
        layout.Children.Add(editor);
        layout.Children.Add(password);
        var window = new Window { Title = "Glint protocol test fixture", Width = 500, Height = 240, Left = 320, Top = 250, Content = layout };
        nint popup = 0;
        HwndSource? source = null;
        editor.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && popup != 0) { FixtureNative.ShowWindow(popup, 0); args.Handled = true; }
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(350);
                editor.Focus();
                Keyboard.Focus(editor);
                var owner = new WindowInteropHelper(window).Handle;
                popup = FixtureNative.CreateCandidateHost(owner);
                source = new HwndSource(new HwndSourceParameters("Glint IME protocol children")
                {
                    ParentWindow = popup,
                    WindowStyle = 0x50000000, // visible child
                    Width = 360,
                    Height = 76
                });
                var candidates = new ListBox { Width = 360, Height = 76, Background = Brushes.White, SelectionMode = SelectionMode.Single };
                AutomationProperties.SetAutomationId(candidates, "IME_Candidate_Window");
                candidates.Items.Add(new ListBoxItem { Content = "你好" });
                candidates.Items.Add(new ListBoxItem { Content = "世界" });
                candidates.SelectedIndex = 0;
                source.RootVisual = candidates;
                FixtureNative.ShowWindow(popup, 4); // show without activation
                Console.WriteLine("phase:first");
                await Task.Delay(1800);
                candidates.SelectedIndex = 1;
                Console.WriteLine("phase:changed");
                await Task.Delay(1800);
                password.Focus();
                Keyboard.Focus(password);
                Console.WriteLine("phase:password");
                await Task.Delay(1800);
                editor.Focus();
                Keyboard.Focus(editor);
                Console.WriteLine("phase:restored");
                await Task.Delay(2800);
                Console.WriteLine("inserted:" + (editor.Text == "glint-test"));
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
            finally
            {
                source?.Dispose();
                if (popup != 0) FixtureNative.DestroyWindow(popup);
                window.Close();
            }
        };
        application.Run(window);
        return Environment.ExitCode;
    }
}

internal static class FixtureNative
{
    private static readonly WndProc Callback = WindowProc;
    internal static nint CreateCandidateHost(nint owner)
    {
        var module = GetModuleHandle(null);
        var registration = new WNDCLASSEX
        {
            size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            procedure = Callback,
            instance = module,
            className = "IME_Candidate_Window"
        };
        if (RegisterClassEx(ref registration) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var hwnd = CreateWindowEx(0x08000088, registration.className, "", unchecked((int)0x80000000), 350, 490, 360, 76, owner, 0, module, 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return hwnd;
    }
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam) => DefWindowProc(hwnd, message, wParam, lParam);
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        internal uint size, style;
        internal WndProc procedure;
        internal int classExtra, windowExtra;
        internal nint instance, icon, cursor, background;
        internal string? menuName;
        internal string className;
        internal nint smallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX registration);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
}
