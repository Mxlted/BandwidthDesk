using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    public ObservableCollection<BandwidthRule> Rules { get; } = new();
    public string? LastLoadWarningMessage { get; private set; }
    public event EventHandler? RulesChanged;

    public RuleManager(IRuleStore store, IThrottlingEngine engine)
    {
        _store = store;
        _engine = engine;
    }

    public async Task LoadAsync()
    {
        await _changeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var loaded = await _store.LoadAsync().ConfigureAwait(true);
            LastLoadWarningMessage = _store is JsonRuleStore jsonStore
                ? jsonStore.LastLoadWarningMessage
                : null;
            ReplaceInMemory(loaded);
            Log.Information("Loaded {Count} rules", Rules.Count);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    public async Task AddOrUpdateAsync(BandwidthRule rule)
    {
        await _changeGate.WaitAsync().ConfigureAwait(true);
        try
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
        finally
        {
            _changeGate.Release();
        }
    }

    public async Task RemoveAsync(Guid id)
    {
        await _changeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var snapshot = Rules.Where(r => r.Id != id).Select(CloneRule).ToList();
            if (snapshot.Count == Rules.Count) return;
            await PersistAsync(snapshot).ConfigureAwait(true);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    public async Task ReplaceAllAsync(IEnumerable<BandwidthRule> rules)
    {
        await _changeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var snapshot = rules.Select(CloneRule).ToList();
            await PersistAsync(snapshot).ConfigureAwait(true);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    public async Task ToggleAsync(Guid id, bool enabled)
    {
        await _changeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var snapshot = Rules.Select(CloneRule).ToList();
            var existing = snapshot.FirstOrDefault(r => r.Id == id);
            if (existing is null) return;
            existing.Enabled = enabled;
            existing.UpdatedUtc = DateTime.UtcNow;
            await PersistAsync(snapshot).ConfigureAwait(true);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    private async Task PersistAsync(IReadOnlyList<BandwidthRule> rules)
    {
        await _store.SaveAsync(rules).ConfigureAwait(true);
        ReplaceInMemory(rules);
    }

    private void ReplaceInMemory(IEnumerable<BandwidthRule> rules)
    {
        Rules.Clear();
        foreach (var r in rules) Rules.Add(CloneRule(r));
        PushToEngine();
        try { RulesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warning(ex, "RulesChanged handler threw"); }
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
