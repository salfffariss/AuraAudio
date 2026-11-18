using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Pembantu Windows Desktop Window Manager (DWM) untuk mengaktifkan efek visual modern
/// Windows 11 (Mica, Dark Acrylic, Immersive Dark Mode, dan Custom Non-Client Frame).
/// </summary>
public static class DwmBackdropHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_MICA_EFFECT = 1029;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;

        public MARGINS(int left, int right, int top, int bottom)
        {
            cxLeftWidth = left;
            cxRightWidth = right;
            cyTopHeight = top;
            cyBottomHeight = bottom;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    public enum BackdropType
    {
        None = 1,
        Mica = 2,
        Acrylic = 3,
        Tabbed = 4 // Mica Alt
    }

    /// <summary>
    /// Menerapkan efek Immersive Dark Mode dan Mica/Acrylic backdrop pada Window WPF.
    /// </summary>
    public static bool ApplyBackdrop(Window window, BackdropType backdrop = BackdropType.Mica)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;

        if (hwnd == IntPtr.Zero)
        {
            // Jika dipanggil sebelum SourceInitialized
            window.SourceInitialized += (s, e) => ApplyBackdrop(window, backdrop);
            return false;
        }

        try
        {
            // 1. Aktifkan Mode Gelap Windows 11 (Immersive Dark Mode)
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // 2. Terapkan System Backdrop (Mica / Acrylic)
            int backdropValue = (int)backdrop;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropValue, sizeof(int));

            if (hr != 0)
            {
                // Fallback untuk Windows 11 build awal (22000)
                int trueVal = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref trueVal, sizeof(int));
            }

            // 3. Perluas bingkai kaca (Glass Frame) ke seluruh area jendela
            var margins = new MARGINS(-1, -1, -1, -1);
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // 4. Set komposisi WPF target ke transparan agar DWM Mica menembus
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
