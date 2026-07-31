using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BandwidthDesk.Core;
using BandwidthDesk.Core.Models;
using Serilog;

namespace BandwidthDesk.App.Services;

/// <summary>
/// A named, exportable bundle of rules + user preferences. Stored as a single JSON file
/// per profile under %LOCALAPPDATA%\BandwidthDesk\profiles\.
/// </summary>
public sealed class Profile
{
    public string SchemaVersion { get; set; } = "1";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool HideMicrosoftProcesses { get; set; } = true;
    public List<BandwidthRule> Rules { get; set; } = new();
}

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory
    {
        get
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "profiles");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public IReadOnlyList<string> ListProfileNames()
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate profiles");
            return Array.Empty<string>();
        }
    }

    public async Task SaveAsync(Profile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));

        var safe = SanitizeFileName(profile.Name);
        var path = Path.Combine(Directory, safe + ".json");
        profile.UpdatedUtc = DateTime.UtcNow;
        await WriteAsync(path, profile).ConfigureAwait(false);
        Log.Information("Saved profile {Name} -> {Path}", profile.Name, path);
    }

    public async Task<Profile?> LoadAsync(string name)
    {
        var path = Path.Combine(Directory, SanitizeFileName(name) + ".json");
        if (!File.Exists(path)) return null;
        return await ReadAsync(path).ConfigureAwait(false);
    }

    public void Delete(string name)
    {
        var path = Path.Combine(Directory, SanitizeFileName(name) + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
            Log.Information("Deleted profile {Name}", name);
        }
    }

    public async Task ExportAsync(Profile profile, string destinationPath)
    {
        await WriteAsync(destinationPath, profile).ConfigureAwait(false);
        Log.Information("Exported profile {Name} -> {Path}", profile.Name, destinationPath);
    }

    public async Task<Profile?> ImportAsync(string sourcePath)
    {
        var profile = await ReadAsync(sourcePath).ConfigureAwait(false);
        if (profile is null) return null;

        // Make sure the imported profile gets a unique name in the local store.
        var taken = new HashSet<string>(
            ListProfileNames().Select(StorageKeyForName),
            StringComparer.OrdinalIgnoreCase);
        var baseName = string.IsNullOrWhiteSpace(profile.Name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : profile.Name;
        var name = baseName;
        int n = 2;
        while (taken.Contains(StorageKeyForName(name)))
            name = $"{baseName} ({n++})";
        profile.Name = name;

        // Issue fresh IDs so importing doesn't collide with existing rules.
        foreach (var r in profile.Rules)
            r.Id = Guid.NewGuid();

        await SaveAsync(profile).ConfigureAwait(false);
        return profile;
    }

    public static string StorageKeyForName(string name) => SanitizeFileName(name);

    private static async Task WriteAsync(string path, Profile profile)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, profile, Json).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task<Profile?> ReadAsync(string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<Profile>(fs, Json).ConfigureAwait(false);
            return NormalizeProfile(profile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read profile from {Path}", path);
            return null;
        }
    }

    private static Profile? NormalizeProfile(Profile? profile)
    {
        if (profile is null) return null;

        profile.SchemaVersion ??= "1";
        if (!string.Equals(profile.SchemaVersion, "1", StringComparison.Ordinal))
            return null;
        if (!Enum.IsDefined(profile.Theme))
            return null;
        profile.Name ??= string.Empty;

        var rules = new List<BandwidthRule>();
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile.Rules is not null)
        {
            foreach (var rule in profile.Rules)
            {
                if (rule is null) continue;
                if (!Enum.IsDefined(rule.MatchKind)
                    || string.IsNullOrWhiteSpace(rule.MatchValue)
                    || rule.DownloadBytesPerSecond < 0
                    || rule.UploadBytesPerSecond < 0)
                {
                    return null;
                }

                if (rule.Id == Guid.Empty) rule.Id = Guid.NewGuid();
                rule.Name ??= string.Empty;
                rule.MatchValue = rule.MatchValue.Trim();
                if (rule.MatchKind == RuleMatchKind.ProcessId
                    && (!int.TryParse(rule.MatchValue, out var pid) || pid <= 0))
                {
                    return null;
                }

                var matchKey = $"{rule.MatchKind}:{RuleMatchNormalizer.NormalizeForComparison(rule.MatchKind, rule.MatchValue)}";
                if (!matches.Add(matchKey))
                    return null;
                rules.Add(rule);
            }
        }
        profile.Rules = rules;

        return profile;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "profile";
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd();

        var stem = cleaned.Split('.')[0];
        string[] reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            cleaned = "_" + cleaned;

        return cleaned;
    }
}
