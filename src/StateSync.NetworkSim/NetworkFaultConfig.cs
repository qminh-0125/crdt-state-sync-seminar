using System;
using System.Collections.Generic;

namespace StateSync.NetworkSim;

/// <summary>
/// Cấu hình các tham số giả lập lỗi mạng tất định (Deterministic Network Fault Harness).
/// Bằng cách cố định Seed, mọi kịch bản thí nghiệm đều có thể tái lập 100%.
/// </summary>
public class NetworkFaultConfig
{
    public int Seed { get; set; } = 42;
    public int MinLatencyMs { get; set; } = 20;
    public int MaxLatencyMs { get; set; } = 100;
    public double PacketLossRate { get; set; } = 0.0;     // Tỷ lệ rớt gói (0.0 -> 1.0)
    public double DuplicationRate { get; set; } = 0.0;    // Tỷ lệ nhận trùng gói tin (0.0 -> 1.0)
    public double ReorderJitterMs { get; set; } = 0.0;    // Độ lệch thời gian gây xáo trộn thứ tự gói

    public static NetworkFaultConfig Ideal => new()
    {
        MinLatencyMs = 0,
        MaxLatencyMs = 0,
        PacketLossRate = 0,
        DuplicationRate = 0
    };

    public static NetworkFaultConfig HighLatency => new()
    {
        MinLatencyMs = 100,
        MaxLatencyMs = 300,
        PacketLossRate = 0.05,
        ReorderJitterMs = 50
    };

    public static NetworkFaultConfig ExtremeChaos => new()
    {
        MinLatencyMs = 50,
        MaxLatencyMs = 500,
        PacketLossRate = 0.15,
        DuplicationRate = 0.10,
        ReorderJitterMs = 100
    };
}
