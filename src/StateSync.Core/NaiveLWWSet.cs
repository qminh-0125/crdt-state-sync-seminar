using System;
using System.Collections.Generic;
using System.Text;

namespace StateSync.Core;

/// <summary>
/// Hệ thống đối chứng Baseline: Sử dụng cơ chế Last-Write-Wins ngây thơ (Naive LWW)
/// không có cơ chế lưu vết phần tử đã xóa (Không có Tombstone).
/// Mô phỏng trực tiếp lỗi đụng độ và mất mát dữ liệu (Lost Updates / Resurrection Anomaly)
/// mà Amazon Shopping Cart từng gặp phải.
/// </summary>
public class NaiveLWWSet<T> where T : notnull
{
    // Chỉ lưu các phần tử hiện có kèm phiên bản Key/Payload mới nhất và timestamp lúc thêm
    private readonly Dictionary<T, (T KeyInstance, Timestamp Ts)> _items = new();
    private readonly object _lock = new();
    private readonly long _instanceId = LockOrderHelper.NextInstanceId();

    public bool Add(T element, Timestamp timestamp)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(element, out var current) ||
                ShouldReplace(element, timestamp, current.KeyInstance, current.Ts))
            {
                _items.Remove(element);
                _items[element] = (element, timestamp);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Xóa phần tử: Naive LWW chỉ đơn giản là xóa khỏi Dictionary mà không để lại vết (Tombstone).
    /// Hậu quả: Nếu có một gói tin Add cũ bị trễ mạng đến sau, phần tử sẽ bị 'sống lại' (Resurrect),
    /// hoặc khi Merge 2 replica sẽ phục hồi nhầm phần tử đã xóa.
    /// </summary>
    public bool Remove(T element, Timestamp timestamp)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(element, out var current))
            {
                if (timestamp >= current.Ts)
                {
                    _items.Remove(element);
                    return true;
                }
            }
            return false;
        }
    }

    public bool Lookup(T element)
    {
        lock (_lock)
        {
            return _items.ContainsKey(element);
        }
    }

    public HashSet<T> GetActiveElements()
    {
        lock (_lock)
        {
            var set = new HashSet<T>();
            foreach (var (_, (keyInstance, _)) in _items)
            {
                set.Add(keyInstance);
            }
            return set;
        }
    }

    public void Merge(NaiveLWWSet<T> other)
    {
        if (ReferenceEquals(this, other)) return;

        LockOrderHelper.ExecuteLocked(_lock, _instanceId, other._lock, other._instanceId, () =>
        {
            foreach (var (elem, (otherKey, otherTs)) in other._items)
            {
                if (!_items.TryGetValue(elem, out var current) ||
                    ShouldReplace(otherKey, otherTs, current.KeyInstance, current.Ts))
                {
                    _items.Remove(elem);
                    _items[elem] = (otherKey, otherTs);
                }
            }
        });
    }

    /// <summary>
    /// Số lượng Tombstone của Naive LWW (luôn bằng số bản ghi xóa được lưu = 0 vì Naive LWW xóa trực tiếp khỏi _items).
    /// </summary>
    public int TombstoneCount => 0;

    /// <summary>
    /// Ước lượng kích thước PayloadBytes, MetadataBytes (Timestamp đi kèm mỗi item đang active) và RAM.
    /// </summary>
    public (long PayloadBytes, long MetadataBytes, double OverheadPercentage, long EstimatedRamBytes) CalculateOverhead()
    {
        lock (_lock)
        {
            long payloadBytes = 0;
            long metadataBytes = 0;
            foreach (var (_, (keyInstance, ts)) in _items)
            {
                int keyBytes = Encoding.UTF8.GetByteCount(keyInstance.ToString() ?? string.Empty);
                int fullItemPayloadBytes = keyInstance is IPayloadElement<T> payloadElem
                    ? payloadElem.GetEstimatedPayloadBytes()
                    : keyBytes + 48;
                payloadBytes += fullItemPayloadBytes;
                metadataBytes += sizeof(long) + Encoding.UTF8.GetByteCount(ts.ClientId ?? string.Empty);
            }
            long ramBytes = payloadBytes + metadataBytes + _items.Count * 32L;
            double ratio = payloadBytes > 0
                ? (double)metadataBytes / payloadBytes * 100.0
                : (metadataBytes > 0 ? 100.0 : 0.0);
            return (payloadBytes, metadataBytes, ratio, ramBytes);
        }
    }

    public NaiveLWWSet<T> Clone()
    {
        lock (_lock)
        {
            var clone = new NaiveLWWSet<T>();
            foreach (var (k, v) in _items) clone._items[k] = v;
            return clone;
        }
    }

    private static bool ShouldReplace(T candidateKey, Timestamp candidateTs, T existingKey, Timestamp existingTs)
    {
        if (candidateTs > existingTs) return true;
        if (candidateTs < existingTs) return false;

        if (candidateKey is IPayloadElement<T> candPayload && existingKey is IPayloadElement<T> existPayload)
        {
            return StringComparer.Ordinal.Compare(
                candPayload.GetCanonicalPayloadString(),
                existPayload.GetCanonicalPayloadString()) > 0;
        }

        return false;
    }
}
