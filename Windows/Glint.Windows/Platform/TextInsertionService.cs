using System.Runtime.InteropServices;

namespace Glint.Windows.Platform;

/// <summary>User-invoked insertion; no clipboard access and no focus stealing.</summary>
public sealed class TextInsertionService
{
    private int inserting;

    public Task<bool> TryInsertAsync(CandidateSnapshot snapshot, string text, CancellationToken cancellation = default) =>
        Task.Run(() => TryInsert(snapshot, text, cancellation), cancellation);

    /// <summary>Call on a worker thread. False includes stale focus, unsafe fields and UIPI restrictions.</summary>
    public bool TryInsert(CandidateSnapshot snapshot, string text) => TryInsert(snapshot, text, default);

    private bool TryInsert(CandidateSnapshot snapshot, string text, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || text.Any(char.IsControl) ||
            Interlocked.CompareExchange(ref inserting, 1, 0) != 0) return false;
        try
        {
            // Hotkeys arrive while their modifiers are still down. Wait for the user to
            // release them instead of generating synthetic modifier-up events.
            var deadline = Environment.TickCount64 + 650;
            while (!NativeMethods.ModifiersAreReleased())
            {
                if (cancellation.IsCancellationRequested || Environment.TickCount64 >= deadline ||
                    NativeMethods.GetForegroundWindow() != snapshot.ForegroundWindow) return false;
                Thread.Sleep(10);
            }
            if (cancellation.IsCancellationRequested || !StillSafe(snapshot)) return false;
            var candidate = CandidateReader.ReadCurrent();
            if (!CandidateReader.SameCandidate(snapshot, candidate)) return false;
            var escape = new[] { NativeMethods.Key(NativeMethods.VkEscape), NativeMethods.Key(NativeMethods.VkEscape, true) };
            if (NativeMethods.SendInput((uint)escape.Length, escape, Marshal.SizeOf<NativeMethods.INPUT>()) != escape.Length) return false;

            // Escape and Unicode must be separate transactions: wait for the IME to
            // acknowledge that composition ended before emitting the translation.
            deadline = Environment.TickCount64 + 400;
            while (true)
            {
                if (cancellation.IsCancellationRequested || !StillSafe(snapshot) || !NativeMethods.ModifiersAreReleased()) return false;
                if (CandidateReader.ReadCurrent() is null) break;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(15);
            }
            if (!StillSafe(snapshot) || !NativeMethods.ModifiersAreReleased()) return false;
            var inputs = new NativeMethods.INPUT[text.Length * 2];
            for (var index = 0; index < text.Length; index++)
            {
                inputs[index * 2] = NativeMethods.Unicode(text[index]);
                inputs[index * 2 + 1] = NativeMethods.Unicode(text[index], true);
            }
            return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Length;
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or
            System.Windows.Automation.ElementNotAvailableException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            Volatile.Write(ref inserting, 0);
        }
    }

    private static bool StillSafe(CandidateSnapshot snapshot) =>
        NativeMethods.GetForegroundWindow() == snapshot.ForegroundWindow &&
        CandidateReader.TryGetSafeFocus(out var foreground, out var identity, out var focusWindow) &&
        foreground == snapshot.ForegroundWindow && identity == snapshot.FocusIdentity &&
        focusWindow == snapshot.FocusWindow;
}
