using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BandwidthDesk.Core.Models;
using Serilog;

namespace BandwidthDesk.Core.Configuration;

/// <summary>
/// Stores rules as JSON on disk. Atomic writes via temp-file + replace.
/// </summary>
public sealed class JsonRuleStore : IRuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? LastLoadWarningMessage { get; private set; }

    public JsonRuleStore() : this(AppPaths.RulesFilePath) { }

    public JsonRuleStore(string path)
    {
        _path = path;
    }

    public async Task<IReadOnlyList<BandwidthRule>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                Log.Information("Rules file not found, starting empty; path={Path}", _path);
                LastLoadWarningMessage = null;
                return Array.Empty<BandwidthRule>();
            }

            await using var stream = File.OpenRead(_path);
            var rules = await JsonSerializer.DeserializeAsync<List<BandwidthRule?>>(stream, JsonOptions, ct).ConfigureAwait(false);
            LastLoadWarningMessage = null;
            return NormalizeRules(rules);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load rules; path={Path}", _path);
            var backupPath = PreserveCorruptFile(_path, ex);
            LastLoadWarningMessage = backupPath is null
                ? "BandwidthDesk could not read rules.json and started with an empty rule list. The original file could not be moved; check the log before saving new rules."
                : $"BandwidthDesk could not read rules.json and started with an empty rule list. The unreadable file was preserved as {backupPath}.";
            return Array.Empty<BandwidthRule>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<BandwidthRule> rules, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tempPath = _path + ".tmp";
            var list = new List<BandwidthRule>(rules);
            await using (var fs = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(fs, list, JsonOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
            Log.Debug("Saved {Count} rules to {Path}", list.Count, _path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save rules; path={Path}", _path);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<BandwidthRule> NormalizeRules(IEnumerable<BandwidthRule?>? rules)
    {
        var normalized = new List<BandwidthRule>();
        if (rules is null) return normalized;

        foreach (var rule in rules)
        {
            if (rule is null) continue;
            if (rule.Id == Guid.Empty) rule.Id = Guid.NewGuid();
            rule.Name ??= string.Empty;
            rule.MatchValue ??= string.Empty;
            normalized.Add(rule);
        }

        return normalized;
    }

    private static string? PreserveCorruptFile(string path, Exception cause)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(dir, $"{name}.corrupt-{timestamp}.json");
            int suffix = 2;
            while (File.Exists(backupPath))
                backupPath = Path.Combine(dir, $"{name}.corrupt-{timestamp}-{suffix++}.json");

            File.Move(path, backupPath);
            Log.Warning(cause, "Preserved unreadable rules file at {BackupPath}", backupPath);
            return backupPath;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to preserve unreadable rules file; path={Path}", path);
            return null;
        }
    }
}
