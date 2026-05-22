using System;
using System.Text.Json.Serialization;

namespace BandwidthDesk.Core.Models;

/// <summary>
/// How a rule matches a running process.
/// </summary>
public enum RuleMatchKind
{
    /// <summary>Match by executable name (e.g. "chrome.exe"). Case-insensitive.</summary>
    ExecutableName = 0,
    /// <summary>Match by exact process id. Lost across reboots; useful for one-off limits.</summary>
    ProcessId = 1,
    /// <summary>Match by full executable path. Case-insensitive.</summary>
    ExecutablePath = 2,
}

public sealed class BandwidthRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly label shown in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    public RuleMatchKind MatchKind { get; set; } = RuleMatchKind.ExecutableName;

    /// <summary>Value to match against (exe name, full path, or pid as string).</summary>
    public string MatchValue { get; set; } = string.Empty;

    /// <summary>Download cap in bytes per second. 0 = unlimited.</summary>
    public long DownloadBytesPerSecond { get; set; }

    /// <summary>Upload cap in bytes per second. 0 = unlimited.</summary>
    public long UploadBytesPerSecond { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool HasDownloadLimit => DownloadBytesPerSecond > 0;

    [JsonIgnore]
    public bool HasUploadLimit => UploadBytesPerSecond > 0;
}
