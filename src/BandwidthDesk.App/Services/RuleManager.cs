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
        var existing = Rules.FirstOrDefault(r => r.Id == rule.Id);
        if (existing is null)
        {
            Rules.Add(rule);
        }
        else
        {
            var idx = Rules.IndexOf(existing);
            Rules[idx] = rule;
        }
        await PersistAsync().ConfigureAwait(true);
    }

    public async Task RemoveAsync(Guid id)
    {
        var existing = Rules.FirstOrDefault(r => r.Id == id);
        if (existing is null) return;
        Rules.Remove(existing);
        await PersistAsync().ConfigureAwait(true);
    }

    public async Task ReplaceAllAsync(IEnumerable<BandwidthRule> rules)
    {
        Rules.Clear();
        foreach (var r in rules) Rules.Add(r);
        await PersistAsync().ConfigureAwait(true);
    }

    public async Task ToggleAsync(Guid id, bool enabled)
    {
        var existing = Rules.FirstOrDefault(r => r.Id == id);
        if (existing is null) return;
        existing.Enabled = enabled;
        existing.UpdatedUtc = DateTime.UtcNow;
        await PersistAsync().ConfigureAwait(true);
    }

    private async Task PersistAsync()
    {
        await _store.SaveAsync(Rules).ConfigureAwait(true);
        PushToEngine();
    }

    private void PushToEngine()
    {
        try { _engine.UpdateRules(Rules.ToList()); }
        catch (Exception ex) { Log.Warning(ex, "Engine.UpdateRules threw"); }
    }
}
