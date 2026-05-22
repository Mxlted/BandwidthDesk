using System;
using BandwidthDesk.Core.Configuration;
using BandwidthDesk.Core.Processes;
using BandwidthDesk.Core.Throttling;
using BandwidthDesk.Engine.WinDivert;

namespace BandwidthDesk.App.Services;

/// <summary>
/// Tiny hand-rolled container. WPF + DI hosts are overkill for an app this size.
/// </summary>
public sealed class AppServices : IDisposable
{
    public IRuleStore RuleStore { get; private set; } = null!;
    public ProcessService ProcessService { get; private set; } = null!;
    public IThrottlingEngine Engine { get; private set; } = null!;
    public RuleManager RuleManager { get; private set; } = null!;
    public ProfileStore ProfileStore { get; private set; } = null!;
    public TrayIconService TrayIcon { get; private set; } = null!;

    public void Initialize()
    {
        RuleStore = new JsonRuleStore();
        ProcessService = new ProcessService();
        Engine = new WinDivertThrottlingEngine();
        RuleManager = new RuleManager(RuleStore, Engine);
        ProfileStore = new ProfileStore();
        TrayIcon = new TrayIconService();
        TrayIcon.Initialize();
    }

    public void Dispose()
    {
        try { Engine?.Dispose(); } catch { }
        try { TrayIcon?.Dispose(); } catch { }
    }
}
