using System.Windows.Input;
using System.Windows.Interop;

namespace Glint.Windows.Platform;

/// <summary>
/// Registers only explicit system hotkeys. This does not install a keyboard hook or
/// observe ordinary keystrokes. Create, register and dispose on the source's UI thread.
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> callbacks = new();
    private bool disposed;

    public GlobalHotKeys(HwndSource source)
    {
        this.source = source;
        source.AddHook(WindowProc);
    }

    public bool Register(int id, ModifierKeys modifiers, Key key, Action callback)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        source.Dispatcher.VerifyAccess();
        if (id is < 0 or > 0xBFFF || key is Key.None or Key.F12) return false;
        Unregister(id);
        uint nativeModifiers = NativeMethods.ModNoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Alt)) nativeModifiers |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) nativeModifiers |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) nativeModifiers |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) nativeModifiers |= 0x0008;
        if (!NativeMethods.RegisterHotKey(source.Handle, id, nativeModifiers, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
        callbacks[id] = callback;
        return true;
    }

    public void Unregister(int id)
    {
        source.Dispatcher.VerifyAccess();
        if (callbacks.Remove(id)) NativeMethods.UnregisterHotKey(source.Handle, id);
    }

    public void Clear()
    {
        source.Dispatcher.VerifyAccess();
        foreach (var id in callbacks.Keys) NativeMethods.UnregisterHotKey(source.Handle, id);
        callbacks.Clear();
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && callbacks.TryGetValue((int)wParam, out var callback))
        {
            handled = true;
            callback();
        }
        return 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear();
        source.RemoveHook(WindowProc);
        disposed = true;
    }
}
