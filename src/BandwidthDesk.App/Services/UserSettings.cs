using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BandwidthDesk.Core;
using Serilog;

namespace BandwidthDesk.App.Services;

public sealed class UserSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool HideMicrosoftProcesses { get; set; } = true;
    public ProcessSortKey ProcessSort { get; set; } = ProcessSortKey.Name;
    public bool ProcessSortDescending { get; set; }

    /// <summary>Default unit pre-selected in the rule editor for new rules.</summary>
    public RateUnit DefaultRateUnit { get; set; } = RateUnit.KBps;

    /// <summary>How often the process list refreshes, in seconds. Clamped to 1..30 at use sites.</summary>
    public int ProcessRefreshSeconds { get; set; } = 3;

    /// <summary>Hide the main window and keep the app available from the tray when minimized.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Hide the main window instead of exiting when the user clicks the close button.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Show a one-time tray hint when the app first hides to the notification area.</summary>
    public bool ShowTrayNotifications { get; set; } = true;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
}

public enum ProcessSortKey
{
    Name,
    Instances,
    Memory,
    Download,
    Upload,
}

public enum RateUnit
{
    Bps,
    KBps,
    MBps,
}

public static class UserSettingsStore
{
    private static readonly string Path = System.IO.Path.Combine(AppPaths.DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new UserSettings();
            var text = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<UserSettings>(text, Json) ?? new UserSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load user settings; using defaults");
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tempPath = Path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, Json));
            File.Move(tempPath, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save user settings");
        }
    }
}
