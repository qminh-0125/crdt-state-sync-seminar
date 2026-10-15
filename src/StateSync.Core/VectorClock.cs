using System;
using System.Collections.Generic;
using System.Linq;

namespace StateSync.Core;

/// <summary>
/// Đồng hồ vector (Vector Clock) để theo dõi quan hệ nhân quả (Causality)
/// và phục vụ thuật toán phát hiện tính ổn định (Stability Detection) để dọn rác Tombstone.
/// </summary>
public class VectorClock
{
    private readonly Dictionary<string, long> _clock = new();
    private readonly object _lock = new();

    public VectorClock() { }

    public VectorClock(IDictionary<string, long> initial)
    {
        foreach (var (k, v) in initial) _clock[k] = v;
    }

    /// <summary>
    /// Tăng giá trị đồng hồ của chính client/replica này.
    /// </summary>
    public long Increment(string clientId)
    {
        lock (_lock)
        {
            _clock.TryGetValue(clientId, out var val);
            val++;
            _clock[clientId] = val;
            return val;
        }
    }

    public long Get(string clientId)
    {
        lock (_lock)
        {
            return _clock.TryGetValue(clientId, out var val) ? val : 0;
        }
    }

    /// <summary>
    /// Hợp nhất với đồng hồ vector từ replica khác (lấy max từng vị trí).
    /// </summary>
    public void Merge(VectorClock other)
    {
        lock (_lock)
        {
            lock (other._lock)
            {
                foreach (var (client, val) in other._clock)
                {
                    if (!_clock.TryGetValue(client, out var current) || val > current)
                    {
                        _clock[client] = val;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Kiểm tra quan hệ xảy-ra-trước (Happened-Before: a -> b):
    /// a <= b nếu mọi phần tử của a <= b và ít nhất một phần tử a < b.
    /// </summary>
    public bool HappenedBefore(VectorClock other)
    {
        lock (_lock)
        {
            lock (other._lock)
            {
                bool strictlyLess = false;
                var allClients = _clock.Keys.Union(other._clock.Keys);

                foreach (var client in allClients)
                {
                    long thisVal = _clock.GetValueOrDefault(client, 0);
                    long otherVal = other._clock.GetValueOrDefault(client, 0);

                    if (thisVal > otherVal) return false;
                    if (thisVal < otherVal) strictlyLess = true;
                }

                return strictlyLess;
            }
        }
    }

    public VectorClock Clone()
    {
        lock (_lock)
        {
            return new VectorClock(_clock);
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return "{" + string.Join(", ", _clock.Select(kvp => $"{kvp.Key}:{kvp.Value}")) + "}";
        }
    }
}
