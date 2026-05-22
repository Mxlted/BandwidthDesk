using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace BandwidthDesk.App.Services;

/// <summary>
/// Extracts small (16px) icons from executable paths via SHGetFileInfo and caches them
/// as frozen <see cref="ImageSource"/>s keyed by full path. The cache is process-wide.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Returns a small icon (16x16-ish) for the given executable path, or null if none can be
    /// extracted. Result is frozen and safe to share across threads. Lookups are cached.
    /// </summary>
    public static ImageSource? GetIconForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _cache.GetOrAdd(path, LoadIcon);
    }

    private static ImageSource? LoadIcon(string path)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;

            // Fall back to file-attributes mode when the file isn't accessible (e.g. protected
            // process). That still yields a generic icon based on extension.
            if (!File.Exists(path))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var hr = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

            if (hr == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            hIcon = shfi.hIcon;
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract icon for {Path}", path);
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }
}
