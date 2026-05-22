using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BandwidthDesk.App.Services;
using BandwidthDesk.App.Views;
using BandwidthDesk.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using WpfApplication = System.Windows.Application;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Win32SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BandwidthDesk.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<UserSettings> _getSettings;
    private readonly Action<UserSettings> _saveSettings;

    public ObservableCollection<string> Profiles { get; } = new();

    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private bool _hideMicrosoftProcesses;
    [ObservableProperty] private RateUnit _defaultRateUnit;
    [ObservableProperty] private int _processRefreshSeconds;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _showTrayNotifications;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _profilesDirectory = ProfileStore.Directory;
    [ObservableProperty] private string _dataDirectory = BandwidthDesk.Core.AppPaths.DataDirectory;
    [ObservableProperty] private string _logsDirectory = BandwidthDesk.Core.AppPaths.LogDirectory;

    public bool IsThemeDark
    {
        get => Theme == AppTheme.Dark;
        set { if (value) Theme = AppTheme.Dark; }
    }
    public bool IsThemeLight
    {
        get => Theme == AppTheme.Light;
        set { if (value) Theme = AppTheme.Light; }
    }
    public bool IsThemeOled
    {
        get => Theme == AppTheme.Oled;
        set { if (value) Theme = AppTheme.Oled; }
    }

    public bool IsUnitBps
    {
        get => DefaultRateUnit == RateUnit.Bps;
        set { if (value) DefaultRateUnit = RateUnit.Bps; }
    }
    public bool IsUnitKBps
    {
        get => DefaultRateUnit == RateUnit.KBps;
        set { if (value) DefaultRateUnit = RateUnit.KBps; }
    }
    public bool IsUnitMBps
    {
        get => DefaultRateUnit == RateUnit.MBps;
        set { if (value) DefaultRateUnit = RateUnit.MBps; }
    }

    public SettingsViewModel(AppServices services, Func<UserSettings> getSettings, Action<UserSettings> saveSettings)
    {
        _services = services;
        _getSettings = getSettings;
        _saveSettings = saveSettings;

        var s = _getSettings();
        _theme = s.Theme;
        _hideMicrosoftProcesses = s.HideMicrosoftProcesses;
        _defaultRateUnit = s.DefaultRateUnit;
        _processRefreshSeconds = Math.Clamp(s.ProcessRefreshSeconds, 1, 30);
        _minimizeToTray = s.MinimizeToTray;
        _closeToTray = s.CloseToTray;
        _showTrayNotifications = s.ShowTrayNotifications;

        RefreshProfileList();
    }

    public bool IsRefreshFast    { get => ProcessRefreshSeconds == 1; set { if (value) ProcessRefreshSeconds = 1; } }
    public bool IsRefreshNormal  { get => ProcessRefreshSeconds == 3; set { if (value) ProcessRefreshSeconds = 3; } }
    public bool IsRefreshSlow    { get => ProcessRefreshSeconds == 10; set { if (value) ProcessRefreshSeconds = 10; } }

    partial void OnProcessRefreshSecondsChanged(int value)
    {
        var s = _getSettings();
        s.ProcessRefreshSeconds = value;
        _saveSettings(s);
        OnPropertyChanged(nameof(IsRefreshFast));
        OnPropertyChanged(nameof(IsRefreshNormal));
        OnPropertyChanged(nameof(IsRefreshSlow));
    }

    partial void OnThemeChanged(AppTheme value)
    {
        ThemeManager.Apply(value);
        var s = _getSettings();
        s.Theme = value;
        _saveSettings(s);
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeOled));
    }

    partial void OnHideMicrosoftProcessesChanged(bool value)
    {
        var s = _getSettings();
        s.HideMicrosoftProcesses = value;
        _saveSettings(s);
    }

    partial void OnDefaultRateUnitChanged(RateUnit value)
    {
        var s = _getSettings();
        s.DefaultRateUnit = value;
        _saveSettings(s);
        OnPropertyChanged(nameof(IsUnitBps));
        OnPropertyChanged(nameof(IsUnitKBps));
        OnPropertyChanged(nameof(IsUnitMBps));
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        var s = _getSettings();
        s.MinimizeToTray = value;
        _saveSettings(s);
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        var s = _getSettings();
        s.CloseToTray = value;
        _saveSettings(s);
    }

    partial void OnShowTrayNotificationsChanged(bool value)
    {
        var s = _getSettings();
        s.ShowTrayNotifications = value;
        _saveSettings(s);
    }

    [RelayCommand]
    private async Task SaveCurrentAsProfile()
    {
        var name = (NewProfileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status("Enter a name for the new profile first.");
            return;
        }

        if (Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var ok = ThemedDialog.Show(
                WpfApplication.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault(),
                "Overwrite profile?",
                $"A profile named '{name}' already exists. Overwrite it?",
                ThemedDialogKind.Question, ThemedDialogButtons.YesNo);
            if (ok != ThemedDialogResult.Yes) return;
        }

        var settings = _getSettings();
        var profile = new Profile
        {
            Name = name,
            Theme = settings.Theme,
            HideMicrosoftProcesses = settings.HideMicrosoftProcesses,
            Rules = _services.RuleManager.Rules.Select(CloneRule).ToList(),
        };

        try
        {
            await _services.ProfileStore.SaveAsync(profile);
            NewProfileName = "";
            RefreshProfileList();
            SelectedProfile = name;
            Status($"Saved profile '{name}'.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save profile");
            Status("Failed to save: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ApplyProfile()
    {
        if (string.IsNullOrEmpty(SelectedProfile)) return;

        var profile = await _services.ProfileStore.LoadAsync(SelectedProfile);
        if (profile is null)
        {
            Status($"Could not load profile '{SelectedProfile}'.");
            return;
        }

        var confirm = ThemedDialog.Show(
            WpfApplication.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault(),
            $"Apply profile '{profile.Name}'?",
            $"This replaces your current rules with {profile.Rules.Count} rule(s) from the profile and switches the theme.",
            ThemedDialogKind.Question, ThemedDialogButtons.OkCancel);
        if (confirm != ThemedDialogResult.Ok) return;

        try
        {
            await _services.RuleManager.ReplaceAllAsync(profile.Rules.Select(CloneRule));

            var settings = _getSettings();
            settings.Theme = profile.Theme;
            settings.HideMicrosoftProcesses = profile.HideMicrosoftProcesses;
            _saveSettings(settings);

            Theme = profile.Theme;
            HideMicrosoftProcesses = profile.HideMicrosoftProcesses;

            Status($"Applied profile '{profile.Name}' ({profile.Rules.Count} rules).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply profile");
            Status("Failed to apply: " + ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (string.IsNullOrEmpty(SelectedProfile)) return;

        var confirm = ThemedDialog.Show(
            WpfApplication.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault(),
            $"Delete profile '{SelectedProfile}'?",
            "This cannot be undone.",
            ThemedDialogKind.Warning, ThemedDialogButtons.YesNo);
        if (confirm != ThemedDialogResult.Yes) return;

        try
        {
            _services.ProfileStore.Delete(SelectedProfile);
            var deleted = SelectedProfile;
            RefreshProfileList();
            SelectedProfile = null;
            Status($"Deleted profile '{deleted}'.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete profile");
            Status("Failed to delete: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportProfile()
    {
        if (string.IsNullOrEmpty(SelectedProfile)) return;

        var profile = await _services.ProfileStore.LoadAsync(SelectedProfile);
        if (profile is null)
        {
            Status($"Could not load profile '{SelectedProfile}'.");
            return;
        }

        var dlg = new Win32SaveFileDialog
        {
            Title = "Export profile",
            FileName = SelectedProfile + ".bwprofile.json",
            DefaultExt = ".json",
            Filter = "BandwidthDesk profile (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _services.ProfileStore.ExportAsync(profile, dlg.FileName);
            Status($"Exported to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export profile");
            Status("Export failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportProfile()
    {
        var dlg = new Win32OpenFileDialog
        {
            Title = "Import profile",
            Filter = "BandwidthDesk profile (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var profile = await _services.ProfileStore.ImportAsync(dlg.FileName);
            if (profile is null)
            {
                Status("Import failed: file is not a valid BandwidthDesk profile.");
                return;
            }
            RefreshProfileList();
            SelectedProfile = profile.Name;
            Status($"Imported as '{profile.Name}'. Click Apply to use it.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to import profile");
            Status("Import failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenProfilesFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ProfileStore.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status("Could not open folder: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = BandwidthDesk.Core.AppPaths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status("Could not open folder: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = BandwidthDesk.Core.AppPaths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status("Could not open folder: " + ex.Message);
        }
    }

    private void RefreshProfileList()
    {
        var names = _services.ProfileStore.ListProfileNames();
        Profiles.Clear();
        foreach (var n in names) Profiles.Add(n);
    }

    private void Status(string message)
    {
        StatusMessage = message;
        Log.Information(message);
    }

    private static BandwidthRule CloneRule(BandwidthRule r) => new()
    {
        Id = Guid.NewGuid(),
        Name = r.Name,
        MatchKind = r.MatchKind,
        MatchValue = r.MatchValue,
        DownloadBytesPerSecond = r.DownloadBytesPerSecond,
        UploadBytesPerSecond = r.UploadBytesPerSecond,
        Enabled = r.Enabled,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc,
    };
}
