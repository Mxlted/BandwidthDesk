using System;
using System.Collections.Generic;
using BandwidthDesk.Core.Models;

namespace BandwidthDesk.Core.Throttling;

public enum EngineStatus
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

public sealed class EngineStatusChangedEventArgs(EngineStatus status, string? message) : EventArgs
{
    public EngineStatus Status { get; } = status;
    public string? Message { get; } = message;
}

public sealed class EngineThroughputEventArgs(Guid ruleId, long downloadBps, long uploadBps) : EventArgs
{
    public Guid RuleId { get; } = ruleId;
    public long DownloadBytesPerSecond { get; } = downloadBps;
    public long UploadBytesPerSecond { get; } = uploadBps;
}

public readonly record struct ProcessThroughputSample(int ProcessId, long DownloadBytesPerSecond, long UploadBytesPerSecond);

public sealed class ProcessThroughputEventArgs(IReadOnlyList<ProcessThroughputSample> samples) : EventArgs
{
    public IReadOnlyList<ProcessThroughputSample> Samples { get; } = samples;
}

/// <summary>
/// Abstracts the platform-specific traffic-shaping backend.
/// </summary>
public interface IThrottlingEngine : IDisposable
{
    EngineStatus Status { get; }

    event EventHandler<EngineStatusChangedEventArgs>? StatusChanged;
    event EventHandler<EngineThroughputEventArgs>? Throughput;
    event EventHandler<ProcessThroughputEventArgs>? ProcessThroughput;

    /// <summary>Start capturing and shaping. May throw if driver/admin missing.</summary>
    void Start();

    /// <summary>Stop capturing. Idempotent.</summary>
    void Stop();

    /// <summary>Replace the active rule set atomically.</summary>
    void UpdateRules(IReadOnlyList<BandwidthRule> rules);
}
