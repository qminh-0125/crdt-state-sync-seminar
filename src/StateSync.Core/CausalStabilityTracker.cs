using System;
using System.Collections.Generic;
using System.Linq;

namespace StateSync.Core;

/// <summary>
/// Cài đặt thuật toán phát hiện tính ổn định nhân quả (Causal Stability Detection)
/// của Wuu &amp; Bernstein theo đúng định nghĩa tại Mục 4.1 của bài báo Marc Shapiro et al. (INRIA RR-7506):
/// Mỗi replica i duy trì đồng hồ VectorClock cục bộ và bảng các VectorClock biết được gần nhất
/// từ mọi replica j trong hệ thống: M[observer][origin].
/// Một thao tác/tombstone với timestamp t = (Ticks, origin) được gọi là ổn định toàn cục (Stable - Φ(t))
/// khi mọi replica observer trong tập _knownReplicas đều đã ghi nhận tiến độ của mọi nguồn &gt;= t.Ticks.
/// Không sử dụng bất kỳ nhánh fallback vô hướng (scalar fallback) nào khi các replica chưa trao đổi VectorClock.
/// </summary>
public class CausalStabilityTracker
{
    private readonly HashSet<string> _knownReplicas = new();
    private readonly Dictionary<string, VectorClock> _replicaClocks = new();
    private readonly object _lock = new();
    private long _cachedHorizon;
    private bool _horizonDirty = true;

    public CausalStabilityTracker(IEnumerable<string> replicaIds)
    {
        foreach (var id in replicaIds)
        {
            _knownReplicas.Add(id);
            _replicaClocks[id] = new VectorClock();
        }
    }

    /// <summary>
    /// Cập nhật tiến độ thông qua đồng hồ VectorClock mà replica `observerReplicaId` đang nắm giữ.
    /// </summary>
    public void ObserveVectorClock(string observerReplicaId, VectorClock clock)
    {
        lock (_lock)
        {
            _knownReplicas.Add(observerReplicaId);
            if (!_replicaClocks.TryGetValue(observerReplicaId, out var existing))
            {
                existing = new VectorClock();
                _replicaClocks[observerReplicaId] = existing;
            }
            existing.Merge(clock);
            _horizonDirty = true;
        }
    }

    /// <summary>
    /// Ghi nhận rằng `observerReplicaId` đã xử lý các thao tác có nguồn gốc từ `originReplicaId` tới mốc `logicalTick` (O(1)).
    /// </summary>
    public void RecordReplicaProgress(string observerReplicaId, string originReplicaId, long logicalTick)
    {
        lock (_lock)
        {
            _knownReplicas.Add(observerReplicaId);
            _knownReplicas.Add(originReplicaId);

            if (!_replicaClocks.TryGetValue(observerReplicaId, out var vc))
            {
                vc = new VectorClock();
                _replicaClocks[observerReplicaId] = vc;
            }
            vc.AdvanceTo(originReplicaId, logicalTick);

            if (!_replicaClocks.ContainsKey(originReplicaId))
            {
                _replicaClocks[originReplicaId] = new VectorClock();
            }
            _horizonDirty = true;
        }
    }

    /// <summary>
    /// Ghi nhận tiến độ cục bộ của chính `replicaId` (tương đương RecordReplicaProgress(replicaId, replicaId, logicalTick)).
    /// </summary>
    public void RecordReplicaProgress(string replicaId, long logicalTick)
    {
        RecordReplicaProgress(replicaId, replicaId, logicalTick);
    }

    /// <summary>
    /// Tính ngưỡng thời gian ổn định toàn cục (Minimum Stable Watermark) theo ma trận Wuu-Bernstein 2 chiều:
    /// min_{origin ∈ KnownReplicas} min_{observer ∈ KnownReplicas} M[observer][origin].
    /// Trả về 0 nếu bất kỳ cặp (observer, origin) nào chưa từng đồng bộ tiến độ với nhau.
    /// </summary>
    public long GetStableHorizon()
    {
        lock (_lock)
        {
            return GetStableHorizonUnlocked();
        }
    }

    /// <summary>
    /// Kiểm tra điều kiện ổn định nhân quả Wuu-Bernstein Φ(ts) nghiêm ngặt (độ phức tạp O(1) nhờ đệm ngưỡng):
    /// Một thao tác ts = (Ticks, ClientId) là ổn định toàn cục khi MỌI replica observer trong hệ thống
    /// đều đã nhận và hợp nhất trạng thái của toàn hệ thống tới ít nhất mốc ts.Ticks:
    /// ts.Ticks &lt;= GetStableHorizon().
    /// </summary>
    public bool IsStable(Timestamp ts)
    {
        lock (_lock)
        {
            if (_knownReplicas.Count == 0 || string.IsNullOrEmpty(ts.ClientId) || !_knownReplicas.Contains(ts.ClientId))
                return false;

            long horizon = GetStableHorizonUnlocked();
            return horizon > 0 && ts.Ticks <= horizon;
        }
    }

    private long GetStableHorizonUnlocked()
    {
        if (_knownReplicas.Count == 0) return 0;
        if (!_horizonDirty) return _cachedHorizon;

        long minVal = long.MaxValue;
        foreach (var origin in _knownReplicas)
        {
            foreach (var observer in _knownReplicas)
            {
                long v = _replicaClocks.TryGetValue(observer, out var vc) ? vc.GetUnlocked(origin) : 0;
                if (v < minVal)
                {
                    minVal = v;
                    if (minVal == 0)
                    {
                        _cachedHorizon = 0;
                        _horizonDirty = false;
                        return 0;
                    }
                }
            }
        }

        _cachedHorizon = minVal == long.MaxValue ? 0 : minVal;
        _horizonDirty = false;
        return _cachedHorizon;
    }
}
