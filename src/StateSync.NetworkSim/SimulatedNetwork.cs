using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StateSync.NetworkSim;

public record NetworkMessage(
    string MessageId,
    string SenderId,
    string ReceiverId,
    object Payload,
    long SendTimeMs,
    long DeliveryTimeMs
);

/// <summary>
/// Bộ giả lập mạng tất định (Deterministic Network Fault Simulator).
/// Hỗ trợ cả 2 chế độ:
/// 1. Discrete-Event Virtual-Time Engine: Chạy 50,000 thao tác qua mạng giả lập (Delay, Reorder, Loss, Duplicate, Partition) trong vài giây cho Benchmark.
/// 2. Real-Time Async Engine: Dành cho Demo Web SignalR thời gian thực.
/// </summary>
public class SimulatedNetwork
{
    private readonly NetworkFaultConfig _config;
    private readonly Random _random;
    private readonly Dictionary<string, Action<NetworkMessage>> _handlers = new();
    private readonly List<NetworkMessage> _virtualQueue = new();
    private readonly List<double> _latenciesMs = new();
    private readonly object _lock = new();
    private long _lastDeliveredSendTime = -1;

    // Quản lý phân vùng mạng (Network Partition / Split-Brain)
    private List<HashSet<string>> _partitions = new();

    // Thống kê metric
    public long TotalSent { get; private set; }
    public long TotalDelivered { get; private set; }
    public long TotalDropped { get; private set; }
    public long TotalDuplicated { get; private set; }
    public long TotalReordered { get; private set; }

    public SimulatedNetwork(NetworkFaultConfig? config = null)
    {
        config ??= new NetworkFaultConfig();
        if (config.MinLatencyMs < 0 || config.MaxLatencyMs < config.MinLatencyMs || config.MaxLatencyMs == int.MaxValue ||
            !double.IsFinite(config.ReorderJitterMs) || config.ReorderJitterMs < 0 || config.ReorderJitterMs >= int.MaxValue ||
            !double.IsFinite(config.PacketLossRate) || config.PacketLossRate < 0 || config.PacketLossRate > 1 ||
            !double.IsFinite(config.DuplicationRate) || config.DuplicationRate < 0 || config.DuplicationRate > 1)
            throw new ArgumentOutOfRangeException(nameof(config), "Invalid latency, jitter or probability.");
        // Take a copy so callers cannot change the experiment during delivery.
        _config = new NetworkFaultConfig
        {
            Seed = config.Seed, MinLatencyMs = config.MinLatencyMs, MaxLatencyMs = config.MaxLatencyMs,
            ReorderJitterMs = config.ReorderJitterMs, PacketLossRate = config.PacketLossRate,
            DuplicationRate = config.DuplicationRate
        };
        _random = new Random(_config.Seed);
    }

    public void RegisterNode(string nodeId, Action<NetworkMessage> onReceive)
    {
        lock (_lock)
        {
            _handlers[nodeId] = onReceive;
        }
    }

    /// <summary>
    /// Thiết lập phân vùng mạng: Các node khác group sẽ không thể nói chuyện với nhau.
    /// </summary>
    public void Partition(params IEnumerable<string>[] groups)
    {
        lock (_lock)
        {
            _partitions = groups.Select(g => new HashSet<string>(g)).ToList();
        }
    }

    /// <summary>
    /// Khôi phục kết nối mạng bình thường (Heal Partition).
    /// </summary>
    public void HealPartition()
    {
        lock (_lock)
        {
            _partitions.Clear();
        }
    }

    public bool CanCommunicate(string sender, string receiver)
    {
        lock (_lock)
        {
            if (_partitions.Count == 0) return true;

            int senderGroup = _partitions.FindIndex(g => g.Contains(sender));
            int receiverGroup = _partitions.FindIndex(g => g.Contains(receiver));

            if (senderGroup != -1 && receiverGroup != -1)
            {
                return senderGroup == receiverGroup;
            }

            return senderGroup == -1 && receiverGroup == -1;
        }
    }

    /// <summary>
    /// Đưa gói tin vào hàng đợi sự kiện rời rạc (Discrete-Event Simulation) với đầy đủ:
    /// Độ trễ (Latency), Biến thiên gây đảo thứ tự (Reorder Jitter), Rớt gói (Packet Loss), Nhân bản (Duplication).
    /// </summary>
    public void EnqueueVirtual(string senderId, string receiverId, object payload, long currentVirtualTimeMs)
    {
        lock (_lock)
        {
            TotalSent++;

            // 1. Kiểm tra phân vùng mạng
            if (!CanCommunicate(senderId, receiverId))
            {
                TotalDropped++;
                return;
            }

            // 2. Giả lập rớt gói (Packet Loss)
            if (_config.PacketLossRate > 0 && _random.NextDouble() < _config.PacketLossRate)
            {
                TotalDropped++;
                return;
            }

            // 3. Tính toán độ trễ (Latency + Reorder Jitter)
            int baseLatency = _config.MinLatencyMs;
            if (_config.MaxLatencyMs > _config.MinLatencyMs)
            {
                baseLatency = _random.Next(_config.MinLatencyMs, _config.MaxLatencyMs + 1);
            }

            int jitter = _config.ReorderJitterMs > 0
                ? _random.Next(0, (int)_config.ReorderJitterMs + 1)
                : 0;

            long deliveryTime = currentVirtualTimeMs + baseLatency + jitter;

            var msg = new NetworkMessage(
                Guid.NewGuid().ToString("N"),
                senderId,
                receiverId,
                payload,
                currentVirtualTimeMs,
                deliveryTime
            );

            _virtualQueue.Add(msg);

            // 4. Giả lập gói tin lặp lại (Duplication)
            if (_config.DuplicationRate > 0 && _random.NextDouble() < _config.DuplicationRate)
            {
                TotalDuplicated++;
                long dupDeliveryTime = deliveryTime + _random.Next(5, 25);
                _virtualQueue.Add(msg with { DeliveryTimeMs = dupDeliveryTime });
            }
        }
    }

    /// <summary>
    /// Thực thi chuyển phát các gói tin có DeliveryTimeMs <= maxDeliveryTimeMs theo đúng thứ tự thời gian đến.
    /// Trả về thời gian xử lý thực tế (elapsed wall-clock) (ms) đo bằng Stopwatch để thực thi đợt đồng bộ này.
    /// </summary>
    public double FlushVirtualQueueUpTo(long maxDeliveryTimeMs)
    {
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        List<NetworkMessage> readyMessages;
        lock (_lock)
        {
            if (_virtualQueue.Count == 0) return 0.0;

            readyMessages = _virtualQueue
                .Where(m => m.DeliveryTimeMs <= maxDeliveryTimeMs)
                .OrderBy(m => m.DeliveryTimeMs)
                .ToList();

            if (readyMessages.Count == 0) return 0.0;

            _virtualQueue.RemoveAll(m => m.DeliveryTimeMs <= maxDeliveryTimeMs);

            foreach (var m in readyMessages)
            {
                if (m.SendTimeMs < _lastDeliveredSendTime)
                {
                    TotalReordered++;
                }
                _lastDeliveredSendTime = Math.Max(_lastDeliveredSendTime, m.SendTimeMs);
            }
        }

        foreach (var msg in readyMessages)
        {
            Deliver(msg);
        }
        long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        return (double)(endTicks - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// Thực thi chuyển phát toàn bộ các gói tin còn lại trong hàng đợi theo đúng thứ tự thời gian đến (DeliveryTimeMs).
    /// </summary>
    public double FlushVirtualQueue()
    {
        return FlushVirtualQueueUpTo(long.MaxValue);
    }

    /// <summary>
    /// Tính toán phân vị độ trễ thứ 95 (P95 Latency) của đường truyền mạng giả lập kèm thời gian xử lý gói tin.
    /// </summary>
    public double GetP95LatencyMs()
    {
        lock (_lock)
        {
            if (_latenciesMs.Count == 0) return 0.0;
            var sorted = _latenciesMs.OrderBy(x => x).ToList();
            int idx = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
            idx = Math.Clamp(idx, 0, sorted.Count - 1);
            return sorted[idx];
        }
    }

    /// <summary>
    /// Gửi tin nhắn qua mạng bất đồng bộ thời gian thực (dành cho Demo Web SignalR).
    /// </summary>
    public async Task SendAsync(string senderId, string receiverId, object payload)
    {
        NetworkMessage msg;
        int latency;
        int duplicateDelay;
        bool duplicate;
        lock (_lock)
        {
            TotalSent++;
            if (!CanCommunicate(senderId, receiverId) ||
                (_config.PacketLossRate > 0 && _random.NextDouble() < _config.PacketLossRate))
            {
                TotalDropped++;
                return;
            }
            latency = _random.Next(_config.MinLatencyMs, _config.MaxLatencyMs + 1);
            latency = checked(latency + (_config.ReorderJitterMs > 0
                ? _random.Next(0, (int)_config.ReorderJitterMs + 1) : 0));
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            msg = new NetworkMessage(Guid.NewGuid().ToString("N"), senderId, receiverId, payload, now, now + latency);
            duplicate = _config.DuplicationRate > 0 && _random.NextDouble() < _config.DuplicationRate;
            duplicateDelay = duplicate ? _random.Next(5, 25) : 0;
            if (duplicate)
                TotalDuplicated++;
        }

        if (latency > 0) await Task.Delay(latency);
        Deliver(msg);
        if (duplicate)
        {
            await Task.Delay(duplicateDelay);
            Deliver(msg with { DeliveryTimeMs = msg.DeliveryTimeMs + duplicateDelay });
        }
    }

    private void Deliver(NetworkMessage msg)
    {
        Action<NetworkMessage>? handler;
        lock (_lock)
        {
            _handlers.TryGetValue(msg.ReceiverId, out handler);
        }

        if (handler != null)
        {
            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            handler(msg);
            long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double cpuApplyMs = (double)(endTicks - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double totalEndToEndMs = (msg.DeliveryTimeMs - msg.SendTimeMs) + cpuApplyMs;

            lock (_lock)
            {
                TotalDelivered++;
                _latenciesMs.Add(totalEndToEndMs);
            }
        }
    }
}
