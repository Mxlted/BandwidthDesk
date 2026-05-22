using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using BandwidthDesk.Core.Models;
using BandwidthDesk.Core.Processes;
using BandwidthDesk.Core.Throttling;
using BandwidthDesk.Engine.WinDivert.Native;
using Serilog;

namespace BandwidthDesk.Engine.WinDivert;

/// <summary>
/// WinDivert-backed per-process bandwidth throttler.
///
/// Strategy:
///   1. Open a WinDivert handle at the Network layer filtering "ip and (tcp or udp)".
///   2. Worker thread: WinDivertRecv → parse headers → look up local port owner via IP Helper API
///      → find matching rule → consume tokens from that rule's directional bucket (sleep if needed)
///      → WinDivertSend (reinject the original packet so the OS continues normally).
///   3. If no rule matches a packet, reinject it immediately (no shaping).
///
/// This is the canonical user-mode approach on Windows. WinDivert ships a signed kernel
/// driver (WinDivert64.sys) which the DLL installs on first open. Admin rights required.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinDivertThrottlingEngine : IThrottlingEngine
{
    private const int PacketBufferSize = 0xFFFF;
    private const string CaptureFilter = "ip and (tcp or udp)";

    private readonly object _gate = new();
    private IntPtr _handle = WinDivertNative.InvalidHandle;
    private Thread? _worker;
    private Thread? _connTableWorker;
    private Thread? _throughputWorker;
    private CancellationTokenSource? _cts;

    // Active rule -> compiled state. Replaced atomically via Volatile.Write.
    private CompiledRules _compiled = CompiledRules.Empty;

    // Cached pid lookups. Refreshed periodically by _connTableWorker.
    private Dictionary<ConnectionTable.ConnectionKey, int> _connSnapshot = new();

    private EngineStatus _status = EngineStatus.Stopped;

    public EngineStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<EngineStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<EngineThroughputEventArgs>? Throughput;
    public event EventHandler<ProcessThroughputEventArgs>? ProcessThroughput;

    // Per-PID byte counters for the current 1s window. ConcurrentDictionary because the
    // worker thread mutates while the throughput emitter (also worker thread, but logically separate)
    // drains it. Reset to empty on each emit.
    private ConcurrentDictionary<int, ProcessCounters> _pidCounters = new();

    public void Start()
    {
        lock (_gate)
        {
            if (_status is EngineStatus.Running or EngineStatus.Starting)
                return;

            SetStatus(EngineStatus.Starting, "Opening WinDivert handle");

            try
            {
                _handle = WinDivertNative.WinDivertOpen(
                    CaptureFilter,
                    WinDivertNative.Layer.Network,
                    priority: 0,
                    WinDivertNative.Flag.None);

                if (_handle == WinDivertNative.InvalidHandle)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"WinDivertOpen failed (Win32 error {err}). " +
                        "Make sure you are running as Administrator and that WinDivert.dll + WinDivert64.sys are next to the executable.");
                }

                SetParamOrLog(WinDivertNative.Param.QueueLength, 8192);
                SetParamOrLog(WinDivertNative.Param.QueueTime, 2000);
                SetParamOrLog(WinDivertNative.Param.QueueSize, 33554432);

                _cts = new CancellationTokenSource();
                _worker = new Thread(WorkerLoop)
                {
                    Name = "BandwidthDesk-WinDivert",
                    IsBackground = true,
                };
                _worker.Start();

                _connTableWorker = new Thread(ConnectionTableLoop)
                {
                    Name = "BandwidthDesk-ConnTable",
                    IsBackground = true,
                };
                _connTableWorker.Start();

                _throughputWorker = new Thread(ThroughputLoop)
                {
                    Name = "BandwidthDesk-Throughput",
                    IsBackground = true,
                };
                _throughputWorker.Start();

                SetStatus(EngineStatus.Running, null);
                Log.Information("WinDivert engine started");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WinDivert engine failed to start");
                SafeClose();
                SetStatus(EngineStatus.Faulted, ex.Message);
                throw;
            }
        }
    }

    public void Stop()
    {
        Thread? worker;
        Thread? conn;
        Thread? throughput;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_status == EngineStatus.Stopped) return;
            cts = _cts;
            worker = _worker;
            conn = _connTableWorker;
            throughput = _throughputWorker;
            _cts = null;
            _worker = null;
            _connTableWorker = null;
            _throughputWorker = null;
        }

        try
        {
            cts?.Cancel();
            if (_handle != WinDivertNative.InvalidHandle)
            {
                WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.ShutdownHow.Both);
            }

            worker?.Join(TimeSpan.FromSeconds(3));
            conn?.Join(TimeSpan.FromSeconds(2));
            throughput?.Join(TimeSpan.FromSeconds(2));
        }
        finally
        {
            SafeClose();
            cts?.Dispose();
            SetStatus(EngineStatus.Stopped, null);
            Log.Information("WinDivert engine stopped");
        }
    }

    public void UpdateRules(IReadOnlyList<BandwidthRule> rules)
    {
        var compiled = CompiledRules.Build(rules);
        Volatile.Write(ref _compiled, compiled);
        Log.Information("Engine rules updated; count={Count}", rules.Count);
    }

    public void Dispose() => Stop();

    private void SafeClose()
    {
        if (_handle != WinDivertNative.InvalidHandle)
        {
            try { WinDivertNative.WinDivertClose(_handle); }
            catch (Exception ex) { Log.Warning(ex, "WinDivertClose threw"); }
            _handle = WinDivertNative.InvalidHandle;
        }
    }

    private void SetParamOrLog(WinDivertNative.Param param, ulong value)
    {
        if (WinDivertNative.WinDivertSetParam(_handle, param, value))
            return;

        int err = Marshal.GetLastWin32Error();
        Log.Warning("WinDivertSetParam failed; param={Param} value={Value} err={Err}", param, value, err);
    }

    private void SetStatus(EngineStatus s, string? msg)
    {
        _status = s;
        try { StatusChanged?.Invoke(this, new EngineStatusChangedEventArgs(s, msg)); }
        catch (Exception ex) { Log.Warning(ex, "StatusChanged handler threw"); }
    }

    private void ConnectionTableLoop()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var snap = ConnectionTable.Snapshot();
                Volatile.Write(ref _connSnapshot, snap);
                ct.WaitHandle.WaitOne(750);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "Connection-table worker faulted");
        }
    }

    private void ThroughputLoop()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (ct.WaitHandle.WaitOne(1000)) break;
                EmitThroughput();
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "Throughput emitter faulted");
        }
    }

    private void WorkerLoop()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        IntPtr buffer = Marshal.AllocHGlobal(PacketBufferSize);

        try
        {
            byte[] managedBuf = new byte[PacketBufferSize];
            while (!ct.IsCancellationRequested)
            {
                var addr = new WinDivertNative.Address();
                if (!WinDivertNative.WinDivertRecv(_handle, buffer, PacketBufferSize, out uint readLen, ref addr))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (ct.IsCancellationRequested) break;
                    // 232 = ERROR_NO_DATA (shutdown), 995 = aborted
                    if (err is 232 or 995) break;
                    Log.Warning("WinDivertRecv failed; err={Err}", err);
                    Thread.Sleep(5);
                    continue;
                }

                if (readLen == 0)
                    continue;

                int len = (int)readLen;
                Marshal.Copy(buffer, managedBuf, 0, len);
                var parsed = PacketHeaders.Parse(managedBuf.AsSpan(0, len));

                int delayMs = 0;
                if (parsed.IsValid)
                {
                    delayMs = ApplyRules(parsed, addr.Outbound);
                }

                if (delayMs > 0)
                {
                    // Cap a single delay to keep WinDivert's queue from timing out.
                    if (delayMs > 250) delayMs = 250;
                    try { ct.WaitHandle.WaitOne(delayMs); }
                    catch { /* ignore */ }
                    if (ct.IsCancellationRequested) break;
                }

                if (!WinDivertNative.WinDivertSend(_handle, buffer, readLen, out _, ref addr))
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Debug("WinDivertSend failed; err={Err} len={Len}", err, readLen);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "WinDivert worker crashed");
            SetStatus(EngineStatus.Faulted, ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Look up the owning PID for the local endpoint of <paramref name="p"/>,
    /// find a matching rule, and consume tokens. Returns ms to delay before reinjecting.
    /// </summary>
    private int ApplyRules(PacketHeaders.Parsed p, bool outbound)
    {
        var compiled = Volatile.Read(ref _compiled);
        var snap = Volatile.Read(ref _connSnapshot);

        // Local endpoint = src for outbound, dst for inbound.
        var localAddr = outbound ? p.SrcAddress : p.DstAddress;
        int localPort = outbound ? p.SrcPort : p.DstPort;

        // Try exact match first, then 0.0.0.0 (listener) entry.
        int pid = -1;
        if (snap.TryGetValue(new ConnectionTable.ConnectionKey(p.Protocol, localAddr, localPort), out var found))
            pid = found;
        else if (snap.TryGetValue(new ConnectionTable.ConnectionKey(p.Protocol, System.Net.IPAddress.Any, localPort), out var anyFound))
            pid = anyFound;

        if (pid <= 0) return 0;

        int bytes = p.TotalLength;

        // Always tally per-PID for the live usage display, even if no rule matches.
        var counters = _pidCounters.GetOrAdd(pid, static _ => new ProcessCounters());
        if (outbound) Interlocked.Add(ref counters.UploadBytes, bytes);
        else Interlocked.Add(ref counters.DownloadBytes, bytes);

        if (compiled.IsEmpty) return 0;
        var ruleState = compiled.MatchByPid(pid);
        if (ruleState is null) return 0;

        if (outbound)
        {
            Interlocked.Add(ref ruleState.UploadBytesThisSecond, bytes);
            if (ruleState.UploadBucket is { } up)
                return up.TryConsume(bytes);
        }
        else
        {
            Interlocked.Add(ref ruleState.DownloadBytesThisSecond, bytes);
            if (ruleState.DownloadBucket is { } down)
                return down.TryConsume(bytes);
        }
        return 0;
    }

    private void EmitThroughput()
    {
        var ruleHandler = Throughput;
        if (ruleHandler is not null)
        {
            var compiled = Volatile.Read(ref _compiled);
            foreach (var (id, state) in compiled.AllStates())
            {
                long down = Interlocked.Exchange(ref state.DownloadBytesThisSecond, 0);
                long up = Interlocked.Exchange(ref state.UploadBytesThisSecond, 0);
                try { ruleHandler(this, new EngineThroughputEventArgs(id, down, up)); }
                catch (Exception ex) { Log.Warning(ex, "Throughput handler threw"); }
            }
        }

        var procHandler = ProcessThroughput;
        // Swap the counters dict atomically so the worker keeps tallying into a fresh one.
        var drained = Interlocked.Exchange(ref _pidCounters, new ConcurrentDictionary<int, ProcessCounters>());
        if (procHandler is not null && drained.Count > 0)
        {
            var samples = new List<ProcessThroughputSample>(drained.Count);
            foreach (var (pid, c) in drained)
            {
                samples.Add(new ProcessThroughputSample(pid, c.DownloadBytes, c.UploadBytes));
            }
            try { procHandler(this, new ProcessThroughputEventArgs(samples)); }
            catch (Exception ex) { Log.Warning(ex, "ProcessThroughput handler threw"); }
        }
    }
}

internal sealed class ProcessCounters
{
    public long DownloadBytes;
    public long UploadBytes;
}

/// <summary>Compiled snapshot of the active rule set, with token buckets attached.</summary>
internal sealed class CompiledRules
{
    public static readonly CompiledRules Empty = new(Array.Empty<RuleState>());

    private readonly RuleState[] _states;

    private CompiledRules(RuleState[] states) { _states = states; }

    public bool IsEmpty => _states.Length == 0;

    public static CompiledRules Build(IReadOnlyList<BandwidthRule> rules)
    {
        var list = new List<RuleState>(rules.Count);
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            if (r.DownloadBytesPerSecond <= 0 && r.UploadBytesPerSecond <= 0) continue;
            list.Add(new RuleState(r));
        }
        return new CompiledRules(list.ToArray());
    }

    public RuleState? MatchByPid(int pid)
    {
        // Fast path: scan in declared order, first match wins.
        foreach (var s in _states)
        {
            if (s.Matches(pid)) return s;
        }
        return null;
    }

    public IEnumerable<KeyValuePair<Guid, RuleState>> AllStates()
    {
        foreach (var s in _states) yield return new KeyValuePair<Guid, RuleState>(s.RuleId, s);
    }
}

internal sealed class RuleState
{
    public Guid RuleId { get; }
    public TokenBucket? DownloadBucket { get; }
    public TokenBucket? UploadBucket { get; }

    public long DownloadBytesThisSecond;
    public long UploadBytesThisSecond;

    private readonly RuleMatchKind _kind;
    private readonly string _value;

    // Cached matched pids -> result, refreshed when needed.
    private readonly ConcurrentDictionary<int, PidMatchCacheEntry> _pidMatchCache = new();
    private static readonly TimeSpan PidMatchCacheTtl = TimeSpan.FromSeconds(10);

    public RuleState(BandwidthRule r)
    {
        RuleId = r.Id;
        _kind = r.MatchKind;
        _value = r.MatchValue ?? string.Empty;

        if (r.DownloadBytesPerSecond > 0)
            DownloadBucket = new TokenBucket(r.DownloadBytesPerSecond, Math.Max(r.DownloadBytesPerSecond, 64 * 1024));
        if (r.UploadBytesPerSecond > 0)
            UploadBucket = new TokenBucket(r.UploadBytesPerSecond, Math.Max(r.UploadBytesPerSecond, 64 * 1024));
    }

    public bool Matches(int pid)
    {
        long now = Stopwatch.GetTimestamp();
        if (_pidMatchCache.TryGetValue(pid, out var cached)
            && Stopwatch.GetElapsedTime(cached.Timestamp, now) < PidMatchCacheTtl)
        {
            return cached.Matches;
        }

        bool result = false;
        try
        {
            switch (_kind)
            {
                case RuleMatchKind.ProcessId:
                    result = int.TryParse(_value, out var wantPid) && wantPid == pid;
                    break;

                case RuleMatchKind.ExecutableName:
                {
                    using var p = Process.GetProcessById(pid);
                    var name = p.ProcessName;
                    var want = _value;
                    if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        want = want[..^4];
                    result = string.Equals(name, want, StringComparison.OrdinalIgnoreCase);
                    break;
                }

                case RuleMatchKind.ExecutablePath:
                {
                    using var p = Process.GetProcessById(pid);
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    result = path is not null && string.Equals(path, _value, StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }
        }
        catch (ArgumentException)
        {
            result = false; // pid gone
        }
        catch
        {
            result = false;
        }

        if (_pidMatchCache.Count > 4096)
            _pidMatchCache.Clear();

        _pidMatchCache[pid] = new PidMatchCacheEntry(result, now);
        return result;
    }

    private readonly record struct PidMatchCacheEntry(bool Matches, long Timestamp);
}
