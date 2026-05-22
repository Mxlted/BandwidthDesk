using System;
using System.Collections.Generic;
using System.Windows;

namespace BandwidthDesk.App.Services;

public enum AppTheme
{
    Dark = 0,
    Light = 1,
    Oled = 2,
}

/// <summary>
/// Loads the chosen palette dictionary and copies its keys directly into Application.Resources.
///
/// Putting brushes at the top level (not as a sibling MergedDictionary alongside Styles.xaml)
/// avoids a WPF quirk where DynamicResource references inside Styles in a sibling dictionary
/// occasionally fail to resolve after a merged-dictionary swap.
/// </summary>
public static class ThemeManager
{
    public static event EventHandler<AppTheme>? Changed;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    private static readonly string[] PaletteKeys =
    {
        "Brush.Bg",
        "Brush.Surface",
        "Brush.SurfaceAlt",
        "Brush.SurfaceHover",
        "Brush.SurfaceActive",
        "Brush.Border",
        "Brush.BorderStrong",
        "Brush.Divider",
        "Brush.Text",
        "Brush.TextMuted",
        "Brush.TextSubtle",
        "Brush.TextOnAccent",
        "Brush.Accent",
        "Brush.AccentHover",
        "Brush.AccentPress",
        "Brush.AccentSoft",
        "Brush.Success",
        "Brush.Warning",
        "Brush.Danger",
        "Brush.DangerSoft",
        "Brush.Selection",
        "Color.Shadow",
    };

    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var uri = theme switch
        {
            AppTheme.Light => new Uri("pack://application:,,,/Resources/Palette.Light.xaml", UriKind.Absolute),
            AppTheme.Oled => new Uri("pack://application:,,,/Resources/Palette.Oled.xaml", UriKind.Absolute),
            _ => new Uri("pack://application:,,,/Resources/Palette.Dark.xaml", UriKind.Absolute),
        };

        var palette = new ResourceDictionary { Source = uri };

        // Copy palette keys directly into Application.Resources. DynamicResource lookups
        // hit the application scope reliably regardless of merged-dictionary plumbing.
        foreach (var key in PaletteKeys)
        {
            if (palette.Contains(key))
                app.Resources[key] = palette[key];
        }

        Current = theme;
        Changed?.Invoke(null, theme);
    }
}
