using System.Windows.Media;
using BandwidthDesk.App.Services;
using BandwidthDesk.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BandwidthDesk.App.ViewModels;

public sealed partial class ProcessRowViewModel : ObservableObject
{
    public ProcessInfo Info { get; private set; }

    [ObservableProperty] private long _currentDownloadBps;
    [ObservableProperty] private long _currentUploadBps;

    public ProcessRowViewModel(ProcessInfo info)
    {
        Info = info;
    }

    public int ProcessId => Info.ProcessId;
    public string Name => Info.Name;
    public string Description => string.IsNullOrWhiteSpace(Info.Description) ? "—" : Info.Description!;
    public long WorkingSetBytes => Info.WorkingSetBytes;
    public string ExecutablePath => Info.ExecutablePath ?? "—";
    public ImageSource? Icon => IconService.GetIconForPath(Info.ExecutablePath);
    public string? CompanyName => Info.CompanyName;
    public bool IsMicrosoft => Info.IsMicrosoft;

    /// <summary>Update the underlying snapshot in place; raises change notifications for fields that may have moved.</summary>
    public void UpdateInfo(ProcessInfo info)
    {
        var oldMem = Info.WorkingSetBytes;
        var oldPath = Info.ExecutablePath;
        var oldDescription = Info.Description;
        var oldCompany = Info.CompanyName;
        var oldMicrosoft = Info.IsMicrosoft;
        Info = info;
        if (oldMem != info.WorkingSetBytes) OnPropertyChanged(nameof(WorkingSetBytes));
        if (oldPath != info.ExecutablePath)
        {
            OnPropertyChanged(nameof(ExecutablePath));
            OnPropertyChanged(nameof(Icon));
        }
        if (oldDescription != info.Description) OnPropertyChanged(nameof(Description));
        if (oldCompany != info.CompanyName) OnPropertyChanged(nameof(CompanyName));
        if (oldMicrosoft != info.IsMicrosoft) OnPropertyChanged(nameof(IsMicrosoft));
    }

    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(CurrentDownloadBps));
        OnPropertyChanged(nameof(CurrentUploadBps));
    }
}
