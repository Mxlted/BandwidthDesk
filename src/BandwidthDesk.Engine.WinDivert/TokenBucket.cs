using System;
using System.Diagnostics;

namespace BandwidthDesk.Engine.WinDivert;

/// <summary>
/// Simple thread-safe token bucket measured in bytes.
/// Capacity == burst allowed above the steady rate.
/// </summary>
internal sealed class TokenBucket
{
    private readonly object _gate = new();
    private double _tokens;
    private double _ratePerSecond;
    private double _capacity;
    private long _lastTicks;

    public TokenBucket(double ratePerSecond, double capacity)
    {
        _ratePerSecond = ratePerSecond;
        _capacity = capacity;
        _tokens = capacity;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public double RatePerSecond
    {
        get { lock (_gate) return _ratePerSecond; }
    }

    public void Reconfigure(double ratePerSecond, double capacity)
    {
        lock (_gate)
        {
            _ratePerSecond = ratePerSecond;
            _capacity = capacity;
            if (_tokens > capacity) _tokens = capacity;
            _lastTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Reserves tokens for <paramref name="bytes"/> and returns the stopwatch timestamp when the
    /// packet should be sent. The bucket may go negative to model accumulated pacing debt.
    /// </summary>
    public long Reserve(int bytes)
    {
        lock (_gate)
        {
            Refill();
            long now = Stopwatch.GetTimestamp();
            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return now;
            }

            double needed = bytes - _tokens;
            double seconds = needed / Math.Max(_ratePerSecond, 1);
            _tokens -= bytes;
            long delayTicks = (long)Math.Ceiling(seconds * Stopwatch.Frequency);
            return now + Math.Max(delayTicks, 1);
        }
    }

    /// <summary>
    /// Consume without throttling (used when no rate limit applies); still drains for telemetry.
    /// </summary>
    public void ForceConsume(int bytes)
    {
        lock (_gate)
        {
            Refill();
            _tokens -= bytes;
            if (_tokens < -_capacity) _tokens = -_capacity;
        }
    }

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
        if (elapsed <= 0) return;
        _lastTicks = now;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _ratePerSecond);
    }
}
