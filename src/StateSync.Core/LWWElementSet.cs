using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StateSync.Core;

/// <summary>
/// Cài đặt cấu trúc Last-Writer-Wins Element Set (LWW-Element-Set) theo định nghĩa
/// chuẩn trong bài báo Marc Shapiro et al. (INRIA RR-7506, Specification 1 &amp; Section 3.3.3).
/// Phép Merge dùng max timestamp và tie-break payload xác định để hỗ trợ SEC,
/// hỗ trợ cập nhật đầy đủ Payload (LIMIT-04), chống Deadlock khi Merge ngược chiều (BUG-02),
/// và lưu ngưỡng thu gom rác nhân quả (_prunedHorizon) để chống sống lại phần tử sau GC (BUG-01).
/// </summary>
public class LWWElementSet<T> where T : notnull
{
    // Tập A: Lưu các phần tử đã được Add kèm cả đối tượng Key (chứa Payload mới nhất) và Timestamp lớn nhất
    private readonly Dictionary<T, (T KeyInstance, Timestamp Ts)> _addSet = new();

    // Tập R: Lưu các phần tử đã bị Remove kèm Timestamp lớn nhất (Tombstones)
    private readonly Dictionary<T, Timestamp> _removeSet = new();

    // Ngưỡng thời gian Tombstone lớn nhất đã được thu gom rác nhân quả (chống Resurrection sau GC - BUG-01)
    private Timestamp _prunedHorizon = new(0, string.Empty);

    // Khóa đồng bộ và định danh thứ tự khóa toàn cục (chống Deadlock khi Merge/Compare hai chiều - BUG-02)
    private readonly object _lock = new();
    private readonly long _instanceId = LockOrderHelper.NextInstanceId();

    /// <summary>
    /// Ngưỡng thời gian nhân quả lớn nhất đã được thu gom rác (GC Cutoff Horizon).
    /// </summary>
    public Timestamp PrunedHorizon
    {
        get
        {
            lock (_lock)
            {
                return _prunedHorizon;
            }
        }
    }

    /// <summary>
    /// Thêm hoặc cập nhật một phần tử vào tập hợp với timestamp xác định (update add).
    /// </summary>
    public bool Add(T element, Timestamp timestamp)
    {
        lock (_lock)
        {
            // Nếu thao tác Add mang dấu thời gian cũ hơn hoặc bằng ngưỡng đã thu gom rác (_prunedHorizon),
            // từ chối bản tin cũ/trùng (BUG-01).
            if (_prunedHorizon.Ticks > 0 && timestamp <= _prunedHorizon)
            {
                return false;
            }

            if (!_addSet.TryGetValue(element, out var existing))
            {
                _addSet[element] = (element, timestamp);
                return true;
            }

            if (ShouldReplace(element, timestamp, existing.KeyInstance, existing.Ts))
            {
                // Xóa trước khi gán lại để Dictionary cập nhật cả đối tượng Key mang Payload mới (LIMIT-04)
                _addSet.Remove(element);
                _addSet[element] = (element, timestamp);
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
            if (_prunedHorizon.Ticks > 0 && timestamp <= _prunedHorizon)
            {
                if (_addSet.TryGetValue(element, out var addEntry) && addEntry.Ts <= timestamp)
                {
                    _addSet.Remove(element);
                }
                return false;
            }

            if (!_removeSet.TryGetValue(element, out var existingTs) || timestamp > existingTs)
            {
                _removeSet.Remove(element);
                _removeSet[element] = timestamp;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Kiểm tra xem phần tử có đang tồn tại trong tập hợp hay không (query lookup).
    /// Theo RR-7506 Section 3.3.3: lookup(e) = ∃t, ∀t' &gt; t : (e, t) ∈ A ∧ (e, t') ∉ R
    /// </summary>
    public bool Lookup(T element)
    {
        lock (_lock)
        {
            if (!_addSet.TryGetValue(element, out var addEntry))
                return false;

            if (!_removeSet.TryGetValue(element, out var removeTs))
                return true;

            return addEntry.Ts > removeTs;
        }
    }

    /// <summary>
    /// Lấy toàn bộ danh sách các phần tử hiện hữu kèm đúng phiên bản Payload mới nhất (Active Elements).
    /// </summary>
    public HashSet<T> GetActiveElements()
    {
        lock (_lock)
        {
            var result = new HashSet<T>();
            foreach (var kvp in _addSet)
            {
                var (_, (keyInstance, addTs)) = kvp;
                if (!_removeSet.TryGetValue(keyInstance, out var removeTs) || addTs > removeTs)
                {
                    result.Add(keyInstance);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Phép so sánh thứ tự bộ phận trên bán dàn (Semilattice Partial Order: this &lt;= other).
    /// </summary>
    public bool Compare(LWWElementSet<T> other)
    {
        if (ReferenceEquals(this, other)) return true;

        return LockOrderHelper.ExecuteLocked(_lock, _instanceId, other._lock, other._instanceId, () =>
        {
            return CompareUnlocked(this, other);
        });
    }

    private static bool CompareUnlocked(LWWElementSet<T> left, LWWElementSet<T> right)
    {
        if (left._prunedHorizon.Ticks > 0)
        {
            if (right._prunedHorizon.Ticks == 0 || left._prunedHorizon > right._prunedHorizon)
                return false;

            foreach (var (elem, (_, rightTs)) in right._addSet)
            {
                if (rightTs <= left._prunedHorizon && !left._addSet.ContainsKey(elem))
                    return false;
            }
        }

        foreach (var (elem, (leftKey, ts)) in left._addSet)
        {
            if (right._prunedHorizon.Ticks > 0 && ts <= right._prunedHorizon && !right._addSet.ContainsKey(elem))
                continue;

            if (!right._addSet.TryGetValue(elem, out var rightEntry) || ts > rightEntry.Ts)
                return false;

            if (ts == rightEntry.Ts &&
                ShouldReplace(leftKey, ts, rightEntry.KeyInstance, rightEntry.Ts))
                return false;
        }

        foreach (var (elem, ts) in left._removeSet)
        {
            if (right._prunedHorizon.Ticks > 0 && ts <= right._prunedHorizon)
            {
                if (right._addSet.TryGetValue(elem, out var rightAdd) && rightAdd.Ts <= ts)
                    return false;
                continue;
            }

            if (!right._removeSet.TryGetValue(elem, out var otherTs) || ts > otherTs)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Kiểm tra tương đương trạng thái bán dàn (bao gồm cả tính tương đương của Payload): S ≡ T iff S &lt;= T và T &lt;= S.
    /// </summary>
    public bool SemilatticeEquals(LWWElementSet<T> other)
    {
        if (ReferenceEquals(this, other)) return true;

        return LockOrderHelper.ExecuteLocked(_lock, _instanceId, other._lock, other._instanceId, () =>
        {
            return CompareUnlocked(this, other) && CompareUnlocked(other, this);
        });
    }

    /// <summary>
    /// Hàm hợp nhất trạng thái (State-based Merge / Join-semilattice LUB: this ⊔ other).
    /// Tính chất toán học: Giao hoán (Commutative), Kết hợp (Associative), Lũy đẳng (Idempotent),
    /// an toàn đa luồng không Deadlock (BUG-02) và bảo toàn kết quả GC trước Snapshot cũ (BUG-01).
    /// </summary>
    public void Merge(LWWElementSet<T> other)
    {
        if (ReferenceEquals(this, other)) return;

        LockOrderHelper.ExecuteLocked(_lock, _instanceId, other._lock, other._instanceId, () =>
        {
            Timestamp effectiveHorizon = MaxHorizon(_prunedHorizon, other._prunedHorizon);

            // 1. Nếu `other` đã chạy GC tới `other._prunedHorizon`, loại bỏ các bản ghi Add cũ trong `this`
            // có timestamp <= other._prunedHorizon mà đã bị xóa và dọn sạch trên `other` (BUG-01).
            if (other._prunedHorizon.Ticks > 0)
            {
                var staleLocalAdds = new List<T>();
                foreach (var (elem, (_, localTs)) in _addSet)
                {
                    if (localTs <= other._prunedHorizon && !other._addSet.ContainsKey(elem))
                    {
                        staleLocalAdds.Add(elem);
                    }
                }
                foreach (var elem in staleLocalAdds)
                {
                    _addSet.Remove(elem);
                }
            }

            // 2. Hợp nhất các phần tử từ `other._addSet` (bỏ qua các bản ghi từ snapshot cũ trước mốc _prunedHorizon của `this`)
            foreach (var (element, (otherKey, otherTs)) in other._addSet)
            {
                if (_prunedHorizon.Ticks > 0 && otherTs <= _prunedHorizon && !_addSet.ContainsKey(element))
                {
                    continue;
                }

                if (!_addSet.TryGetValue(element, out var current) ||
                    ShouldReplace(otherKey, otherTs, current.KeyInstance, current.Ts))
                {
                    _addSet.Remove(element);
                    _addSet[element] = (otherKey, otherTs);
                }
            }

            // 3. Hợp nhất các Tombstone từ `other._removeSet`
            foreach (var (element, otherTs) in other._removeSet)
            {
                if (!_removeSet.TryGetValue(element, out var currentTs) || otherTs > currentTs)
                {
                    _removeSet.Remove(element);
                    _removeSet[element] = otherTs;
                }
            }

            // 4. Nếu có ngưỡng GC mới hiệu lực, dọn các bản ghi đã nằm dưới ngưỡng ổn định đã cắt tỉa
            if (effectiveHorizon.Ticks > 0)
            {
                var prunedRemoves = _removeSet
                    .Where(kvp => kvp.Value <= effectiveHorizon)
                    .ToList();

                foreach (var (elem, remTs) in prunedRemoves)
                {
                    if (_addSet.TryGetValue(elem, out var addEntry) && addEntry.Ts <= remTs)
                    {
                        _addSet.Remove(elem);
                    }
                    _removeSet.Remove(elem);
                }
            }

            _prunedHorizon = effectiveHorizon;
        });
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
    /// Khi một Tombstone đã ổn định (được mọi replica ghi nhận), ta xóa Tombstone đó,
    /// đồng thời cập nhật `_prunedHorizon` để ngăn chặn gói tin Add cũ bị lag hoặc Snapshot cũ làm sống lại phần tử (BUG-01).
    /// Caller phải bảo đảm một prefix ổn định toàn cục với tập replica cố định:
    /// mọi thao tác tới ngưỡng được chọn đã được xử lý và không có thao tác mới hợp lệ
    /// sinh ra dưới ngưỡng đó. Predicate tùy ý hoặc client offline chưa đồng bộ không đáp ứng hợp đồng này.
    /// </summary>
    public int PruneTombstones(Func<T, Timestamp, bool> isStable)
    {
        ArgumentNullException.ThrowIfNull(isStable);
        KeyValuePair<T, Timestamp>[] candidates;
        lock (_lock)
        {
            candidates = _removeSet.ToArray();
        }

        // A caller's stability callback may query/merge another set. Run it
        // outside our lock, then recheck each version before deleting anything.
        var toRemove = candidates.Where(kvp => isStable(kvp.Key, kvp.Value)).ToArray();
        lock (_lock)
        {
            int pruned = 0;

            foreach (var (elem, removeTs) in toRemove)
            {
                if (!_removeSet.TryGetValue(elem, out var current) || current != removeTs)
                    continue;
                pruned++;
                if (_prunedHorizon.Ticks == 0 || removeTs > _prunedHorizon)
                {
                    _prunedHorizon = removeTs;
                }

                if (_addSet.TryGetValue(elem, out var addEntry))
                {
                    if (addEntry.Ts <= removeTs)
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
            return pruned;
        }
    }

    /// <summary>
    /// Ước lượng kích thước Payload (Bytes), kích thước Metadata/Tombstone (Bytes), tỷ lệ Overhead (%)
    /// và dung lượng bộ nhớ RAM ước lượng (EstimatedRamBytes).
    /// Khi giỏ hàng trống (PayloadBytes == 0) nhưng vẫn còn Tombstone (MetadataBytes &gt; 0),
    /// OverheadPercentage trả về 100.0% (100% dữ liệu lưu trữ là metadata) để phân biệt rõ với giỏ trống hoàn toàn (0.0%) (BUG-05).
    /// </summary>
    public (long PayloadBytes, long MetadataBytes, double OverheadPercentage, long EstimatedRamBytes) CalculateOverhead()
    {
        lock (_lock)
        {
            long payloadBytes = 0;
            long metadataBytes = 0;

            foreach (var (elem, (keyInstance, addTs)) in _addSet)
            {
                int keyBytes = Encoding.UTF8.GetByteCount(keyInstance.ToString() ?? string.Empty);
                int fullItemPayloadBytes = keyInstance is IPayloadElement<T> payloadElem
                    ? payloadElem.GetEstimatedPayloadBytes()
                    : keyBytes + 48;
                int tsBytes = sizeof(long) + Encoding.UTF8.GetByteCount(addTs.ClientId ?? string.Empty);

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
                int tsBytes = sizeof(long) + Encoding.UTF8.GetByteCount(remTs.ClientId ?? string.Empty);
                metadataBytes += keyBytes + tsBytes;
            }

            // Dung lượng RAM ước lượng theo mô hình bộ nhớ (.NET Dictionary entry overhead ~ 32 bytes/entry + dữ liệu)
            long estimatedRamBytes = payloadBytes + metadataBytes + (_addSet.Count + _removeSet.Count) * 32L;

            // Khi PayloadBytes > 0: tính tỷ lệ MetadataBytes / PayloadBytes (%).
            // Khi PayloadBytes == 0 nhưng MetadataBytes > 0 (giỏ trống nhưng còn Tombstone): trả về 100.0%
            // để không bị nhầm với 0.0% của giỏ trống hoàn toàn không tốn metadata (BUG-05).
            double ratio = payloadBytes > 0
                ? (double)metadataBytes / payloadBytes * 100.0
                : (metadataBytes > 0 ? 100.0 : 0.0);

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
            clone._prunedHorizon = _prunedHorizon;
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

    private static Timestamp MaxHorizon(Timestamp a, Timestamp b)
    {
        if (a.Ticks == 0) return b;
        if (b.Ticks == 0) return a;
        return a >= b ? a : b;
    }
}
