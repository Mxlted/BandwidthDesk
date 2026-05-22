using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using BandwidthDesk.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BandwidthDesk.App.ViewModels;

/// <summary>
/// Groups all running instances of one executable. Top-level row in the process tree.
/// Aggregates memory, download, and upload from its child PIDs.
/// </summary>
public sealed partial class ProcessGroupViewModel : ObservableObject
{
    public string Name { get; }

    /// <summary>Best-effort path/description sample from the first child.</summary>
    [ObservableProperty] private string _description = "—";
    [ObservableProperty] private string _executablePath = "—";

    /// <summary>Small icon extracted from <see cref="ExecutablePath"/>, or null if unavailable.</summary>
    public ImageSource? Icon => IconService.GetIconForPath(ExecutablePath);

    partial void OnExecutablePathChanged(string value) => OnPropertyChanged(nameof(Icon));

    public ObservableCollection<ProcessRowViewModel> Children { get; } = new();

    [ObservableProperty] private long _totalWorkingSetBytes;
    [ObservableProperty] private long _currentDownloadBps;
    [ObservableProperty] private long _currentUploadBps;
    [ObservableProperty] private bool _isExpanded;

    public bool IsMicrosoft { get; set; }

    public ProcessGroupViewModel(string name)
    {
        Name = name;
    }

    public int InstanceCount => Children.Count;
    public string InstanceCountLabel => Children.Count == 1 ? "1 instance" : $"{Children.Count} instances";

    public void RecalcMemory()
    {
        TotalWorkingSetBytes = Children.Sum(c => c.WorkingSetBytes);
        OnPropertyChanged(nameof(InstanceCount));
        OnPropertyChanged(nameof(InstanceCountLabel));
    }

    /// <summary>Re-fire property notifications so brush-returning converters re-evaluate after a theme swap.</summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(CurrentDownloadBps));
        OnPropertyChanged(nameof(CurrentUploadBps));
    }

    public void RecalcThroughput()
    {
        long d = 0, u = 0;
        foreach (var c in Children)
        {
            d += c.CurrentDownloadBps;
            u += c.CurrentUploadBps;
        }
        CurrentDownloadBps = d;
        CurrentUploadBps = u;
    }
}
