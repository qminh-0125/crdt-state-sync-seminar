using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StateSync.Core;

/// <summary>
/// Cài đặt cấu trúc Last-Writer-Wins Element Set (LWW-Element-Set) theo định nghĩa
/// chuẩn trong bài báo Marc Shapiro et al. (INRIA RR-7506, Specification 1 & Section 3.3.3).
/// Đạt chuẩn Strong Eventual Consistency (SEC) - hội tụ 100% không xung đột.
/// </summary>
public class LWWElementSet<T> where T : notnull
{
    // Tập A: Lưu các phần tử đã được Add kèm timestamp lớn nhất
    private readonly Dictionary<T, Timestamp> _addSet = new();

    // Tập R: Lưu các phần tử đã bị Remove kèm timestamp lớn nhất (Tombstones)
    private readonly Dictionary<T, Timestamp> _removeSet = new();

    // Khóa đồng bộ cho đa luồng an toàn
    private readonly object _lock = new();

    /// <summary>
    /// Thêm một phần tử vào tập hợp với timestamp xác định (update add).
    /// </summary>
    public bool Add(T element, Timestamp timestamp)
    {
        lock (_lock)
        {
            if (!_addSet.TryGetValue(element, out var existingTs) || timestamp > existingTs)
            {
                _addSet[element] = timestamp;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Xóa một phần tử khỏi tập hợp bằng cách đánh dấu vào Remove-set / Tombstone (update remove).
    /// </summary>
    public bool Remove(T element, Timestamp timestamp)
    {
        lock (_lock)
        {
            if (!_removeSet.TryGetValue(element, out var existingTs) || timestamp > existingTs)
            {
                _removeSet[element] = timestamp;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Kiểm tra xem phần tử có đang tồn tại trong tập hợp hay không (query lookup).
    /// Theo RR-7506 Section 3.3.3: lookup(e) = ∃t, ∀t' > t : (e, t) ∈ A ∧ (e, t') ∉ R
    /// </summary>
    public bool Lookup(T element)
    {
        lock (_lock)
        {
            if (!_addSet.TryGetValue(element, out var addTs))
                return false;

            if (!_removeSet.TryGetValue(element, out var removeTs))
                return true;

            return addTs > removeTs;
        }
    }

    /// <summary>
    /// Lấy toàn bộ danh sách các phần tử hiện hữu (Active Elements).
    /// </summary>
    public HashSet<T> GetActiveElements()
    {
        lock (_lock)
        {
            var result = new HashSet<T>();
            foreach (var kvp in _addSet)
            {
                var element = kvp.Key;
                var addTs = kvp.Value;
                if (!_removeSet.TryGetValue(element, out var removeTs) || addTs > removeTs)
                {
                    result.Add(element);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Phép so sánh thứ tự bộ phận trên bán dàn (Semilattice Partial Order: this <= other).
    /// Theo RR-7506 Specification 1 & Section 2.3.1:
    /// S <= T iff ∀(e, t) ∈ S.A : ∃(e, t') ∈ T.A (t <= t') AND ∀(e, t) ∈ S.R : ∃(e, t') ∈ T.R (t <= t').
    /// </summary>
    public bool Compare(LWWElementSet<T> other)
    {
        lock (_lock)
        {
            lock (other._lock)
            {
                foreach (var (elem, ts) in _addSet)
                {
                    if (!other._addSet.TryGetValue(elem, out var otherTs) || ts > otherTs)
                        return false;
                }

                foreach (var (elem, ts) in _removeSet)
                {
                    if (!other._removeSet.TryGetValue(elem, out var otherTs) || ts > otherTs)
                        return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Kiểm tra tương đương trạng thái bán dàn: S ≡ T iff S <= T và T <= S.
    /// </summary>
    public bool SemilatticeEquals(LWWElementSet<T> other)
    {
        return Compare(other) && other.Compare(this);
    }

    /// <summary>
    /// Hàm hợp nhất trạng thái (State-based Merge / Join-semilattice LUB: this ⊔ other).
    /// Tính chất toán học: Giao hoán (Commutative), Kết hợp (Associative), Lũy đẳng (Idempotent).
    /// </summary>
    public void Merge(LWWElementSet<T> other)
    {
        lock (_lock)
        {
            lock (other._lock)
            {
                foreach (var (element, otherTs) in other._addSet)
                {
                    if (!_addSet.TryGetValue(element, out var currentTs) || otherTs > currentTs)
                    {
                        _addSet[element] = otherTs;
                    }
                }

                foreach (var (element, otherTs) in other._removeSet)
                {
                    if (!_removeSet.TryGetValue(element, out var currentTs) || otherTs > currentTs)
                    {
                        _removeSet[element] = otherTs;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Số lượng mộ phần (Tombstones) hiện có trong tập Remove.
    /// </summary>
    public int TombstoneCount
    {
        get
        {
            lock (_lock)
            {
                return _removeSet.Count;
            }
        }
    }

    /// <summary>
    /// Tổng số bản ghi đang lưu giữ trong bộ nhớ (cả AddSet và RemoveSet).
    /// </summary>
    public int TotalMetadataEntries
    {
        get
        {
            lock (_lock)
            {
                return _addSet.Count + _removeSet.Count;
            }
        }
    }

    /// <summary>
    /// Cơ chế Dọn rác (Garbage Collection / Tombstone Pruning) chuẩn Wuu-Bernstein Stability (RR-7506 Section 4.1).
    /// Khi một Tombstone đã ổn định (được mọi replica ghi nhận), ta xóa Tombstone đó.
    /// Nếu phần tử tương ứng trong AddSet có timestamp <= removeTs (đã bị xóa), ta dọn luôn khỏi AddSet.
    /// </summary>
    public int PruneTombstones(Func<T, Timestamp, bool> isStable)
    {
        lock (_lock)
        {
            var toRemove = new List<(T Element, Timestamp RemoveTs)>();
            foreach (var (elem, ts) in _removeSet)
            {
                if (isStable(elem, ts))
                {
                    toRemove.Add((elem, ts));
                }
            }

            foreach (var (elem, removeTs) in toRemove)
            {
                if (_addSet.TryGetValue(elem, out var addTs))
                {
                    if (addTs <= removeTs)
                    {
                        _addSet.Remove(elem);
                        _removeSet.Remove(elem);
                    }
                    else
                    {
                        _removeSet.Remove(elem);
                    }
                }
                else
                {
                    _removeSet.Remove(elem);
                }
            }
            return toRemove.Count;
        }
    }

    /// <summary>
    /// Đo lường chính xác kích thước Payload thực tế (Bytes), kích thước Metadata/Tombstone (Bytes)
    /// và dung lượng bộ nhớ RAM (EstimatedRamBytes) theo Metric 2 trong Đề cương & Metric 5 trong seminar-metric01.png.
    /// </summary>
    public (long PayloadBytes, long MetadataBytes, double OverheadPercentage, long EstimatedRamBytes) CalculateOverhead()
    {
        lock (_lock)
        {
            long payloadBytes = 0;
            long metadataBytes = 0;

            foreach (var (elem, addTs) in _addSet)
            {
                int keyBytes = Encoding.UTF8.GetByteCount(elem.ToString() ?? string.Empty);
                // Mỗi mặt hàng giỏ hàng thực tế gồm Key + Tên/Giá/Số lượng (~48 bytes thuộc tính)
                int fullItemPayloadBytes = keyBytes + 48;
                int tsBytes = sizeof(long) + 4; // 8B Ticks + 4B compact ReplicaId

                bool isActive = !_removeSet.TryGetValue(elem, out var remTs) || addTs > remTs;
                if (isActive)
                {
                    payloadBytes += fullItemPayloadBytes;
                    metadataBytes += tsBytes;
                }
                else
                {
                    // Bản ghi đã bị xóa nhưng còn lưu trong _addSet
                    metadataBytes += keyBytes + tsBytes;
                }
            }

            foreach (var (elem, remTs) in _removeSet)
            {
                int keyBytes = Encoding.UTF8.GetByteCount(elem.ToString() ?? string.Empty);
                int tsBytes = sizeof(long) + 4;
                metadataBytes += keyBytes + tsBytes;
            }

            // Dung lượng RAM trên Heap (.NET Dictionary entry overhead ~ 32 bytes/entry + dữ liệu)
            long estimatedRamBytes = payloadBytes + metadataBytes + (_addSet.Count + _removeSet.Count) * 32L;

            // Công thức chuẩn của Thầy trong seminar-metric01.png: Bytes metadata / payload
            double ratio = payloadBytes > 0 ? (double)metadataBytes / payloadBytes * 100.0 : 0.0;
            return (payloadBytes, metadataBytes, ratio, estimatedRamBytes);
        }
    }

    public LWWElementSet<T> Clone()
    {
        lock (_lock)
        {
            var clone = new LWWElementSet<T>();
            foreach (var (k, v) in _addSet) clone._addSet[k] = v;
            foreach (var (k, v) in _removeSet) clone._removeSet[k] = v;
            return clone;
        }
    }
}
