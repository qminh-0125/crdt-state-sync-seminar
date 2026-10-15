# StateSync: A Conflict-Free Shared-State Web Architecture for Collaborative State Synchronization via CRDT under High Concurrency

> **Đề tài Seminar Kỹ nghệ Phần mềm (Software Engineering)**  
> **Sinh viên thực hiện:** Trần Quang Minh (MSSV: `23110187`)  
> **Tên đề tài (VI):** Mô hình hóa và cài đặt thuật toán đồng bộ CRDT cho ứng dụng web chia sẻ trạng thái thời gian thực.

---

## 1. Giới thiệu & Vấn đề Nghiên cứu

Trong các ứng dụng web chia sẻ trạng thái thời gian thực (như Giỏ hàng dùng chung — *Collaborative Shopping Cart*), các phương pháp đồng bộ cơ bản dựa trên ghi đè trực tiếp (**Naive Last-Write-Wins — Baseline**) thường dẫn đến **phân rã trạng thái (State Divergence)**, **mất mát cập nhật (Lost Updates)** và **hiện tượng phần tử đã xóa tự sống lại (Resurrection Anomaly)** khi mạng có độ trễ biến thiên (Jitter), đảo thứ tự gói tin (Reordering) hoặc chia cắt phân vùng mạng (Split-Brain Partition).

Dự án **StateSync** tự cài đặt từ đầu (không phụ thuộc thư viện ngoài) cấu trúc dữ liệu **CRDT (`LWW-Element-Set` & `OR-Set`)** kết hợp cơ chế dọn rác nhân quả **Wuu-Bernstein VectorClock Garbage Collection** trên nền tảng **C# (.NET 10) & ASP.NET Core SignalR**, nhằm:
1. Chứng minh tính đúng đắn **Strong Eventual Consistency (SEC)**: đạt **100% tỷ lệ hội tụ** và **0% mất mát cập nhật**.
2. Định lượng sự đánh đổi tài nguyên (**Metadata / Tombstone Overhead vs. Throughput**) dưới tải đồng thời cao (`10,000 – 50,000` thao tác, `2 – 50` clients ảo).

---

## 2. Cấu trúc Mã nguồn (Solution Architecture)

```text
crdt-state-sync-seminar/
├── src/
│   ├── StateSync.slnx                 # .NET 10 Solution
│   ├── StateSync.Core/                # Lõi thuật toán CRDT & Baseline (Thuần C#)
│   │   ├── Timestamp.cs               # Dấu thời gian toàn phần (Logical Ticks, ClientId)
│   │   ├── LWWElementSet.cs           # LWW-Element-Set CRDT (Add-Set, Remove-Set, Compare <=, Merge LUB)
│   │   ├── ORSet.cs                   # Observed-Remove Set CRDT (Add-Wins semantics)
│   │   ├── NaiveLWWSet.cs             # Hệ thống đối chứng Baseline (Naive LWW không có Tombstone)
│   │   ├── VectorClock.cs             # Đồng hồ vector theo dõi quan hệ nhân quả (Happened-Before)
│   │   ├── CausalStabilityTracker.cs  # Thuật toán Wuu-Bernstein Stability Detection để tỉa Tombstone
│   │   └── ShoppingCartItem.cs        # Model mặt hàng trong Giỏ hàng dùng chung
│   ├── StateSync.NetworkSim/          # Bộ giả lập lỗi mạng tất định (Deterministic Network Fault Harness)
│   │   ├── NetworkFaultConfig.cs      # Cấu hình Seed, Latency, Reorder Jitter, Packet Loss, Duplication
│   │   └── SimulatedNetwork.cs        # Discrete-Event & Async Network Simulator (hỗ trợ Split-Brain Partition)
│   ├── StateSync.Server/              # Ứng dụng Web ASP.NET Core SignalR
│   │   ├── StateSyncHub.cs            # SignalR Hub đồng bộ giỏ hàng thời gian thực (CRDT vs Baseline)
│   │   ├── Program.cs                 # Cấu hình Web Server & REST API
│   │   └── wwwroot/index.html         # Giao diện Dashboard trực quan tiêm lỗi mạng & quan sát hội tụ
│   ├── StateSync.Tests/               # Bộ kiểm thử tự động xUnit & Property-Based Testing
│   │   ├── CRDTPropertiesTests.cs     # Kiểm chứng Giao hoán, Kết hợp, Lũy đẳng, Đơn điệu (200 kịch bản fuzzing)
│   │   └── LWWVsBaselineTests.cs      # Tái hiện lỗi Amazon Shopping Cart trên Baseline vs CRDT
│   └── StateSync.Benchmark/           # Công cụ chạy thực nghiệm 10,000 - 50,000 thao tác (5 Trials Mean ± Std)
│       └── Program.cs                 # Thực thi 4 thí nghiệm chuẩn SE 2026 và xuất CSV/JSON
├── results.csv                        # Bảng kết quả thực nghiệm định lượng (17 kịch bản)
├── metrics_summary.json               # Dữ liệu chi tiết các phép đo (JSON)
└── error_analysis_cases.json          # Biên bản phân tích các ca lỗi thực tế (Failure/Recovery Cases)
```

---

## 3. Hướng dẫn Chạy Tái lập Thực nghiệm (Reproducibility)

Yêu cầu môi trường: **.NET SDK 10.0+**

### 3.1. Chạy Bộ Kiểm thử Toán học & Property-Based Tests (11/11 Tests)
```bash
dotnet test src/StateSync.Tests/StateSync.Tests.csproj
```

### 3.2. Chạy Benchmark Thu thập 7 Metrics Thực nghiệm (Xuất CSV & JSON)
```bash
dotnet run --project src/StateSync.Benchmark/StateSync.Benchmark.csproj
```
*(Chương trình cố định tập hạt nhân `Seeds = [42..46]` qua 5 trials độc lập, tự động xuất `results.csv`, `metrics_summary.json` và `error_analysis_cases.json`).*

### 3.3. Chạy Ứng dụng Web ASP.NET Core SignalR Dashboard
```bash
dotnet run --project src/StateSync.Server/StateSync.Server.csproj
```

---

## 4. Kết quả Thực nghiệm Tổng hợp (`Mean ± Std` qua 5 Trials)

| Kịch bản Thí nghiệm | Clients | Tổng Ops | Hội tụ (%) | Lost Update / Anomaly ($\text{Mean} \pm \text{Std}$) | P95 Latency (ms) | Throughput ($\text{Mean} \pm \text{Std}$ Ops/s) | Tombstones | Metadata Overhead (%) | RAM (KB) | Partition Recovery |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Exp 1: CRDT (Broadcast)** | 10 | 10,000 | **100.0%** | **0.00% ± 0.00%** | 171.80 ms *(Net)* | **89,992 ± 30,851** | 500 | 93.6% *(Chưa GC)* | 66.3 KB | **100.0%** |
| **Exp 1: Baseline (Broadcast)** | 10 | 10,000 | **0.0%** | **4.29% ± 1.05%** | 171.80 ms *(Net)* | 80,862 ± 17,566 | 0 | 0.0% | 29.7 KB | **0.0%** |
| **Exp 2A: Stress 2 Clients** | 2 | 25,000 | **100.0%** | **0.00%** | 0.047 ms *(Merge)* | **2,176,575 ± 151,290** | 65 | 35.1% | 37.9 KB | **100.0%** |
| **Exp 2A: Stress 10 Clients** | 10 | 25,000 | **100.0%** | **0.00%** | 0.055 ms *(Merge)* | **1,144,968 ± 27,696** | 62 | 34.8% | 37.4 KB | **100.0%** |
| **Exp 2A: Stress 50 Clients** | 50 | **50,000** | **100.0%** | **0.00%** | 0.089 ms *(Merge)* | **389,481 ± 7,080** | 57 | 33.2% | 38.1 KB | **100.0%** |
| **Exp 2B: Low Conflict (10%) Base** | 10 | 10,000 | **0.0%** | **23.49% ± 0.89%** | 0.100 ms | 4,865,929 ± 1,868,231 | 0 | 0.0% | 168.5 KB | **0.0%** |
| **Exp 2B: Med Conflict (50%) Base** | 10 | 10,000 | **0.0%** | **26.21% ± 1.88%** | 0.100 ms | 12,787,617 ± 548,842 | 0 | 0.0% | 18.0 KB | **0.0%** |
| **Exp 2B: High Conflict (90%) Base** | 10 | 10,000 | **0.0%** | **34.54% ± 14.02%** | 0.100 ms | 16,501,683 ± 282,014 | 0 | 0.0% | 1.8 KB | **0.0%** |
| **Exp 3: Split-Brain CRDT** | 4 | 10,000 | **100.0%** | **0.00% ± 0.00%** | 0.161 ms *(Heal)* | **562,840 ± 184,866** | 200 | 119.4% | 30.0 KB | **100.0%** |
| **Exp 3: Split-Brain Baseline** | 4 | 10,000 | **0.0%** | **51.48% ± 8.42%** | 0.068 ms *(Heal)* | 648,652 ± 66,323 | 0 | 0.0% | 18.9 KB | **0.0%** |
| **Exp 4: Ablation GC OFF (50k Ops)** | 4 | **50,000** | **100.0%** | **0.00% ± 0.00%** | 23.527 ms | **2,248,407 ± 547,136** | **4,947** | **134.5%** | **709.5 KB** | **100.0%** |
| **Exp 4: Ablation GC ON (50k Ops)** | 4 | **50,000** | **100.0%** | **0.00% ± 0.00%** | 42.307 ms | **1,191,687 ± 114,262** | **109** | **21.7%** *(Đạt <30%)* | **298.2 KB** | **100.0%** |

---

## 5. Tài liệu Tham khảo Chính

1. Marc Shapiro, Nuno Preguiça, Carlos Baquero, & Marek Zawirski (2011). *Conflict-Free Replicated Data Types*. In International Symposium on Stabilization, Safety, and Security of Distributed Systems (SSS 2011 / INRIA Research Report RR-7687).
2. Marc Shapiro, Nuno Preguiça, Carlos Baquero, & Marek Zawirski (2011). *A comprehensive study of Convergent and Commutative Replicated Data Types*. INRIA Research Report RR-7506.
