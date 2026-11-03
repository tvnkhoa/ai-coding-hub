using System;
using System.Runtime.InteropServices;

namespace AiCodingHub.App.Interop;

/// <summary>
/// Win32/DWM helpers for the overlay: rounded corners, tool-window (hide from Alt-Tab), the
/// Windows 11 acrylic system backdrop and immersive dark mode. All best-effort — failures are
/// swallowed so the HUD still works on older Windows builds (it then draws an opaque tint).
/// </summary>
public static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic

    /// <summary>The system backdrop attribute exists from Windows 11 22H2 (build 22621).</summary>
    public static bool SupportsSystemBackdrop =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    public static void EnableRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // pre-Win11 — square corners
        }
    }

    /// <summary>
    /// Turns on the acrylic backdrop behind the (transparent) client area. The window must be
    /// non-layered (<c>AllowsTransparency=False</c>) with its glass frame extended over the whole
    /// client area (WindowChrome <c>GlassFrameThickness=-1</c>). Returns false when unsupported.
    /// </summary>
    public static bool TryEnableBackdrop(IntPtr hwnd, bool dark)
    {
        if (!SupportsSystemBackdrop)
        {
            return false;
        }

        try
        {
            SetDarkMode(hwnd, dark);
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var type = DWMSBT_TRANSIENTWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Makes DWM draw the frame/backdrop in its dark variant (follows the HUD theme).</summary>
    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        try
        {
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
        }
    }

    public static void SetToolWindow(IntPtr hwnd, bool enabled)
    {
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = enabled ? ex | WS_EX_TOOLWINDOW : ex & ~WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);
}
