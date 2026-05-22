using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BandwidthDesk.Core.Configuration;
using BandwidthDesk.Core.Models;
using BandwidthDesk.Core.Throttling;
using Serilog;

namespace BandwidthDesk.App.Services;

/// <summary>
/// Owns the in-memory rule set, persists changes to disk, and pushes updates into the engine.
/// </summary>
public sealed class RuleManager
{
    private readonly IRuleStore _store;
    private readonly IThrottlingEngine _engine;
    public ObservableCollection<BandwidthRule> Rules { get; } = new();

    public RuleManager(IRuleStore store, IThrottlingEngine engine)
    {
        _store = store;
        _engine = engine;
    }

    public async Task LoadAsync()
    {
        var loaded = await _store.LoadAsync().ConfigureAwait(true);
        Rules.Clear();
        foreach (var r in loaded) Rules.Add(r);
        PushToEngine();
        Log.Information("Loaded {Count} rules", Rules.Count);
    }

    public async Task AddOrUpdateAsync(BandwidthRule rule)
    {
        rule.UpdatedUtc = DateTime.UtcNow;
        var snapshot = Rules.Select(CloneRule).ToList();
        var idx = snapshot.FindIndex(r => r.Id == rule.Id);
        if (idx < 0)
            snapshot.Add(CloneRule(rule));
        else
            snapshot[idx] = CloneRule(rule);

        await PersistAsync(snapshot).ConfigureAwait(true);
    }

    public async Task RemoveAsync(Guid id)
    {
        var snapshot = Rules.Where(r => r.Id != id).Select(CloneRule).ToList();
        if (snapshot.Count == Rules.Count) return;
        await PersistAsync(snapshot).ConfigureAwait(true);
    }

    public async Task ReplaceAllAsync(IEnumerable<BandwidthRule> rules)
    {
        var snapshot = rules.Select(CloneRule).ToList();
        await PersistAsync(snapshot).ConfigureAwait(true);
    }

    public async Task ToggleAsync(Guid id, bool enabled)
    {
        var snapshot = Rules.Select(CloneRule).ToList();
        var existing = snapshot.FirstOrDefault(r => r.Id == id);
        if (existing is null) return;
        existing.Enabled = enabled;
        existing.UpdatedUtc = DateTime.UtcNow;
        await PersistAsync(snapshot).ConfigureAwait(true);
    }

    private async Task PersistAsync(IReadOnlyList<BandwidthRule> rules)
    {
        await _store.SaveAsync(rules).ConfigureAwait(true);
        Rules.Clear();
        foreach (var r in rules) Rules.Add(CloneRule(r));
        PushToEngine();
    }

    private void PushToEngine()
    {
        try { _engine.UpdateRules(Rules.ToList()); }
        catch (Exception ex) { Log.Warning(ex, "Engine.UpdateRules threw"); }
    }

    private static BandwidthRule CloneRule(BandwidthRule r) => new()
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
}
