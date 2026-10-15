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
    // Chỉ lưu các phần tử hiện có và timestamp lúc thêm
    private readonly Dictionary<T, Timestamp> _items = new();
    private readonly object _lock = new();

    public bool Add(T element, Timestamp timestamp)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(element, out var currentTs) || timestamp > currentTs)
            {
                _items[element] = timestamp;
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
            if (_items.TryGetValue(element, out var currentTs))
            {
                if (timestamp >= currentTs)
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
            return new HashSet<T>(_items.Keys);
        }
    }

    public void Merge(NaiveLWWSet<T> other)
    {
        lock (_lock)
        {
            lock (other._lock)
            {
                foreach (var (elem, otherTs) in other._items)
                {
                    if (!_items.TryGetValue(elem, out var currentTs) || otherTs > currentTs)
                    {
                        _items[elem] = otherTs;
                    }
                }
            }
        }
    }

    public (long PayloadBytes, long EstimatedRamBytes) CalculateMemoryFootprint()
    {
        lock (_lock)
        {
            long payloadBytes = 0;
            foreach (var elem in _items.Keys)
            {
                int keyBytes = Encoding.UTF8.GetByteCount(elem.ToString() ?? string.Empty);
                payloadBytes += keyBytes + 48;
            }
            long ramBytes = payloadBytes + _items.Count * 32L;
            return (payloadBytes, ramBytes);
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
}
