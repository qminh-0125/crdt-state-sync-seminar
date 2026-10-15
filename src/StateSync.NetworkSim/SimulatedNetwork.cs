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
        _config = config ?? new NetworkFaultConfig();
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
    /// Thực thi chuyển phát toàn bộ các gói tin trong hàng đợi theo đúng thứ tự thời gian đến (DeliveryTimeMs).
    /// Các gói gửi sau nhưng có Latency nhỏ hơn sẽ tự động đến trước (mô phỏng chính xác Packet Reordering).
    /// </summary>
    public void FlushVirtualQueue()
    {
        List<NetworkMessage> orderedMessages;
        lock (_lock)
        {
            // Đếm số gói bị đảo thứ tự so với lúc gửi
            long lastSendTime = -1;
            orderedMessages = _virtualQueue.OrderBy(m => m.DeliveryTimeMs).ToList();
            foreach (var m in orderedMessages)
            {
                if (m.SendTimeMs < lastSendTime)
                {
                    TotalReordered++;
                }
                lastSendTime = Math.Max(lastSendTime, m.SendTimeMs);
            }
            _virtualQueue.Clear();
        }

        var flushSw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var msg in orderedMessages)
        {
            Deliver(msg, flushSw.Elapsed.TotalMilliseconds);
        }
        flushSw.Stop();
    }

    /// <summary>
    /// Tính toán phân vị độ trễ thứ 95 (P95 Latency) từ tập dữ liệu đo thực tế (Network Delay + CPU Handler Time).
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
        TotalSent++;

        if (!CanCommunicate(senderId, receiverId))
        {
            TotalDropped++;
            return;
        }

        if (_config.PacketLossRate > 0 && _random.NextDouble() < _config.PacketLossRate)
        {
            TotalDropped++;
            return;
        }

        int latency = _config.MinLatencyMs;
        if (_config.MaxLatencyMs > _config.MinLatencyMs)
        {
            latency = _random.Next(_config.MinLatencyMs, _config.MaxLatencyMs);
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var msg = new NetworkMessage(
            Guid.NewGuid().ToString("N"),
            senderId,
            receiverId,
            payload,
            now,
            now + latency
        );

        bool duplicate = _config.DuplicationRate > 0 && _random.NextDouble() < _config.DuplicationRate;

        _ = Task.Run(async () =>
        {
            var realSw = System.Diagnostics.Stopwatch.StartNew();
            if (latency > 0) await Task.Delay(latency);
            Deliver(msg, realSw.Elapsed.TotalMilliseconds);

            if (duplicate)
            {
                TotalDuplicated++;
                await Task.Delay(20);
                Deliver(msg, realSw.Elapsed.TotalMilliseconds);
            }
        });
    }

    private void Deliver(NetworkMessage msg, double queueElapsedMs)
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
            double totalEndToEndMs = (msg.DeliveryTimeMs - msg.SendTimeMs) + queueElapsedMs + cpuApplyMs;

            lock (_lock)
            {
                TotalDelivered++;
                _latenciesMs.Add(totalEndToEndMs);
            }
        }
    }
}
