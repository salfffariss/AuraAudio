using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Windows 11 DWM helper enforcing pure-monochrome (R = G = B) solid window chrome:
/// immersive dark mode, disabled system backdrop (no Mica/Acrylic wallpaper hue bleed),
/// neutral caption/border COLORREFs matching NeutralTokens.xaml, and hardware rounded corners.
/// </summary>
public static class DwmBackdropHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    // Pure neutral COLORREFs (0x00BBGGRR where R = G = B, matching NeutralTokens.xaml)
    private const int CAPTION_COLOR_NEUTRAL = 0x000B0B0B; // Neutral.05
    private const int BORDER_COLOR_NEUTRAL = 0x00323232;  // Neutral.25
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    public enum BackdropType
    {
        None = 1
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// Applies solid pure-neutral Windows 11 chrome (DarkMode = 1, SystemBackdrop = None,
    /// caption #0B0B0B, border #323232, hardware rounded corners when unmaximized).
    /// </summary>
    public static void ApplyChrome(Window window)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;

        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyChrome(window);
            return;
        }

        try
        {
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int noneBackdrop = (int)BackdropType.None;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref noneBackdrop, sizeof(int));

            int captionColor = CAPTION_COLOR_NEUTRAL;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            int borderColor = BORDER_COLOR_NEUTRAL;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            SetCornerPreference(window, window.WindowState == WindowState.Maximized);
        }
        catch
        {
            // Safely ignored on pre-Windows 11 hosts.
        }
    }

    /// <summary>
    /// Updates hardware window corner rounding (rounded in Normal state, square when Maximized).
    /// </summary>
    public static void SetCornerPreference(Window window, bool isMaximized)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int cornerPref = isMaximized ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            int borderColor = isMaximized ? DWMWA_COLOR_NONE : BORDER_COLOR_NEUTRAL;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        }
        catch
        {
            // Safely ignored on Windows 10.
        }
    }
}
