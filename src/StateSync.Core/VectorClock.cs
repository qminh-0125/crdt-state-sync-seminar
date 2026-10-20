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
    private readonly long _instanceId = LockOrderHelper.NextInstanceId();

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

    /// <summary>
    /// Cập nhật trực tiếp đồng hồ logic của client/replica lên mốc `tick` (độ phức tạp O(1)).
    /// </summary>
    public void AdvanceTo(string clientId, long tick)
    {
        lock (_lock)
        {
            if (!_clock.TryGetValue(clientId, out var current) || tick > current)
            {
                _clock[clientId] = tick;
            }
        }
    }

    public long Get(string clientId)
    {
        lock (_lock)
        {
            return _clock.TryGetValue(clientId, out var val) ? val : 0;
        }
    }

    internal long GetUnlocked(string clientId)
    {
        return _clock.TryGetValue(clientId, out var val) ? val : 0;
    }

    /// <summary>
    /// Hợp nhất với đồng hồ vector từ replica khác (lấy max từng vị trí, chống Deadlock khi Merge hai chiều - BUG-02).
    /// </summary>
    public void Merge(VectorClock other)
    {
        if (ReferenceEquals(this, other)) return;

        LockOrderHelper.ExecuteLocked(_lock, _instanceId, other._lock, other._instanceId, () =>
        {
            foreach (var (client, val) in other._clock)
            {
                if (!_clock.TryGetValue(client, out var current) || val > current)
                {
                    _clock[client] = val;
                }
            }
        });
    }

    /// <summary>
    /// Kiểm tra quan hệ xảy-ra-trước (Happened-Before: a -&gt; b):
    /// a &lt;= b nếu mọi phần tử của a &lt;= b và ít nhất một phần tử a &lt; b.
    /// </summary>
    public bool HappenedBefore(VectorClock other)
    {
        if (ReferenceEquals(this, other)) return false;

        return LockOrderHelper.ExecuteLocked(_lock, _instanceId, other._lock, other._instanceId, () =>
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
        });
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
