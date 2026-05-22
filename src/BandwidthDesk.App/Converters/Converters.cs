using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BandwidthDesk.App.Services;
using BandwidthDesk.Core.Throttling;

namespace BandwidthDesk.App.Converters;

/// <summary>
/// MultiValue: [header label, this column's sort key (string), current sort key, descending].
/// Returns the label with a ▲/▼ chevron appended when this column is active.
/// </summary>
public sealed class SortHeaderTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4) return string.Empty;
        string label = values[0] as string ?? string.Empty;
        string thisKey = values[1] as string ?? string.Empty;
        if (values[2] is not ProcessSortKey current) return label;
        bool descending = values[3] is bool b && b;
        if (!Enum.TryParse<ProcessSortKey>(thisKey, out var parsed) || parsed != current) return label;
        return descending ? $"{label} ▼" : $"{label} ▲";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ByteRateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        long bps = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
        bool isLive = parameter is string s && s.Equals("live", StringComparison.OrdinalIgnoreCase);
        if (bps <= 0) return isLive ? "0 B/s" : "Unlimited";
        return Format(bps);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static string Format(long bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond} B/s";
        double kb = bytesPerSecond / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB/s";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.##} MB/s";
        double gb = mb / 1024.0;
        return $"{gb:0.##} GB/s";
    }
}

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        long bytes = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool x && x;
        bool invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns the muted text brush when rate == 0, the accent brush when there's traffic.
/// Looked up at use time from the current theme via Application.Current.Resources.
/// </summary>
public sealed class RateActivityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long v = value is null ? 0 : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
        string key = v > 0 ? "Brush.Text" : "Brush.TextSubtle";
        return Application.Current?.TryFindResource(key) ?? System.Windows.DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EngineStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is EngineStatus s
            ? s switch
            {
                EngineStatus.Running => "Brush.Success",
                EngineStatus.Starting => "Brush.Warning",
                EngineStatus.Faulted => "Brush.Danger",
                _ => "Brush.TextSubtle",
            }
            : "Brush.TextSubtle";
        return Application.Current?.TryFindResource(key) ?? System.Windows.DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
