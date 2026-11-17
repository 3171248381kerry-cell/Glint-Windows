using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Glint.Windows.Platform;

internal sealed class CompositionBackdrop : IDisposable
{
    private nint _native;
    private bool _attempted;
    internal int Error { get; private set; }

    public bool Show(nint foreground, int x, int y, int width, int height, double scale)
    {
        if (SystemParameters.HighContrast || !TransparencyEnabled()) { Hide(); return false; }
        try
        {
            if (!_attempted) { _attempted = true; _native = GlintHudCreate(); if (_native == 0) Error = GlintHudLastError(); }
            if (_native == 0) return false;
            if (GlintHudShow(_native, foreground, x, y, width, height, (float)(9 * scale)) != 0) return true;
            Hide(); return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    }
    public void SetOpacity(double value) { if (_native != 0) GlintHudOpacity(_native, (float)Math.Clamp(value, 0, 1)); }
    public void Hide() { if (_native != 0) GlintHudHide(_native); }
    public void Dispose() { if (_native != 0) { GlintHudDestroy(_native); _native = 0; } }
    private static bool TransparencyEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("EnableTransparency") is not int value || value != 0; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
    [DllImport("HudComposition.dll", CallingConvention = CallingConvention.Cdecl)] private static extern nint GlintHudCreate();
    [DllImport("HudComposition.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int GlintHudLastError();
    [DllImport("HudComposition.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int GlintHudShow(nint handle, nint foreground, int x, int y, int width, int height, float radius);
    [DllImport("HudComposition.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void GlintHudOpacity(nint handle, float opacity);
    [DllImport("HudComposition.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void GlintHudHide(nint handle);
    [DllImport("HudComposition.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void GlintHudDestroy(nint handle);
}
