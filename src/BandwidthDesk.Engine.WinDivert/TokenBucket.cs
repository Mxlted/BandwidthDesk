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
    /// Tries to consume <paramref name="bytes"/> tokens. If insufficient, returns the milliseconds the caller
    /// should wait before retrying. Returns 0 on success.
    /// </summary>
    public int TryConsume(int bytes)
    {
        lock (_gate)
        {
            Refill();
            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return 0;
            }

            double needed = bytes - _tokens;
            double seconds = needed / Math.Max(_ratePerSecond, 1);
            int ms = (int)Math.Ceiling(seconds * 1000);
            return Math.Max(ms, 1);
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
