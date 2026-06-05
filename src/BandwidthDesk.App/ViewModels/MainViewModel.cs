using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BandwidthDesk.App.Services;
using BandwidthDesk.App.Views;
using BandwidthDesk.Core.Models;
using BandwidthDesk.Core.Throttling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using WpfApplication = System.Windows.Application;

namespace BandwidthDesk.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _processTimer;
    private readonly Dispatcher _ui;
    private int _processRefreshVersion;

    public ObservableCollection<ProcessGroupViewModel> ProcessGroups { get; } = new();
    public ObservableCollection<RuleRowViewModel> Rules { get; } = new();

    [ObservableProperty] private string _processFilter = string.Empty;
    [ObservableProperty] private object? _selectedProcessNode;
    [ObservableProperty] private RuleRowViewModel? _selectedRule;
    [ObservableProperty] private EngineStatus _engineStatus = EngineStatus.Stopped;
    [ObservableProperty] private string _engineStatusMessage = "Stopped";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private long _totalDownloadBps;
    [ObservableProperty] private long _totalUploadBps;
    [ObservableProperty] private bool _hideMicrosoftProcesses = true;
    [ObservableProperty] private ProcessSortKey _sortKey = ProcessSortKey.Name;
    [ObservableProperty] private bool _sortDescending;

    public bool IsThemeDark
    {
        get => Theme == AppTheme.Dark;
        set { if (value) SetTheme(AppTheme.Dark); }
    }
    public bool IsThemeLight
    {
        get => Theme == AppTheme.Light;
        set { if (value) SetTheme(AppTheme.Light); }
    }
    public bool IsThemeOled
    {
        get => Theme == AppTheme.Oled;
        set { if (value) SetTheme(AppTheme.Oled); }
    }

    private UserSettings _settings;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _ui = WpfApplication.Current.Dispatcher;
        IsElevated = App.IsElevated();
        _theme = ThemeManager.Current;

        _settings = UserSettingsStore.Load();
        _hideMicrosoftProcesses = _settings.HideMicrosoftProcesses;
        _sortKey = _settings.ProcessSort;
        _sortDescending = _settings.ProcessSortDescending;

        _services.Engine.StatusChanged += OnEngineStatusChanged;
        _services.Engine.Throughput += OnThroughput;
        _services.Engine.ProcessThroughput += OnProcessThroughput;

        ThemeManager.Changed += (_, t) => _ui.Invoke(() =>
        {
            Theme = t;
            OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(IsThemeLight));
            OnPropertyChanged(nameof(IsThemeOled));
            RefreshThemeBindings();
        });

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ClampRefreshSeconds(_settings.ProcessRefreshSeconds)) };
        _processTimer.Tick += (_, _) => _ = RefreshProcessesAsync();
    }

    private static int ClampRefreshSeconds(int seconds) => Math.Clamp(seconds, 1, 30);

    /// <summary>Called by the Settings dialog when the user picks a new polling interval.</summary>
    public void ApplyRefreshInterval(int seconds)
    {
        seconds = ClampRefreshSeconds(seconds);
        _processTimer.Interval = TimeSpan.FromSeconds(seconds);
    }

    public async Task InitializeAsync()
    {
        await _services.RuleManager.LoadAsync();
        SyncRulesFromManager();
        _services.RuleManager.Rules.CollectionChanged += (_, _) => SyncRulesFromManager();
        ShowStartupPersistenceWarnings();

        await RefreshProcessesAsync();
        _processTimer.Start();

        if (IsElevated)
        {
            try
            {
                _services.Engine.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start engine");
                EngineStatusMessage = ex.Message;
            }
        }
        else
        {
            EngineStatusMessage = "Not elevated — restart as Administrator to apply limits.";
        }
    }

    private void ShowStartupPersistenceWarnings()
    {
        var owner = WpfApplication.Current.MainWindow;
        if (!string.IsNullOrWhiteSpace(_services.RuleManager.LastLoadWarningMessage))
        {
            ThemedDialog.Show(owner,
                "Rules file was preserved",
                _services.RuleManager.LastLoadWarningMessage,
                ThemedDialogKind.Warning,
                ThemedDialogButtons.Ok);
        }

        var settingsWarning = UserSettingsStore.ConsumeLastLoadWarning();
        if (!string.IsNullOrWhiteSpace(settingsWarning))
        {
            ThemedDialog.Show(owner,
                "Settings file was preserved",
                settingsWarning,
                ThemedDialogKind.Warning,
                ThemedDialogButtons.Ok);
        }
    }

    private void SetTheme(AppTheme theme)
    {
        if (Theme == theme) return;
        ThemeManager.Apply(theme);
        _settings.Theme = theme;
        UserSettingsStore.Save(_settings);
    }

    /// <summary>
    /// Force bindings driven by value-converters that resolve theme brushes (RateActivityConverter,
    /// EngineStatusToBrushConverter) to re-evaluate. They capture a Brush at convert time, so the
    /// stale brush would survive a theme swap until the underlying value next changed.
    /// </summary>
    private void RefreshThemeBindings()
    {
        OnPropertyChanged(nameof(EngineStatus));
        foreach (var group in ProcessGroups)
        {
            group.NotifyThemeChanged();
            foreach (var child in group.Children) child.NotifyThemeChanged();
        }
        foreach (var rule in Rules) rule.NotifyThemeChanged();
    }

    partial void OnHideMicrosoftProcessesChanged(bool value)
    {
        _settings.HideMicrosoftProcesses = value;
        UserSettingsStore.Save(_settings);
        _ = RefreshProcessesAsync();
    }

    partial void OnSortKeyChanged(ProcessSortKey value)
    {
        _settings.ProcessSort = value;
        UserSettingsStore.Save(_settings);
        _ui.Invoke(ApplySortInPlace);
    }

    partial void OnSortDescendingChanged(bool value)
    {
        _settings.ProcessSortDescending = value;
        UserSettingsStore.Save(_settings);
        _ui.Invoke(ApplySortInPlace);
    }

    [RelayCommand]
    private void SetSort(string keyName)
    {
        if (!Enum.TryParse<ProcessSortKey>(keyName, out var key)) return;
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            // Memory / Download / Upload default to descending — that's what users want.
            SortDescending = key is ProcessSortKey.Memory or ProcessSortKey.Download
                                  or ProcessSortKey.Upload or ProcessSortKey.Instances;
        }
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        var refreshVersion = Interlocked.Increment(ref _processRefreshVersion);
        try
        {
            var procs = await Task.Run(() => _services.ProcessService.GetProcesses(includeSystem: false));
            await _ui.InvokeAsync(() =>
            {
                if (refreshVersion == Volatile.Read(ref _processRefreshVersion))
                    RebuildGroups(procs);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Process refresh failed");
        }
    }

    private void RebuildGroups(IReadOnlyList<ProcessInfo> procs)
    {
        var filter = ProcessFilter?.Trim();

        // Bucket incoming processes by exe name, applying filters first.
        var incoming = new Dictionary<string, List<ProcessInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (HideMicrosoftProcesses && p.IsMicrosoft) continue;
            if (!string.IsNullOrEmpty(filter)
                && !p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && (p.Description is null || !p.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                && (p.CompanyName is null || !p.CompanyName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!incoming.TryGetValue(p.Name, out var list))
                incoming[p.Name] = list = new List<ProcessInfo>();
            list.Add(p);
        }

        // Reconcile groups in place. Updating existing VMs (instead of recreating them) is what
        // keeps TreeView selection alive across the 3s refresh.
        var existingGroups = ProcessGroups.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

        // Remove groups that no longer have any matching process.
        foreach (var name in existingGroups.Keys.ToList())
        {
            if (!incoming.ContainsKey(name))
            {
                ProcessGroups.Remove(existingGroups[name]);
                existingGroups.Remove(name);
            }
        }

        // Add or update each surviving / new group.
        foreach (var (name, items) in incoming)
        {
            if (!existingGroups.TryGetValue(name, out var group))
            {
                var first = items[0];
                group = new ProcessGroupViewModel(name)
                {
                    Description = string.IsNullOrWhiteSpace(first.Description) ? "—" : first.Description!,
                    ExecutablePath = first.ExecutablePath ?? "—",
                    IsMicrosoft = first.IsMicrosoft,
                };
                existingGroups.Add(name, group);
                ProcessGroups.Add(group);
            }
            else
            {
                // Refresh group-level metadata in case it improved between scans.
                var first = items[0];
                var desc = string.IsNullOrWhiteSpace(first.Description) ? "—" : first.Description!;
                if (group.Description != desc) group.Description = desc;
                var path = first.ExecutablePath ?? "—";
                if (group.ExecutablePath != path) group.ExecutablePath = path;
            }

            // Reconcile children by PID.
            var existingChildren = group.Children.ToDictionary(c => c.ProcessId);
            var seenPids = new HashSet<int>();
            foreach (var p in items)
            {
                seenPids.Add(p.ProcessId);
                if (existingChildren.TryGetValue(p.ProcessId, out var row))
                    row.UpdateInfo(p);
                else
                    group.Children.Add(new ProcessRowViewModel(p));
            }
            for (int i = group.Children.Count - 1; i >= 0; i--)
            {
                if (!seenPids.Contains(group.Children[i].ProcessId))
                    group.Children.RemoveAt(i);
            }
            group.RecalcMemory();
            group.RecalcThroughput();
        }

        ApplySortInPlace();
    }

    private IEnumerable<ProcessGroupViewModel> SortGroups(IEnumerable<ProcessGroupViewModel> groups)
    {
        IOrderedEnumerable<ProcessGroupViewModel> sorted = SortKey switch
        {
            ProcessSortKey.Instances => SortDescending
                ? groups.OrderByDescending(g => g.InstanceCount)
                : groups.OrderBy(g => g.InstanceCount),
            ProcessSortKey.Memory => SortDescending
                ? groups.OrderByDescending(g => g.TotalWorkingSetBytes)
                : groups.OrderBy(g => g.TotalWorkingSetBytes),
            ProcessSortKey.Download => SortDescending
                ? groups.OrderByDescending(g => g.CurrentDownloadBps)
                : groups.OrderBy(g => g.CurrentDownloadBps),
            ProcessSortKey.Upload => SortDescending
                ? groups.OrderByDescending(g => g.CurrentUploadBps)
                : groups.OrderBy(g => g.CurrentUploadBps),
            _ => SortDescending
                ? groups.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase)
                : groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
        };
        return sorted.ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void ApplySortInPlace()
    {
        // Reorder via Move() so we don't drop the TreeView selection.
        var sorted = SortGroups(ProcessGroups.ToList()).ToList();
        for (int target = 0; target < sorted.Count; target++)
        {
            var current = ProcessGroups.IndexOf(sorted[target]);
            if (current < 0) continue;
            if (current != target) ProcessGroups.Move(current, target);
        }
    }

    partial void OnProcessFilterChanged(string value) => _ = RefreshProcessesAsync();

    /// <summary>
    /// Returns the first existing rule (if any) that matches the given process node,
    /// by exe-name first then PID.
    /// </summary>
    public RuleRowViewModel? FindRuleForProcess(object? node)
    {
        string? exeName = node switch
        {
            ProcessGroupViewModel g => g.Name,
            ProcessRowViewModel r => r.Name,
            _ => null,
        };
        int pid = node is ProcessRowViewModel pr ? pr.ProcessId : 0;
        if (exeName is null && pid == 0) return null;

        foreach (var rule in Rules)
        {
            var r = rule.Rule;
            if (exeName is not null
                && r.MatchKind == RuleMatchKind.ExecutableName
                && RuleMatchNormalizer.MatchValuesEqual(RuleMatchKind.ExecutableName, r.MatchValue, exeName))
                return rule;
            if (pid != 0
                && r.MatchKind == RuleMatchKind.ProcessId
                && int.TryParse(RuleMatchNormalizer.NormalizeForComparison(RuleMatchKind.ProcessId, r.MatchValue), out var rpid)
                && rpid == pid)
                return rule;
        }
        return null;
    }

    [RelayCommand]
    private async Task EditRuleForProcess(object? node)
    {
        var rule = FindRuleForProcess(node);
        if (rule is null) return;
        SelectedRule = rule;
        await EditRule();
    }

    [RelayCommand]
    private async Task NewRuleFromProcessNode(object? node)
    {
        SelectedProcessNode = node;
        await NewRuleFromSelectedProcess();
    }

    [RelayCommand]
    private async Task NewRuleFromSelectedProcess()
    {
        BandwidthRule rule;
        switch (SelectedProcessNode)
        {
            case ProcessRowViewModel row:
                // Specific PID selected — default to matching by exe name (covers all instances)
                // since that's the more useful behavior. User can change to PID in editor.
                rule = new BandwidthRule
                {
                    Name = row.Name,
                    MatchKind = RuleMatchKind.ExecutableName,
                    MatchValue = row.Name,
                };
                break;
            case ProcessGroupViewModel group:
                rule = new BandwidthRule
                {
                    Name = group.Name,
                    MatchKind = RuleMatchKind.ExecutableName,
                    MatchValue = group.Name,
                };
                break;
            default:
                ThemedDialog.Show(WpfApplication.Current.MainWindow,
                    "No process selected",
                    "Pick a process from the list on the left first.",
                    ThemedDialogKind.Info, ThemedDialogButtons.Ok);
                return;
        }
        await OpenEditorAsync(rule);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_services)
        {
            Owner = WpfApplication.Current.MainWindow,
        };
        window.ShowDialog();

        // Pick up any settings changes the dialog persisted to disk.
        _settings = UserSettingsStore.Load();
        if (HideMicrosoftProcesses != _settings.HideMicrosoftProcesses)
            HideMicrosoftProcesses = _settings.HideMicrosoftProcesses;
        ApplyRefreshInterval(_settings.ProcessRefreshSeconds);
        _services.TrayIcon.ApplySettings(_settings);
    }

    [RelayCommand]
    private async Task NewBlankRule()
    {
        var rule = new BandwidthRule
        {
            Name = "New rule",
            MatchKind = RuleMatchKind.ExecutableName,
            MatchValue = "",
        };
        await OpenEditorAsync(rule);
    }

    [RelayCommand]
    private async Task EditRule()
    {
        if (SelectedRule is null) return;
        var r = SelectedRule.Rule;
        var clone = new BandwidthRule
        {
            Id = r.Id,
            Name = r.Name,
            MatchKind = r.MatchKind,
            MatchValue = r.MatchValue,
            DownloadBytesPerSecond = r.DownloadBytesPerSecond,
            UploadBytesPerSecond = r.UploadBytesPerSecond,
            Enabled = r.Enabled,
            CreatedUtc = r.CreatedUtc,
            UpdatedUtc = r.UpdatedUtc,
        };
        await OpenEditorAsync(clone);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null) return;
        var result = ThemedDialog.Show(WpfApplication.Current.MainWindow,
            $"Delete rule '{SelectedRule.Name}'?",
            "The rule will be removed and no longer applied.",
            ThemedDialogKind.Question, ThemedDialogButtons.YesNo);
        if (result != ThemedDialogResult.Yes) return;
        try
        {
            await _services.RuleManager.RemoveAsync(SelectedRule.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete rule");
            ThemedDialog.Show(WpfApplication.Current.MainWindow,
                "Could not delete rule",
                ex.Message,
                ThemedDialogKind.Error,
                ThemedDialogButtons.Ok);
        }
    }

    [RelayCommand]
    private async Task ToggleRuleAsync(RuleRowViewModel? vm)
    {
        if (vm is null) return;
        var requested = vm.Enabled;
        try
        {
            await _services.RuleManager.ToggleAsync(vm.Id, requested);
        }
        catch (Exception ex)
        {
            vm.Enabled = !requested;
            Log.Error(ex, "Failed to toggle rule");
            ThemedDialog.Show(WpfApplication.Current.MainWindow,
                "Could not update rule",
                ex.Message,
                ThemedDialogKind.Error,
                ThemedDialogButtons.Ok);
        }
    }

    [RelayCommand]
    private async Task ToggleSelectedRuleAsync()
    {
        if (SelectedRule is null) return;
        var requested = !SelectedRule.Enabled;
        SelectedRule.Enabled = requested;
        try
        {
            await _services.RuleManager.ToggleAsync(SelectedRule.Id, requested);
        }
        catch (Exception ex)
        {
            SelectedRule.Enabled = !requested;
            Log.Error(ex, "Failed to toggle selected rule");
            ThemedDialog.Show(WpfApplication.Current.MainWindow,
                "Could not update rule",
                ex.Message,
                ThemedDialogKind.Error,
                ThemedDialogButtons.Ok);
        }
    }

    private async Task OpenEditorAsync(BandwidthRule rule)
    {
        var existing = _services.RuleManager.Rules.ToList();
        var dialog = new Views.RuleEditorWindow(rule, existing, _settings.DefaultRateUnit)
        {
            Owner = WpfApplication.Current.MainWindow,
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _services.RuleManager.AddOrUpdateAsync(dialog.Result!);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save rule");
                ThemedDialog.Show(WpfApplication.Current.MainWindow,
                    "Could not save rule",
                    ex.Message,
                    ThemedDialogKind.Error,
                    ThemedDialogButtons.Ok);
            }
        }
    }

    private void SyncRulesFromManager()
    {
        _ui.Invoke(() =>
        {
            Rules.Clear();
            foreach (var r in _services.RuleManager.Rules)
                Rules.Add(new RuleRowViewModel(r));
        });
    }

    private void OnEngineStatusChanged(object? sender, EngineStatusChangedEventArgs e)
    {
        _ui.Invoke(() =>
        {
            EngineStatus = e.Status;
            EngineStatusMessage = e.Message ?? e.Status.ToString();
        });
    }

    private void OnThroughput(object? sender, EngineThroughputEventArgs e)
    {
        _ui.BeginInvoke(() =>
        {
            var row = Rules.FirstOrDefault(r => r.Id == e.RuleId);
            if (row is null) return;
            row.CurrentDownloadBps = e.DownloadBytesPerSecond;
            row.CurrentUploadBps = e.UploadBytesPerSecond;
        });
    }

    private void OnProcessThroughput(object? sender, ProcessThroughputEventArgs e)
    {
        // Build a quick pid -> sample lookup, then walk the tree once.
        var lookup = new Dictionary<int, ProcessThroughputSample>(e.Samples.Count);
        foreach (var s in e.Samples) lookup[s.ProcessId] = s;

        _ui.BeginInvoke(() =>
        {
            long totalD = 0, totalU = 0;
            foreach (var group in ProcessGroups)
            {
                foreach (var child in group.Children)
                {
                    if (lookup.TryGetValue(child.ProcessId, out var sample))
                    {
                        child.CurrentDownloadBps = sample.DownloadBytesPerSecond;
                        child.CurrentUploadBps = sample.UploadBytesPerSecond;
                    }
                    else
                    {
                        // No samples this tick = zero throughput.
                        child.CurrentDownloadBps = 0;
                        child.CurrentUploadBps = 0;
                    }
                }
                group.RecalcThroughput();
                totalD += group.CurrentDownloadBps;
                totalU += group.CurrentUploadBps;
            }
            TotalDownloadBps = totalD;
            TotalUploadBps = totalU;
        });
    }
}
