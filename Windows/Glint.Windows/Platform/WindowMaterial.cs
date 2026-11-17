using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Glint.Windows.Platform;

/// <summary>
/// Best-effort desktop material for a small HUD. A false result means the caller must
/// retain its readable opaque/translucent fill. No desktop pixels are captured.
/// </summary>
public static class WindowMaterial
{
    private static readonly Lazy<SetCompositionAttribute?> SetComposition = new(LoadCompositionApi);

    /// <summary>
    /// style: Frosted, System or Solid. True means Windows accepted a backdrop request;
    /// OS policy, power saving and remote sessions can still suppress the visual effect.
    /// </summary>
    public static bool Apply(nint handle, string style, bool dark)
    {
        if (handle == 0) return false;
        try
        {
            var opaque = style == "Solid" || SystemParameters.HighContrast || !TransparencyEnabled();
            var none = 1; // DWMSBT_NONE
            _ = DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
            if (opaque)
            {
                SetAccent(handle, 0, 0);
                return false;
            }

            // Accent effects on layered WPF windows can draw outside the rounded
            // WPF surface even when the native window has a rounded region.
            var extendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
            if ((extendedStyle & 0x00080000) != 0)
            {
                SetAccent(handle, 0, 0);
                return false;
            }
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                var darkMode = dark ? 1 : 0;
                _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
                var backdrop = 3; // DWMSBT_TRANSIENTWINDOW = Desktop Acrylic
                if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) == 0) return true;
                return false;
            }

            // Accent policy is intentionally isolated here: it is an OS compatibility
            // fallback rather than a guaranteed Windows API contract. Resolve the API
            // dynamically and fall back to the caller's surface on any failure.
            var accentState = style == "System" && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134) ? 4 : 3;
            return SetAccent(handle, accentState, dark ? 0xB81F1C1C : 0xB8F5F5F5);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
            System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool SetAccent(nint handle, int state, uint tint)
    {
        var api = SetComposition.Value;
        if (api is null) return false;
        var policy = new AccentPolicy { state = state, gradientColor = tint };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new CompositionAttributeData
            {
                attribute = 19, // WCA_ACCENT_POLICY
                data = pointer,
                size = (uint)Marshal.SizeOf<AccentPolicy>()
            };
            return api(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static SetCompositionAttribute? LoadCompositionApi()
    {
        // Keep the process-owned system DLL loaded for this delegate's lifetime.
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "user32.dll"), out var library)) return null;
        if (!NativeLibrary.TryGetExport(library, "SetWindowCompositionAttribute", out var address))
        {
            NativeLibrary.Free(library);
            return null;
        }
        return Marshal.GetDelegateForFunctionPointer<SetCompositionAttribute>(address);
    }

    private static bool TransparencyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("EnableTransparency") is not int value || value != 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetCompositionAttribute(nint hwnd, ref CompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        internal int attribute;
        internal nint data;
        internal uint size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        internal int state;
        internal uint flags;
        internal uint gradientColor;
        internal uint animation;
    }
}
