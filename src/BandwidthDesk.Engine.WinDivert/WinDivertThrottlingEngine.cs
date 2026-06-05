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
///      → find matching rule → reserve tokens from that rule's directional bucket.
///   3. If the packet is due now, reinject immediately. If it needs pacing, copy packet/address
///      metadata into a bounded delayed-send queue so unrelated traffic can continue flowing.
///
/// This is the canonical user-mode approach on Windows. WinDivert ships a signed kernel
/// driver (WinDivert64.sys) which the DLL installs on first open. Admin rights required.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinDivertThrottlingEngine : IThrottlingEngine
{
    private const int PacketBufferSize = 0xFFFF;
    private const string CaptureFilter = "ip and (tcp or udp)";
    private const int MaxDelayedPackets = 8192;
    private const long MaxDelayedBytes = 32L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly object _delayGate = new();
    private IntPtr _handle = WinDivertNative.InvalidHandle;
    private Thread? _worker;
    private Thread? _connTableWorker;
    private Thread? _throughputWorker;
    private Thread? _delayedSenderWorker;
    private CancellationTokenSource? _cts;
    private readonly PriorityQueue<DelayedPacket, DelayedPacketPriority> _delayedPackets = new();
    private long _delayedPacketBytes;
    private long _delayedSequence;
    private bool _acceptDelayedPackets;
    private bool _delayerStopRequested;
    private bool _delayerForceDrain;
    private long _lastDelayBackpressureLogTicks;

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
                ResetDelayedQueueForStart();

                _delayedSenderWorker = new Thread(DelayedSendLoop)
                {
                    Name = "BandwidthDesk-DelayedSend",
                    IsBackground = true,
                };
                _delayedSenderWorker.Start();

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
                Volatile.Write(ref _acceptDelayedPackets, false);
                _cts?.Cancel();
                RequestDelayedSenderStop(forceDrain: true);
                _delayedSenderWorker?.Join(TimeSpan.FromSeconds(1));
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
        Thread? delayed;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_status == EngineStatus.Stopped) return;
            cts = _cts;
            worker = _worker;
            conn = _connTableWorker;
            throughput = _throughputWorker;
            delayed = _delayedSenderWorker;
            _cts = null;
            _worker = null;
            _connTableWorker = null;
            _throughputWorker = null;
            _delayedSenderWorker = null;
        }

        try
        {
            Volatile.Write(ref _acceptDelayedPackets, false);
            cts?.Cancel();
            if (_handle != WinDivertNative.InvalidHandle)
            {
                WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.ShutdownHow.Recv);
            }

            worker?.Join(TimeSpan.FromSeconds(3));
            conn?.Join(TimeSpan.FromSeconds(2));
            throughput?.Join(TimeSpan.FromSeconds(2));
            RequestDelayedSenderStop(forceDrain: true);
            if (delayed is not null && !delayed.Join(TimeSpan.FromSeconds(3)))
            {
                Log.Warning("Delayed-send worker did not stop within the drain timeout; remaining packets may be dropped on close");
            }
        }
        finally
        {
            if (_handle != WinDivertNative.InvalidHandle)
                WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.ShutdownHow.Send);
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

                long dueTicks = Stopwatch.GetTimestamp();
                if (parsed.IsValid)
                {
                    dueTicks = ApplyRules(parsed, addr.Outbound, dueTicks);
                }

                if (dueTicks > Stopwatch.GetTimestamp()
                    && TryEnqueueDelayedPacket(buffer, readLen, addr, dueTicks))
                {
                    continue;
                }

                if (!WinDivertNative.WinDivertSend(_handle, buffer, readLen, out _, ref addr))
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Debug("WinDivertSend failed; err={Err} len={Len}", err, readLen);
                }

                if (ct.IsCancellationRequested)
                    break;
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

    private void ResetDelayedQueueForStart()
    {
        lock (_delayGate)
        {
            _delayedPackets.Clear();
            _delayedPacketBytes = 0;
            _delayedSequence = 0;
            _acceptDelayedPackets = true;
            _delayerStopRequested = false;
            _delayerForceDrain = false;
            _lastDelayBackpressureLogTicks = 0;
        }
    }

    private void RequestDelayedSenderStop(bool forceDrain)
    {
        lock (_delayGate)
        {
            _acceptDelayedPackets = false;
            _delayerStopRequested = true;
            _delayerForceDrain = forceDrain;
            Monitor.PulseAll(_delayGate);
        }
    }

    private bool TryEnqueueDelayedPacket(IntPtr packet, uint packetLen, WinDivertNative.Address address, long dueTicks)
    {
        if (!Volatile.Read(ref _acceptDelayedPackets))
            return false;

        lock (_delayGate)
        {
            if (!_acceptDelayedPackets
                || _delayerStopRequested
                || _delayedPackets.Count >= MaxDelayedPackets
                || _delayedPacketBytes + packetLen > MaxDelayedBytes)
            {
                LogDelayBackpressure();
                return false;
            }

            int length = checked((int)packetLen);
            var copy = new byte[length];
            Marshal.Copy(packet, copy, 0, length);

            long sequence = ++_delayedSequence;
            _delayedPackets.Enqueue(
                new DelayedPacket(copy, packetLen, address),
                new DelayedPacketPriority(dueTicks, sequence));
            _delayedPacketBytes += packetLen;
            Monitor.PulseAll(_delayGate);
            return true;
        }
    }

    private void DelayedSendLoop()
    {
        try
        {
            while (true)
            {
                DelayedPacket packet;
                lock (_delayGate)
                {
                    while (!TryDequeueDueDelayedPacket(out packet))
                    {
                        if (_delayerStopRequested && (!_delayerForceDrain || _delayedPackets.Count == 0))
                            return;

                        var wait = TimeUntilNextDelayedPacket();
                        if (wait == Timeout.InfiniteTimeSpan)
                            Monitor.Wait(_delayGate);
                        else
                            Monitor.Wait(_delayGate, wait);
                    }
                }

                SendDelayedPacket(packet);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Delayed-send worker faulted");
        }
    }

    private bool TryDequeueDueDelayedPacket(out DelayedPacket packet)
    {
        packet = default;
        if (_delayedPackets.Count == 0)
            return false;

        _delayedPackets.TryPeek(out _, out var priority);
        if (!_delayerForceDrain && priority.DueTicks > Stopwatch.GetTimestamp())
            return false;

        packet = _delayedPackets.Dequeue();
        _delayedPacketBytes -= packet.PacketLength;
        return true;
    }

    private TimeSpan TimeUntilNextDelayedPacket()
    {
        if (_delayedPackets.Count == 0)
            return Timeout.InfiniteTimeSpan;

        _delayedPackets.TryPeek(out _, out var priority);
        long now = Stopwatch.GetTimestamp();
        if (priority.DueTicks <= now)
            return TimeSpan.Zero;

        double seconds = (priority.DueTicks - now) / (double)Stopwatch.Frequency;
        return TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(seconds * 1000), 1, 1000));
    }

    private void SendDelayedPacket(DelayedPacket packet)
    {
        var address = packet.Address;
        if (!WinDivertNative.WinDivertSend(_handle, packet.Packet, packet.PacketLength, out _, ref address))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Debug("Delayed WinDivertSend failed; err={Err} len={Len}", err, packet.PacketLength);
        }
    }

    private void LogDelayBackpressure()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastDelayBackpressureLogTicks);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(5))
            return;

        if (Interlocked.CompareExchange(ref _lastDelayBackpressureLogTicks, now, last) == last)
        {
            Log.Warning(
                "Delayed packet queue is full or stopping; reinjecting packet immediately to preserve connectivity. queuedPackets={QueuedPackets} queuedBytes={QueuedBytes}",
                _delayedPackets.Count,
                _delayedPacketBytes);
        }
    }

    /// <summary>
    /// Look up the owning PID for the local endpoint of <paramref name="p"/>,
    /// find a matching rule, and reserve tokens. Returns the stopwatch timestamp when the
    /// packet should be reinjected.
    /// </summary>
    private long ApplyRules(PacketHeaders.Parsed p, bool outbound, long nowTicks)
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

        if (pid <= 0 || pid == 4) return nowTicks;

        int bytes = p.TotalLength;

        // Always tally per-PID for the live usage display, even if no rule matches.
        var counters = _pidCounters.GetOrAdd(pid, static _ => new ProcessCounters());
        if (outbound) Interlocked.Add(ref counters.UploadBytes, bytes);
        else Interlocked.Add(ref counters.DownloadBytes, bytes);

        if (compiled.IsEmpty) return nowTicks;
        var ruleState = compiled.MatchByPid(pid);
        if (ruleState is null) return nowTicks;

        if (outbound)
        {
            Interlocked.Add(ref ruleState.UploadBytesThisSecond, bytes);
            if (ruleState.UploadBucket is { } up)
                return up.Reserve(bytes);
        }
        else
        {
            Interlocked.Add(ref ruleState.DownloadBytesThisSecond, bytes);
            if (ruleState.DownloadBucket is { } down)
                return down.Reserve(bytes);
        }
        return nowTicks;
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
        if (procHandler is not null)
        {
            var samples = new List<ProcessThroughputSample>(drained.Count);
            foreach (var (pid, c) in drained)
            {
                samples.Add(new ProcessThroughputSample(
                    pid,
                    Interlocked.Read(ref c.DownloadBytes),
                    Interlocked.Read(ref c.UploadBytes)));
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

internal readonly record struct DelayedPacket(
    byte[] Packet,
    uint PacketLength,
    WinDivertNative.Address Address);

internal readonly record struct DelayedPacketPriority(long DueTicks, long Sequence) : IComparable<DelayedPacketPriority>
{
    public int CompareTo(DelayedPacketPriority other)
    {
        int due = DueTicks.CompareTo(other.DueTicks);
        return due != 0 ? due : Sequence.CompareTo(other.Sequence);
    }
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
        if (pid <= 0 || pid == 4)
            return null;

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
    private const int MinimumBurstBytes = 1500;

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
        _value = RuleMatchNormalizer.NormalizeForComparison(r.MatchKind, r.MatchValue);

        if (r.DownloadBytesPerSecond > 0)
            DownloadBucket = new TokenBucket(r.DownloadBytesPerSecond, Math.Max(r.DownloadBytesPerSecond, MinimumBurstBytes));
        if (r.UploadBytesPerSecond > 0)
            UploadBucket = new TokenBucket(r.UploadBytesPerSecond, Math.Max(r.UploadBytesPerSecond, MinimumBurstBytes));
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
                    result = string.Equals(name, _value, StringComparison.OrdinalIgnoreCase);
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
