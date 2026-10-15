using System;
using System.Collections.Generic;
using System.Linq;

namespace StateSync.Core;

/// <summary>
/// Cài đặt thuật toán phát hiện tính ổn định nhân quả (Causal Stability Detection)
/// của Wuu & Bernstein theo đúng định nghĩa tại Mục 4.1 của bài báo Marc Shapiro et al. (INRIA RR-7506):
/// Mỗi replica i duy trì đồng hồ VectorClock cục bộ và bảng các VectorClock biết được gần nhất
/// từ mọi replica j trong hệ thống: V_i^min(j).
/// Một thao tác/tombstone với timestamp t được gọi là ổn định (Stable - Φ_i(t))
/// khi mọi replica trong hệ thống đều đã ghi nhận tiến độ thời gian >= t.Ticks.
/// </summary>
public class CausalStabilityTracker
{
    private readonly HashSet<string> _knownReplicas = new();
    private readonly Dictionary<string, VectorClock> _replicaClocks = new();
    private readonly Dictionary<string, long> _scalarProgress = new();
    private readonly object _lock = new();

    public CausalStabilityTracker(IEnumerable<string> replicaIds)
    {
        foreach (var id in replicaIds)
        {
            _knownReplicas.Add(id);
            _replicaClocks[id] = new VectorClock();
            _scalarProgress[id] = 0;
        }
    }

    /// <summary>
    /// Cập nhật tiến độ thông qua đồng hồ VectorClock nhận được từ replica `replicaId`.
    /// </summary>
    public void ObserveVectorClock(string replicaId, VectorClock clock)
    {
        lock (_lock)
        {
            _knownReplicas.Add(replicaId);
            if (!_replicaClocks.TryGetValue(replicaId, out var existing))
            {
                existing = new VectorClock();
                _replicaClocks[replicaId] = existing;
            }
            existing.Merge(clock);

            long ownTick = existing.Get(replicaId);
            if (!_scalarProgress.TryGetValue(replicaId, out var curr) || ownTick > curr)
            {
                _scalarProgress[replicaId] = ownTick;
            }
        }
    }

    /// <summary>
    /// Cập nhật tiến độ thời gian logic mới nhất mà replica `replicaId` đã xác nhận xử lý.
    /// </summary>
    public void RecordReplicaProgress(string replicaId, long logicalTick)
    {
        lock (_lock)
        {
            _knownReplicas.Add(replicaId);
            if (!_scalarProgress.TryGetValue(replicaId, out var current) || logicalTick > current)
            {
                _scalarProgress[replicaId] = logicalTick;
            }

            if (!_replicaClocks.TryGetValue(replicaId, out var vc))
            {
                vc = new VectorClock();
                _replicaClocks[replicaId] = vc;
            }
            while (vc.Get(replicaId) < logicalTick)
            {
                vc.Increment(replicaId);
            }
        }
    }

    /// <summary>
    /// Tính ngưỡng thời gian ổn định toàn cục (Minimum Stable Watermark: min_j V_i[j]).
    /// Mọi Tombstone có Timestamp.Ticks <= GetStableHorizon() đều an toàn 100% để xóa bỏ!
    /// </summary>
    public long GetStableHorizon()
    {
        lock (_lock)
        {
            if (_knownReplicas.Count == 0) return 0;
            return _knownReplicas.Min(id => _scalarProgress.GetValueOrDefault(id, 0));
        }
    }

    /// <summary>
    /// Kiểm tra điều kiện ổn định Φ(ts): ∀j ∈ Replicas, V[j] >= ts.Ticks.
    /// </summary>
    public bool IsStable(Timestamp ts)
    {
        return ts.Ticks <= GetStableHorizon();
    }
}
