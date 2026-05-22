using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MediaColor = System.Windows.Media.Color;

namespace BandwidthDesk.App.Services;

/// <summary>
/// Title-bar / window chrome is drawn by DWM, not by WPF. When the app runs elevated, Windows
/// prepends "Administrator: " to the title and the chrome is the most visible part of the window;
/// without this, the title bar stays light on a Dark/OLED body. <see cref="ApplyTheme"/> calls
/// the documented DWM attributes to swap the immersive title-bar mode and (on Win11) tint the
/// caption and border to match.
/// </summary>
public static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Win10 20H1+ / Win11
    private const int DWMWA_CAPTION_COLOR = 35;               // Win11
    private const int DWMWA_BORDER_COLOR = 34;                // Win11
    private const int DWMWA_TEXT_COLOR = 36;                  // Win11

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyTheme(Window window, AppTheme theme)
    {
        if (window is null) return;

        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            bool dark = theme != AppTheme.Light;
            int useDark = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            // Match the caption + border + text to the palette where possible. Failures are
            // ignored (older Windows builds simply won't honor these attribute IDs).
            int caption = ToBgrInt(LookupColor(theme, captionForBg: true));
            int border = ToBgrInt(LookupColor(theme, captionForBg: false));
            int text = ToBgrInt(LookupTextColor(theme));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
            window.SourceInitialized += (_, _) => Apply();
        else
            Apply();
    }

    private static MediaColor LookupColor(AppTheme theme, bool captionForBg) => theme switch
    {
        AppTheme.Light => captionForBg ? MediaColor.FromRgb(0xFF, 0xFF, 0xFF) : MediaColor.FromRgb(0xE2, 0xE5, 0xEC),
        AppTheme.Oled  => MediaColor.FromRgb(0x00, 0x00, 0x00),
        _              => captionForBg ? MediaColor.FromRgb(0x1E, 0x1E, 0x24) : MediaColor.FromRgb(0x31, 0x31, 0x39),
    };

    private static MediaColor LookupTextColor(AppTheme theme) => theme switch
    {
        AppTheme.Light => MediaColor.FromRgb(0x1B, 0x1D, 0x22),
        _              => MediaColor.FromRgb(0xEC, 0xEC, 0xF1),
    };

    /// <summary>DWM caption/border/text attributes take a COLORREF (0x00BBGGRR), not ARGB.</summary>
    private static int ToBgrInt(MediaColor c) => (c.B << 16) | (c.G << 8) | c.R;
}
