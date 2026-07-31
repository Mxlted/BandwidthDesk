using System;
using BandwidthDesk.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BandwidthDesk.App.ViewModels;

public sealed partial class RuleRowViewModel : ObservableObject
{
    public BandwidthRule Rule { get; private set; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private long _currentDownloadBps;
    [ObservableProperty] private long _currentUploadBps;

    public RuleRowViewModel(BandwidthRule rule)
    {
        Rule = rule;
        _enabled = rule.Enabled;
    }

    public Guid Id => Rule.Id;
    public string Name => string.IsNullOrWhiteSpace(Rule.Name) ? Rule.MatchValue : Rule.Name;
    public string MatchSummary => Rule.MatchKind switch
    {
        RuleMatchKind.ExecutableName => $"Exe: {Rule.MatchValue}",
        RuleMatchKind.ProcessId => $"PID: {Rule.MatchValue}",
        RuleMatchKind.ExecutablePath => $"Path: {Rule.MatchValue}",
        _ => Rule.MatchValue,
    };
    public long DownloadBytesPerSecond => Rule.DownloadBytesPerSecond;
    public long UploadBytesPerSecond => Rule.UploadBytesPerSecond;
    public bool HasDownloadLimit => Rule.DownloadBytesPerSecond > 0;
    public bool HasUploadLimit => Rule.UploadBytesPerSecond > 0;
    public bool IsActivelyLimited => Enabled && (HasDownloadLimit || HasUploadLimit);

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(IsActivelyLimited));

    public void UpdateRule(BandwidthRule rule)
    {
        Rule = rule;
        Enabled = rule.Enabled;
        if (!IsActivelyLimited)
        {
            CurrentDownloadBps = 0;
            CurrentUploadBps = 0;
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(MatchSummary));
        OnPropertyChanged(nameof(DownloadBytesPerSecond));
        OnPropertyChanged(nameof(UploadBytesPerSecond));
        OnPropertyChanged(nameof(HasDownloadLimit));
        OnPropertyChanged(nameof(HasUploadLimit));
        OnPropertyChanged(nameof(IsActivelyLimited));
    }

    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(CurrentDownloadBps));
        OnPropertyChanged(nameof(CurrentUploadBps));
    }
}
