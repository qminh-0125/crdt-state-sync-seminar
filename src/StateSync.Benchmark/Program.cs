using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StateSync.Core;
using StateSync.NetworkSim;

namespace StateSync.Benchmark;

class Program
{
    // Chạy lặp 5 trials độc lập cho mọi thí nghiệm với dải hạt giống tất định theo từng kịch bản:
    // Exp1: 42..46 | Exp2A: 100+clients*10+trial | Exp2B: 300..304 | Exp3: 2026..2030 | Exp4: 77..81
    private const int NumTrials = 5;
    private const int SyncBatchIntervalOps = 250; // Chu kỳ đồng bộ thống nhất (250 ops/batch) trên mọi thí nghiệm

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("==========================================================================================");
        Console.WriteLine(" StateSync Benchmark: Đánh giá Thực nghiệm CRDT chuẩn Software Engineering 2026");
        Console.WriteLine($" Cấu hình: {NumTrials} Trials/Kịch bản | Chu kỳ Sync: {SyncBatchIntervalOps} Ops | Đo trạng thái & elapsed time; ước lượng bộ nhớ");
        Console.WriteLine("==========================================================================================\n");

        var results = new List<BenchmarkResult>();
        var errorCases = new List<ErrorAnalysisCase>();

        // 1. THÍ NGHIỆM 1: Đối chứng Baseline Naive LWW vs CRDT (10 Clients, 10,000 Thao tác, CÙNG PROTOCOL)
        Console.WriteLine(">>> [THÍ NGHIỆM 1] Đối chứng Baseline Naive LWW vs CRDT LWW-Element-Set (10 Clients, 10,000 Ops)...");
        var exp1 = RunExp1_BaselineVsCRDT(clientCount: 10, totalOps: 10000, errorCases);
        results.Add(exp1.crdt);
        results.Add(exp1.baseline);

        // 2A. THÍ NGHIỆM 2A: Stress Test Tải đồng thời cao theo Số lượng Client (2 -> 50 Clients, 25,000 & 50,000 Ops)
        Console.WriteLine("\n>>> [THÍ NGHIỆM 2A] Stress Test Khả năng Chịu tải theo Số lượng Client (2 -> 50 Clients, 25k - 50k Ops)...");
        int[] clientScales = { 2, 5, 10, 20, 50 };
        foreach (var clients in clientScales)
        {
            int ops = (clients == 50) ? 50000 : 25000;
            var exp2 = RunExp2_StressTest(clients, totalOps: ops);
            results.Add(exp2);
        }

        // 2B. THÍ NGHIỆM 2B: Khảo sát theo Mức độ Tranh chấp tăng dần theo Kích thước Tập Mặt hàng (PoolSize = 500 -> 100 -> 20)
        Console.WriteLine("\n>>> [THÍ NGHIỆM 2B] Khảo sát Mức độ Tranh chấp theo Kích thước Pool (Low Pool=500 -> Med Pool=100 -> High Pool=20)...");
        var contentionLevels = new[]
        {
            (Name: "LowContention_Pool500", PoolSize: 500),
            (Name: "MedContention_Pool100", PoolSize: 100),
            (Name: "HighContention_Pool20", PoolSize: 20)
        };
        foreach (var level in contentionLevels)
        {
            var (crdtConf, baseConf) = RunExp2B_ContentionScaling(level.Name, level.PoolSize, clientCount: 10, totalOps: 10000);
            results.Add(crdtConf);
            results.Add(baseConf);
        }

        // 3. THÍ NGHIỆM 3: Tiêm lỗi Phân vùng mạng & Tự phục hồi (Split-Brain qua SimulatedNetwork, 5 Trials)
        Console.WriteLine("\n>>> [THÍ NGHIỆM 3] Tiêm lỗi Phân vùng mạng & Đánh giá Tự phục hồi (Split-Brain - 5 Trials)...");
        var (exp3Crdt, exp3Baseline) = RunExp3_NetworkPartitionRecovery(totalOps: 10000, errorCases);
        results.Add(exp3Crdt);
        results.Add(exp3Baseline);

        // 4. THÍ NGHIỆM 4: Ablation Study - Đánh giá Cơ chế Dọn rác Tombstone GC ở mốc 50,000 Thao tác (5 Trials)
        Console.WriteLine("\n>>> [THÍ NGHIỆM 4] Ablation Study: Tombstone GC (Wuu-Bernstein VectorClock - 50,000 Ops, 5 Trials)...");
        var exp4NoGC = RunExp4_AblationGC(enableGC: false, totalOps: 50000);
        var exp4WithGC = RunExp4_AblationGC(enableGC: true, totalOps: 50000);
        results.Add(exp4NoGC);
        results.Add(exp4WithGC);

        ExportResults(results, errorCases);

        Console.WriteLine("\n==========================================================================================");
        Console.WriteLine(" [HOÀN TẤT] Đã xuất results.csv, metrics_summary.json và error_analysis_cases.json");
        Console.WriteLine("==========================================================================================");
    }

    /// <summary>
    /// Thí nghiệm 1: So sánh trực tiếp CRDT và Baseline trên cùng kịch bản mạng bất đồng bộ (Jitter &amp; Reordering, Seeds = 42..46).
    /// </summary>
    static (BenchmarkResult crdt, BenchmarkResult baseline) RunExp1_BaselineVsCRDT(
        int clientCount, int totalOps, List<ErrorAnalysisCase> errorCases)
    {
        var crdtTrials = new List<TrialMeasurement>();
        var baseTrials = new List<TrialMeasurement>();
        int lastDiscrepancyItems = 0;

        for (int trial = 0; trial < NumTrials; trial++)
        {
            int seed = 42 + trial;
            var random = new Random(seed);

            int itemPoolSize = 500;
            var operations = new List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)>(totalOps);
            for (int i = 0; i < totalOps; i++)
            {
                int cIdx = random.Next(clientCount);
                string cId = $"client_{cIdx}";
                string item = $"Item_{random.Next(itemPoolSize):D3}";
                bool isAdd = random.NextDouble() > 0.35;
                operations.Add((cIdx, cId, isAdd, item, new Timestamp(i + 1, cId)));
            }

            var oracleTruth = ComputeOracleGroundTruth(operations);

            var netConfig = new NetworkFaultConfig
            {
                Seed = seed,
                MinLatencyMs = 15,
                MaxLatencyMs = 120,
                ReorderJitterMs = 80,
                DuplicationRate = 0.05
            };

            // --- 1. CHẠY TRÊN CRDT ---
            var crdtNet = new SimulatedNetwork(netConfig);
            var crdtReplicas = Enumerable.Range(0, clientCount).Select(_ => new LWWElementSet<string>()).ToList();
            for (int c = 0; c < clientCount; c++)
            {
                int receiverIdx = c;
                crdtNet.RegisterNode($"client_{c}", msg =>
                {
                    var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                    if (isAdd) crdtReplicas[receiverIdx].Add(item, ts);
                    else crdtReplicas[receiverIdx].Remove(item, ts);
                });
            }

            var crdtBatchElapsedTimes = new List<double>();
            var crdtSw = Stopwatch.StartNew();
            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                if (op.isAdd) crdtReplicas[op.clientIdx].Add(op.item, op.ts);
                else crdtReplicas[op.clientIdx].Remove(op.item, op.ts);

                for (int peer = 0; peer < clientCount; peer++)
                {
                    if (peer != op.clientIdx)
                    {
                        crdtNet.EnqueueVirtual(op.clientId, $"client_{peer}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
                    }
                }

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    double batchElapsedMs = crdtNet.FlushVirtualQueueUpTo(op.ts.Ticks);
                    if (batchElapsedMs > 0) crdtBatchElapsedTimes.Add(batchElapsedMs);
                }
            }
            double finalCrdtFlushMs = crdtNet.FlushVirtualQueue();
            if (finalCrdtFlushMs > 0) crdtBatchElapsedTimes.Add(finalCrdtFlushMs);
            crdtSw.Stop();

            var crdtMeasurement = EvaluateCrdtTrial(crdtReplicas, oracleTruth, crdtBatchElapsedTimes, crdtSw.Elapsed.TotalMilliseconds, totalOps);
            crdtTrials.Add(crdtMeasurement);

            // --- 2. CHẠY TRÊN BASELINE NAIVE LWW (CÙNG GIAO THỨC & CẤU HÌNH MẠNG) ---
            var baseNet = new SimulatedNetwork(netConfig);
            var baseReplicas = Enumerable.Range(0, clientCount).Select(_ => new NaiveLWWSet<string>()).ToList();
            for (int c = 0; c < clientCount; c++)
            {
                int receiverIdx = c;
                baseNet.RegisterNode($"client_{c}", msg =>
                {
                    var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                    if (isAdd) baseReplicas[receiverIdx].Add(item, ts);
                    else baseReplicas[receiverIdx].Remove(item, ts);

                });
            }

            var baseBatchElapsedTimes = new List<double>();
            var baseSw = Stopwatch.StartNew();
            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                if (op.isAdd) baseReplicas[op.clientIdx].Add(op.item, op.ts);
                else baseReplicas[op.clientIdx].Remove(op.item, op.ts);


                for (int peer = 0; peer < clientCount; peer++)
                {
                    if (peer != op.clientIdx)
                    {
                        baseNet.EnqueueVirtual(op.clientId, $"client_{peer}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
                    }
                }

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    double batchElapsedMs = baseNet.FlushVirtualQueueUpTo(op.ts.Ticks);
                    if (batchElapsedMs > 0) baseBatchElapsedTimes.Add(batchElapsedMs);
                }
            }
            double finalBaseFlushMs = baseNet.FlushVirtualQueue();
            if (finalBaseFlushMs > 0) baseBatchElapsedTimes.Add(finalBaseFlushMs);
            baseSw.Stop();

            var baseMeasurement = EvaluateBaselineTrial(baseReplicas, oracleTruth, baseBatchElapsedTimes, baseSw.Elapsed.TotalMilliseconds, totalOps);
            baseTrials.Add(baseMeasurement);
            lastDiscrepancyItems = (int)Math.Round(baseMeasurement.AvgDiffItems);

            if (trial == 0 && errorCases.Count < 1)
            {
                // Replay tracing outside both timing windows, with the same seed and schedule.
                var baseReceiveTrace = ReplayReceiveTrace(operations, netConfig, clientCount);
                var baseStates = baseReplicas.Select(r => r.GetActiveElements()).ToList();
                var resurrectedItems = baseStates[0].Except(oracleTruth).OrderBy(x => x).ToList();
                if (resurrectedItems.Count > 0)
                {
                    string sampleItem = resurrectedItems[0];
                    var recvEvents = baseReceiveTrace.TryGetValue(sampleItem, out var rList)
                        ? rList.TakeLast(4).ToList()
                        : operations.Where(o => o.item == sampleItem).TakeLast(3).Select(o => $"{(o.isAdd ? "ADD" : "REMOVE")}@{o.ts}").ToList();
                    string trace = string.Join(" -> ", recvEvents);
                    errorCases.Add(new ErrorAnalysisCase(
                        CaseId: "ERR_RESURRECTION_01",
                        Scenario: "Exp1_OutOfOrder_Network_Jitter",
                        AffectedItem: sampleItem,
                        OperationHistory: trace,
                        BaselineOutcome: $"SAI LỆCH (Hội tụ cặp {baseMeasurement.ConvergenceRatePct:F1}%, Khớp Oracle {baseMeasurement.OracleAgreementRatePct:F1}%, Sai lệch {baseMeasurement.StateDiscrepancyRatePct:F2}%): Tại client_0, gói tin REMOVE của '{sampleItem}' được xử lý trước nhưng gói tin ADD có timestamp nhỏ hơn lại đến muộn sau đó do Jitter 80ms, khiến Baseline không có Tombstone tự động thêm lại '{sampleItem}' vào giỏ (Resurrection Anomaly).",
                        CrdtOutcome: $"ĐÚNG ĐẮN (Hội tụ cặp {crdtMeasurement.ConvergenceRatePct:F1}%, Khớp Oracle {crdtMeasurement.OracleAgreementRatePct:F1}%, Sai lệch {crdtMeasurement.StateDiscrepancyRatePct:F2}%): CRDT giữ Tombstone trong Remove-Set với Timestamp lớn hơn nên chặn gói tin ADD cũ đến trễ; mọi Replica đều thống nhất '{sampleItem}' đã bị xóa."
                    ));
                }
            }
        }

        var crdtRes = AggregateTrials("Exp1_CRDT_LWW_Element_Set", clientCount, totalOps, crdtTrials);
        var baseRes = AggregateTrials("Exp1_Baseline_Naive_LWW", clientCount, totalOps, baseTrials);

        Console.WriteLine($"  -> [CRDT]     Hội tụ = {crdtRes.ConvergenceRate:F1}% | Sai lệch = {crdtRes.StateDiscrepancyMean:F2}% ± {crdtRes.StateDiscrepancyStd:F2}% | Mean P95 Batch Elapsed = {crdtRes.MeanTrialP95BatchSyncElapsedMs:F3} ms | Invariant = {crdtRes.InvariantPassRate:F1}% | Khớp Oracle = {crdtRes.OracleAgreementRate:F1}% | Throughput = {crdtRes.ThroughputMeanOpsPerSec:F0} ± {crdtRes.ThroughputStdOpsPerSec:F0} ops/s");
        Console.WriteLine($"  -> [Baseline] Hội tụ = {baseRes.ConvergenceRate:F1}% | Sai lệch = {baseRes.StateDiscrepancyMean:F2}% ± {baseRes.StateDiscrepancyStd:F2}% (~{lastDiscrepancyItems} items lỗi) | Mean P95 Batch Elapsed = {baseRes.MeanTrialP95BatchSyncElapsedMs:F3} ms | Invariant = {baseRes.InvariantPassRate:F1}% | Khớp Oracle = {baseRes.OracleAgreementRate:F1}% | Throughput = {baseRes.ThroughputMeanOpsPerSec:F0} ± {baseRes.ThroughputStdOpsPerSec:F0} ops/s");

        return (crdtRes, baseRes);
    }

    static Dictionary<string, List<string>> ReplayReceiveTrace(
        List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)> operations,
        NetworkFaultConfig config, int clientCount)
    {
        var trace = new Dictionary<string, List<string>>();
        void Record(string item, string entry)
        {
            if (!trace.TryGetValue(item, out var entries)) trace[item] = entries = new();
            entries.Add(entry);
        }
        var net = new SimulatedNetwork(config);
        for (int c = 0; c < clientCount; c++)
        {
            int receiver = c;
            net.RegisterNode($"client_{c}", msg =>
            {
                if (receiver != 0) return;
                var (add, item, ts) = ((bool, string, Timestamp))msg.Payload;
                Record(item, $"{(add ? "RECV_ADD" : "RECV_REMOVE")}@{ts}(deliver={msg.DeliveryTimeMs}ms)");
            });
        }
        for (int i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            if (op.clientIdx == 0) Record(op.item, $"{(op.isAdd ? "LOCAL_ADD" : "LOCAL_REMOVE")}@{op.ts}");
            for (int peer = 0; peer < clientCount; peer++)
                if (peer != op.clientIdx)
                    net.EnqueueVirtual(op.clientId, $"client_{peer}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
            if ((i + 1) % SyncBatchIntervalOps == 0) net.FlushVirtualQueueUpTo(op.ts.Ticks);
        }
        net.FlushVirtualQueue();
        return trace;
    }

    /// <summary>
    /// Thí nghiệm 2A: Stress Test khả năng mở rộng từ 2 -&gt; 50 Clients (25,000 -&gt; 50,000 thao tác)
    /// kết hợp đồng bộ VectorClock Wuu-Bernstein thực tế (Seeds = 100 + clientCount * 10 + trial).
    /// </summary>
    static BenchmarkResult RunExp2_StressTest(int clientCount, int totalOps)
    {
        var trials = new List<TrialMeasurement>();

        for (int trial = 0; trial < NumTrials; trial++)
        {
            var replicaIds = Enumerable.Range(0, clientCount).Select(i => $"c_{i}").ToArray();
            var stabilityTracker = new CausalStabilityTracker(replicaIds);
            var clientClocks = replicaIds.ToDictionary(id => id, _ => new VectorClock());

            var random = new Random(100 + clientCount * 10 + trial);
            var replicas = Enumerable.Range(0, clientCount).Select(_ => new LWWElementSet<string>()).ToList();
            var operations = new List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)>(totalOps);
            var batchMergeElapsedTimes = new List<double>();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < totalOps; i++)
            {
                int clientIdx = random.Next(clientCount);
                string clientId = replicaIds[clientIdx];
                string item = $"Prod_{random.Next(500):D3}";
                bool isAdd = random.NextDouble() > 0.35;
                long logicalTime = i + 1;
                var ts = new Timestamp(logicalTime, clientId);
                operations.Add((clientIdx, clientId, isAdd, item, ts));

                if (isAdd) replicas[clientIdx].Add(item, ts);
                else replicas[clientIdx].Remove(item, ts);

                // Cập nhật đồng hồ VectorClock cục bộ của client thực hiện thao tác
                clientClocks[clientId].AdvanceTo(clientId, logicalTime);
                stabilityTracker.ObserveVectorClock(clientId, clientClocks[clientId]);

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    var mergeSw = Stopwatch.StartNew();
                    int hops = clientCount - 1;
                    int startNode = ((i + 1) / SyncBatchIntervalOps) % clientCount;
                    for (int h = 0; h < hops; h++)
                    {
                        int srcIdx = (startNode + h) % clientCount;
                        int dstIdx = (srcIdx + 1) % clientCount;
                        string srcId = replicaIds[srcIdx];
                        string dstId = replicaIds[dstIdx];

                        replicas[dstIdx].Merge(replicas[srcIdx]);
                        clientClocks[dstId].Merge(clientClocks[srcId]);
                        stabilityTracker.ObserveVectorClock(dstId, clientClocks[dstId]);
                    }

                    int targetIdx = (startNode + hops) % clientCount;
                    replicas[targetIdx].PruneTombstones((_, t) => stabilityTracker.IsStable(t));
                    mergeSw.Stop();
                    batchMergeElapsedTimes.Add(mergeSw.Elapsed.TotalMilliseconds);
                }
            }

            // Pha Quiescence: Toàn bộ các replica hợp nhất trạng thái cuối phiên
            for (int i = 1; i < clientCount; i++)
            {
                replicas[0].Merge(replicas[i]);
                clientClocks[replicaIds[0]].Merge(clientClocks[replicaIds[i]]);
            }
            for (int i = 1; i < clientCount; i++)
            {
                replicas[i].Merge(replicas[0]);
            }
            // Dọn rác theo trạng thái ổn định đã được ghi nhận trước pha quiescence
            foreach (var r in replicas)
            {
                r.PruneTombstones((_, t) => stabilityTracker.IsStable(t));
            }
            sw.Stop();

            var oracleTruth = ComputeOracleGroundTruth(operations);
            trials.Add(EvaluateCrdtTrial(replicas, oracleTruth, batchMergeElapsedTimes, sw.Elapsed.TotalMilliseconds, totalOps));
        }

        var res = AggregateTrials($"Exp2A_Stress_{clientCount}Clients_{totalOps / 1000}kOps", clientCount, totalOps, trials);
        Console.WriteLine($"  -> Clients = {clientCount,2} | Ops = {totalOps,5} | Throughput = {res.ThroughputMeanOpsPerSec,8:F0} ± {res.ThroughputStdOpsPerSec,6:F0} ops/s | Mean P95 Batch Elapsed = {res.MeanTrialP95BatchSyncElapsedMs:F3} ms | Overhead = {res.MetadataOverheadPct:F1}% | Hội tụ = {res.ConvergenceRate:F1}%");
        return res;
    }

    /// <summary>
    /// Thí nghiệm 2B: Khảo sát độ nhạy theo Mức độ Tranh chấp tăng dần thông qua thu hẹp kích thước tập mặt hàng
    /// (LowContention_Pool500 -&gt; MedContention_Pool100 -&gt; HighContention_Pool20, Seeds = 300..304).
    /// </summary>
    static (BenchmarkResult crdt, BenchmarkResult baseline) RunExp2B_ContentionScaling(
        string levelName, int itemPoolSize, int clientCount, int totalOps)
    {
        var crdtTrials = new List<TrialMeasurement>();
        var baseTrials = new List<TrialMeasurement>();

        for (int trial = 0; trial < NumTrials; trial++)
        {
            int seed = 300 + trial;
            var rng = new Random(seed);
            var ops = new List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)>(totalOps);

            for (int i = 0; i < totalOps; i++)
            {
                int cIdx = rng.Next(clientCount);
                string cId = $"c_{cIdx}";
                string item = $"HotItem_{rng.Next(itemPoolSize):D4}";
                bool isAdd = rng.NextDouble() > 0.40;
                ops.Add((cIdx, cId, isAdd, item, new Timestamp(i + 1, cId)));
            }

            var oracleTruth = ComputeOracleGroundTruth(ops);

            var netConfig = new NetworkFaultConfig
            {
                Seed = seed,
                MinLatencyMs = 15,
                MaxLatencyMs = 120,
                ReorderJitterMs = 80,
                DuplicationRate = 0.05
            };

            // --- 1. CHẠY TRÊN CRDT ---
            var crdtNet = new SimulatedNetwork(netConfig);
            var crdtReplicas = Enumerable.Range(0, clientCount).Select(_ => new LWWElementSet<string>()).ToList();
            for (int c = 0; c < clientCount; c++)
            {
                int receiverIdx = c;
                crdtNet.RegisterNode($"c_{c}", msg =>
                {
                    var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                    if (isAdd) crdtReplicas[receiverIdx].Add(item, ts);
                    else crdtReplicas[receiverIdx].Remove(item, ts);
                });
            }

            var crdtBatchElapsedTimes = new List<double>();
            var swCrdt = Stopwatch.StartNew();
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                if (op.isAdd) crdtReplicas[op.clientIdx].Add(op.item, op.ts);
                else crdtReplicas[op.clientIdx].Remove(op.item, op.ts);

                for (int peer = 0; peer < clientCount; peer++)
                {
                    if (peer != op.clientIdx)
                    {
                        crdtNet.EnqueueVirtual(op.clientId, $"c_{peer}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
                    }
                }

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    double batchMs = crdtNet.FlushVirtualQueueUpTo(op.ts.Ticks);
                    if (batchMs > 0) crdtBatchElapsedTimes.Add(batchMs);
                }
            }
            double finalCrdtMs = crdtNet.FlushVirtualQueue();
            if (finalCrdtMs > 0) crdtBatchElapsedTimes.Add(finalCrdtMs);
            swCrdt.Stop();

            crdtTrials.Add(EvaluateCrdtTrial(crdtReplicas, oracleTruth, crdtBatchElapsedTimes, swCrdt.Elapsed.TotalMilliseconds, totalOps));

            // --- 2. CHẠY TRÊN BASELINE (CÙNG GIAO THỨC & BỘ HÀM ĐÁNH GIÁ) ---
            var baseNet = new SimulatedNetwork(netConfig);
            var baseReplicas = Enumerable.Range(0, clientCount).Select(_ => new NaiveLWWSet<string>()).ToList();
            for (int c = 0; c < clientCount; c++)
            {
                int receiverIdx = c;
                baseNet.RegisterNode($"c_{c}", msg =>
                {
                    var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                    if (isAdd) baseReplicas[receiverIdx].Add(item, ts);
                    else baseReplicas[receiverIdx].Remove(item, ts);
                });
            }

            var baseBatchElapsedTimes = new List<double>();
            var swBase = Stopwatch.StartNew();
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                if (op.isAdd) baseReplicas[op.clientIdx].Add(op.item, op.ts);
                else baseReplicas[op.clientIdx].Remove(op.item, op.ts);

                for (int peer = 0; peer < clientCount; peer++)
                {
                    if (peer != op.clientIdx)
                    {
                        baseNet.EnqueueVirtual(op.clientId, $"c_{peer}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
                    }
                }

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    double batchMs = baseNet.FlushVirtualQueueUpTo(op.ts.Ticks);
                    if (batchMs > 0) baseBatchElapsedTimes.Add(batchMs);
                }
            }
            double finalBaseMs = baseNet.FlushVirtualQueue();
            if (finalBaseMs > 0) baseBatchElapsedTimes.Add(finalBaseMs);
            swBase.Stop();

            baseTrials.Add(EvaluateBaselineTrial(baseReplicas, oracleTruth, baseBatchElapsedTimes, swBase.Elapsed.TotalMilliseconds, totalOps));
        }

        var crdtRes = AggregateTrials($"Exp2B_{levelName}_CRDT", clientCount, totalOps, crdtTrials);
        var baseRes = AggregateTrials($"Exp2B_{levelName}_Baseline", clientCount, totalOps, baseTrials);

        Console.WriteLine($"  -> [{levelName,-21}] CRDT Hội tụ = {crdtRes.ConvergenceRate,5:F1}%, Sai lệch = {crdtRes.StateDiscrepancyMean:F2}% (Mean P95 Elapsed = {crdtRes.MeanTrialP95BatchSyncElapsedMs:F3} ms) | Baseline Hội tụ = {baseRes.ConvergenceRate,4:F1}%, Sai lệch = {baseRes.StateDiscrepancyMean,5:F2}% ± {baseRes.StateDiscrepancyStd:F2}% (Mean P95 Elapsed = {baseRes.MeanTrialP95BatchSyncElapsedMs:F3} ms)");

        return (crdtRes, baseRes);
    }

    /// <summary>
    /// Thí nghiệm 3: Tiêm lỗi Chia cắt Phân vùng mạng (Split-Brain Partition, Seeds = 2026..2030) và đánh giá khả năng Tự phục hồi.
    /// Cả CRDT và Baseline đều chạy ĐÚNG CÙNG giao thức đồng bộ trước và sau khi nối mạng.
    /// </summary>
    static (BenchmarkResult crdt, BenchmarkResult baseline) RunExp3_NetworkPartitionRecovery(
        int totalOps, List<ErrorAnalysisCase> errorCases)
    {
        int clientCount = 4;
        var crdtTrials = new List<TrialMeasurement>();
        var baseTrials = new List<TrialMeasurement>();
        long lastDroppedPackets = 0;

        for (int trial = 0; trial < NumTrials; trial++)
        {
            int seed = 2026 + trial;
            var rng = new Random(seed);
            var operations = new List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)>(totalOps);

            for (int i = 0; i < totalOps; i++)
            {
                int senderIdx = i % clientCount;
                string senderId = $"node_{senderIdx}";
                string item = $"SharedCartItem_{rng.Next(200):D3}";
                bool isAdd = rng.NextDouble() > 0.35;
                var ts = new Timestamp(i + 1, senderId);
                operations.Add((senderIdx, senderId, isAdd, item, ts));
            }

            var oracleTruth = ComputeOracleGroundTruth(operations);

            // --- 1. CHẠY TRÊN CRDT ---
            var crdtNet = new SimulatedNetwork(new NetworkFaultConfig
            {
                Seed = seed,
                MinLatencyMs = 10,
                MaxLatencyMs = 50,
                ReorderJitterMs = 20
            });
            crdtNet.Partition(new[] { "node_0", "node_1" }, new[] { "node_2", "node_3" });
            var crdtReplicas = Enumerable.Range(0, clientCount).Select(_ => new LWWElementSet<string>()).ToList();

            for (int c = 0; c < clientCount; c++)
            {
                int idx = c;
                crdtNet.RegisterNode($"node_{c}", msg =>
                {
                    var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                    if (isAdd) crdtReplicas[idx].Add(item, ts);
                    else crdtReplicas[idx].Remove(item, ts);
                });
            }

            var crdtStepElapsedTimes = new List<double>();
            var swCrdt = Stopwatch.StartNew();
            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                if (op.isAdd) crdtReplicas[op.clientIdx].Add(op.item, op.ts);
                else crdtReplicas[op.clientIdx].Remove(op.item, op.ts);

                for (int r = 0; r < clientCount; r++)
                {
                    if (r != op.clientIdx)
                    {
                        crdtNet.EnqueueVirtual(op.clientId, $"node_{r}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
                    }
                }

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    double batchMs = crdtNet.FlushVirtualQueueUpTo(op.ts.Ticks);
                    if (batchMs > 0) crdtStepElapsedTimes.Add(batchMs);
                }
            }
            double preHealCrdtMs = crdtNet.FlushVirtualQueue();
            if (preHealCrdtMs > 0) crdtStepElapsedTimes.Add(preHealCrdtMs);
            lastDroppedPackets = crdtNet.TotalDropped;

            crdtNet.HealPartition();
            var healCrdtSw = Stopwatch.StartNew();
            for (int i = 1; i < clientCount; i++) crdtReplicas[0].Merge(crdtReplicas[i]);
            for (int i = 1; i < clientCount; i++) crdtReplicas[i].Merge(crdtReplicas[0]);
            healCrdtSw.Stop();
            crdtStepElapsedTimes.Add(healCrdtSw.Elapsed.TotalMilliseconds);
            swCrdt.Stop();

            var crdtMeasurement = EvaluateCrdtTrial(crdtReplicas, oracleTruth, crdtStepElapsedTimes, swCrdt.Elapsed.TotalMilliseconds, totalOps);
            crdtTrials.Add(crdtMeasurement);

            // --- 2. CHẠY TRÊN BASELINE (CÙNG GIAO THỨC GATHER/BROADCAST MERGE SAU KHI NỐI MẠNG) ---
            var baseNet = new SimulatedNetwork(new NetworkFaultConfig
            {
                Seed = seed,
                MinLatencyMs = 10,
                MaxLatencyMs = 50,
                ReorderJitterMs = 20
            });
            baseNet.Partition(new[] { "node_0", "node_1" }, new[] { "node_2", "node_3" });
            var baseReplicas = Enumerable.Range(0, clientCount).Select(_ => new NaiveLWWSet<string>()).ToList();

            for (int c = 0; c < clientCount; c++)
            {
                int idx = c;
                baseNet.RegisterNode($"node_{c}", msg =>
                {
                    var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                    if (isAdd) baseReplicas[idx].Add(item, ts);
                    else baseReplicas[idx].Remove(item, ts);
                });
            }

            var baseStepElapsedTimes = new List<double>();
            var swBase = Stopwatch.StartNew();
            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                if (op.isAdd) baseReplicas[op.clientIdx].Add(op.item, op.ts);
                else baseReplicas[op.clientIdx].Remove(op.item, op.ts);

                for (int r = 0; r < clientCount; r++)
                {
                    if (r != op.clientIdx)
                    {
                        baseNet.EnqueueVirtual(op.clientId, $"node_{r}", (op.isAdd, op.item, op.ts), op.ts.Ticks);
                    }
                }

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    double batchMs = baseNet.FlushVirtualQueueUpTo(op.ts.Ticks);
                    if (batchMs > 0) baseStepElapsedTimes.Add(batchMs);
                }
            }
            double preHealBaseMs = baseNet.FlushVirtualQueue();
            if (preHealBaseMs > 0) baseStepElapsedTimes.Add(preHealBaseMs);

            // Chụp trạng thái trước khi nối mạng để truy vết chính xác phân vùng nào đã xóa và phân vùng nào còn giữ bản sao cũ
            var preHealGroup1Active = baseReplicas[0].GetActiveElements();
            var preHealGroup2Active = baseReplicas[2].GetActiveElements();

            baseNet.HealPartition();
            var healBaseSw = Stopwatch.StartNew();
            for (int i = 1; i < clientCount; i++) baseReplicas[0].Merge(baseReplicas[i]);
            for (int i = 1; i < clientCount; i++) baseReplicas[i].Merge(baseReplicas[0]);
            healBaseSw.Stop();
            baseStepElapsedTimes.Add(healBaseSw.Elapsed.TotalMilliseconds);
            swBase.Stop();

            var baseMeasurement = EvaluateBaselineTrial(baseReplicas, oracleTruth, baseStepElapsedTimes, swBase.Elapsed.TotalMilliseconds, totalOps);
            baseTrials.Add(baseMeasurement);

            var resurrected = baseReplicas[0].GetActiveElements().Except(oracleTruth).OrderBy(x => x).ToList();
            if (trial == 0 && resurrected.Count > 0)
            {
                string sampleItem = resurrected[0];
                var itemOps = operations.Where(o => o.item == sampleItem).ToList();
                var lastRemove = itemOps.Last(o => !o.isAdd);
                var lastAdd = itemOps.Last(o => o.isAdd);

                string deletingPartition = lastRemove.clientIdx <= 1
                    ? "Partition Group 1 [node_0, node_1]"
                    : "Partition Group 2 [node_2, node_3]";
                string staleKeepingPartition = preHealGroup1Active.Contains(sampleItem)
                    ? "Partition Group 1 [node_0, node_1]"
                    : "Partition Group 2 [node_2, node_3]";

                errorCases.Add(new ErrorAnalysisCase(
                    CaseId: "ERR_SPLIT_BRAIN_02",
                    Scenario: "Exp3_NetworkPartition_SplitBrain_Heal",
                    AffectedItem: sampleItem,
                    OperationHistory: $"ADD@{lastAdd.ts} -> REMOVE@{lastRemove.ts} ({deletingPartition} xóa || {staleKeepingPartition} còn giữ bản sao cũ trước khi nối mạng)",
                    BaselineOutcome: $"HỘI TỤ VỀ TRẠNG THÁI SAI (Hội tụ cặp {baseMeasurement.ConvergenceRatePct:F1}%, Khớp Oracle {baseMeasurement.OracleAgreementRatePct:F1}%, Sai lệch {baseMeasurement.StateDiscrepancyRatePct:F2}%): Trong lúc đứt mạng ({lastDroppedPackets} gói tin xuyên phân vùng bị chặn), {deletingPartition} đã thực hiện REMOVE@{lastRemove.ts}. Khi nối lại mạng và chạy Gather/Broadcast Naive Merge, vì không có Tombstone nên '{sampleItem}' từ {staleKeepingPartition} bị copy ngược lại sang toàn bộ 4 node.",
                    CrdtOutcome: $"TỰ PHỤC HỒI ĐÚNG ĐẮN (Hội tụ cặp {crdtMeasurement.ConvergenceRatePct:F1}%, Khớp Oracle {crdtMeasurement.OracleAgreementRatePct:F1}%, Sai lệch {crdtMeasurement.StateDiscrepancyRatePct:F2}%): Khi nối lại mạng, hàm Merge của CRDT hợp nhất cả Add-Set và Remove-Set (LUB). Tombstone REMOVE@{lastRemove.ts} từ {deletingPartition} giúp toàn bộ 4 node thống nhất '{sampleItem}' đã bị xóa."
                ));
            }
        }

        var crdtRes = AggregateTrials("Exp3_Partition_CRDT", clientCount, totalOps, crdtTrials);
        var baseRes = AggregateTrials("Exp3_Partition_Baseline", clientCount, totalOps, baseTrials);

        Console.WriteLine($"  -> [CRDT (LWW-Element-Set)] Dropped = {lastDroppedPackets} pkts | Mean P95 Batch Elapsed = {crdtRes.MeanTrialP95BatchSyncElapsedMs:F3} ms | Hội tụ = {crdtRes.ConvergenceRate:F1}% | Sai lệch = {crdtRes.StateDiscrepancyMean:F2}% ± {crdtRes.StateDiscrepancyStd:F2}% | Invariant = {crdtRes.InvariantPassRate:F1}% | Khớp Oracle = {crdtRes.OracleAgreementRate:F1}%");
        Console.WriteLine($"  -> [Baseline (Naive LWW)  ] Dropped = {lastDroppedPackets} pkts | Mean P95 Batch Elapsed = {baseRes.MeanTrialP95BatchSyncElapsedMs:F3} ms | Hội tụ = {baseRes.ConvergenceRate:F1}% | Sai lệch = {baseRes.StateDiscrepancyMean:F2}% ± {baseRes.StateDiscrepancyStd:F2}% | Invariant = {baseRes.InvariantPassRate:F1}% | Khớp Oracle = {baseRes.OracleAgreementRate:F1}%");

        return (crdtRes, baseRes);
    }

    /// <summary>
    /// Thí nghiệm 4: Ablation Study đối chứng TẮT vs BẬT cơ chế dọn rác Wuu-Bernstein VectorClock (50,000 Ops, Seeds = 77..81).
    /// Sử dụng ma trận VectorClock 2 chiều thật giữa 4 replicas (không dùng hằng số trừ lùi).
    /// </summary>
    static BenchmarkResult RunExp4_AblationGC(bool enableGC, int totalOps)
    {
        int clientCount = 4;
        var trials = new List<TrialMeasurement>();
        double lastMetadataKb = 0;
        double lastPayloadKb = 0;

        for (int trial = 0; trial < NumTrials; trial++)
        {
            var replicaIds = Enumerable.Range(0, clientCount).Select(i => $"client_{i}").ToArray();
            var stabilityTracker = new CausalStabilityTracker(replicaIds);
            var clientClocks = replicaIds.ToDictionary(id => id, _ => new VectorClock());
            var replicas = Enumerable.Range(0, clientCount).Select(_ => new LWWElementSet<string>()).ToList();
            var operations = new List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)>(totalOps);
            var stepElapsedTimes = new List<double>();

            var random = new Random(77 + trial);
            var sw = Stopwatch.StartNew();
            int itemCatalogSize = 5000;

            for (int i = 0; i < totalOps; i++)
            {
                int cIdx = i % clientCount;
                string clientId = replicaIds[cIdx];
                int windowStart = (i / 100) * 20;
                int itemId = (windowStart + random.Next(250)) % itemCatalogSize;
                string item = $"CartProduct_{itemId:D4}";

                long logicalTime = i + 1;
                bool isAdd = random.NextDouble() > 0.45;
                var ts = new Timestamp(logicalTime, clientId);
                operations.Add((cIdx, clientId, isAdd, item, ts));

                if (isAdd) replicas[cIdx].Add(item, ts);
                else replicas[cIdx].Remove(item, ts);

                // Cập nhật tiến độ thực của clientId trên VectorClock cục bộ
                clientClocks[clientId].AdvanceTo(clientId, logicalTime);
                stabilityTracker.ObserveVectorClock(clientId, clientClocks[clientId]);

                if ((i + 1) % SyncBatchIntervalOps == 0)
                {
                    var maintSw = Stopwatch.StartNew();
                    int baseNode = ((i + 1) / SyncBatchIntervalOps) % clientCount;
                    for (int h = 0; h < clientCount; h++)
                    {
                        int sender = (baseNode + h) % clientCount;
                        int receiver = (sender + 1) % clientCount;

                        // Đồng bộ theo vòng (Ring Gossip): dữ liệu và VectorClock lan truyền qua từng chặng
                        replicas[receiver].Merge(replicas[sender]);
                        clientClocks[replicaIds[receiver]].Merge(clientClocks[replicaIds[sender]]);
                        stabilityTracker.ObserveVectorClock(replicaIds[receiver], clientClocks[replicaIds[receiver]]);

                        if (enableGC)
                        {
                            // Chỉ những Tombstone đã lan truyền qua đủ 4 replica trong ma trận Wuu-Bernstein mới được tỉa
                            replicas[receiver].PruneTombstones((_, t) => stabilityTracker.IsStable(t));
                        }
                    }
                    maintSw.Stop();
                    stepElapsedTimes.Add(maintSw.Elapsed.TotalMilliseconds);
                }
            }

            // Pha Quiescence cuối phiên: Hợp nhất toàn bộ các thao tác còn đang trên đường truyền
            for (int r = 1; r < clientCount; r++) replicas[0].Merge(replicas[r]);
            for (int r = 1; r < clientCount; r++) replicas[r].Merge(replicas[0]);
            if (enableGC)
            {
                foreach (var r in replicas)
                {
                    r.PruneTombstones((_, t) => stabilityTracker.IsStable(t));
                }
            }
            sw.Stop();

            var oracleTruth = ComputeOracleGroundTruth(operations);
            trials.Add(EvaluateCrdtTrial(replicas, oracleTruth, stepElapsedTimes, sw.Elapsed.TotalMilliseconds, totalOps));

            var (payloadBytes, metadataBytes, _, _) = replicas[0].CalculateOverhead();
            lastMetadataKb = metadataBytes / 1024.0;
            lastPayloadKb = payloadBytes / 1024.0;
        }

        var res = AggregateTrials($"Exp4_Ablation_GC_{(enableGC ? "ON" : "OFF")}_50kOps", clientCount, totalOps, trials);
        Console.WriteLine($"  -> Ablation GC = {(enableGC ? "BẬT (With GC)" : "TẮT (No GC)"),-15} | Tombstones = {res.TombstoneCount,5} | Overhead = {res.MetadataOverheadPct,5:F1}% (Meta {lastMetadataKb:F1} KB / Payload {lastPayloadKb:F1} KB | RAM {res.EstimatedRamKb:F1} KB) | Mean P95 Batch Elapsed = {res.MeanTrialP95BatchSyncElapsedMs:F3} ms | Sai lệch = {res.StateDiscrepancyMean:F2}% | Throughput = {res.ThroughputMeanOpsPerSec:F0} ± {res.ThroughputStdOpsPerSec:F0} ops/s");
        return res;
    }

    /// <summary>
    /// Hàm đánh giá chuẩn hóa duy nhất cho mọi kịch bản chạy trên CRDT.
    /// </summary>
    static TrialMeasurement EvaluateCrdtTrial(
        IReadOnlyList<LWWElementSet<string>> replicas,
        HashSet<string> oracleTruth,
        List<double> syncBatchElapsedTimesMs,
        double elapsedTotalMs,
        int totalOps)
    {
        var states = replicas.Select(r => r.GetActiveElements()).ToList();
        var (discrepancyPct, avgDiffItems) = MeasureDiscrepancyRate(states, oracleTruth);
        var overheads = replicas.Select(r => r.CalculateOverhead()).ToList();

        return new TrialMeasurement(
            ConvergenceRatePct: MeasurePairwiseConvergenceRate(states),
            StateDiscrepancyRatePct: discrepancyPct,
            AvgDiffItems: avgDiffItems,
            TrialP95BatchSyncElapsedMs: ComputeP95(syncBatchElapsedTimesMs),
            ThroughputOpsPerSec: totalOps * 1000.0 / Math.Max(0.1, elapsedTotalMs),
            TombstoneCount: replicas.Average(r => r.TombstoneCount),
            MetadataOverheadPct: overheads.Average(o => o.OverheadPercentage),
            EstimatedRamKb: overheads.Average(o => o.EstimatedRamBytes / 1024.0),
            InvariantPassRatePct: MeasureCrdtInvariantsPassRate(replicas, oracleTruth),
            OracleAgreementRatePct: MeasureOracleAgreementRate(states, oracleTruth)
        );
    }

    /// <summary>
    /// Hàm đánh giá chuẩn hóa duy nhất cho mọi kịch bản chạy trên Baseline (dùng đúng cùng công thức với CRDT).
    /// </summary>
    static TrialMeasurement EvaluateBaselineTrial(
        IReadOnlyList<NaiveLWWSet<string>> replicas,
        HashSet<string> oracleTruth,
        List<double> syncBatchElapsedTimesMs,
        double elapsedTotalMs,
        int totalOps)
    {
        var states = replicas.Select(r => r.GetActiveElements()).ToList();
        var (discrepancyPct, avgDiffItems) = MeasureDiscrepancyRate(states, oracleTruth);
        var overheads = replicas.Select(r => r.CalculateOverhead()).ToList();

        return new TrialMeasurement(
            ConvergenceRatePct: MeasurePairwiseConvergenceRate(states),
            StateDiscrepancyRatePct: discrepancyPct,
            AvgDiffItems: avgDiffItems,
            TrialP95BatchSyncElapsedMs: ComputeP95(syncBatchElapsedTimesMs),
            ThroughputOpsPerSec: totalOps * 1000.0 / Math.Max(0.1, elapsedTotalMs),
            TombstoneCount: replicas.Average(r => r.TombstoneCount),
            MetadataOverheadPct: overheads.Average(o => o.OverheadPercentage),
            EstimatedRamKb: overheads.Average(o => o.EstimatedRamBytes / 1024.0),
            InvariantPassRatePct: MeasureBaselineInvariantsPassRate(replicas, oracleTruth),
            OracleAgreementRatePct: MeasureOracleAgreementRate(states, oracleTruth)
        );
    }

    static BenchmarkResult AggregateTrials(
        string experimentName,
        int clients,
        int totalOps,
        List<TrialMeasurement> trials)
    {
        var (discMean, discStd) = CalcMeanStd(trials.Select(t => t.StateDiscrepancyRatePct).ToList());
        var (tputMean, tputStd) = CalcMeanStd(trials.Select(t => t.ThroughputOpsPerSec).ToList());

        return new BenchmarkResult(
            ExperimentName: experimentName,
            Clients: clients,
            TotalOperations: totalOps,
            ConvergenceRate: trials.Average(t => t.ConvergenceRatePct),
            StateDiscrepancyMean: discMean,
            StateDiscrepancyStd: discStd,
            MeanTrialP95BatchSyncElapsedMs: trials.Average(t => t.TrialP95BatchSyncElapsedMs),
            ThroughputMeanOpsPerSec: tputMean,
            ThroughputStdOpsPerSec: tputStd,
            TombstoneCount: (int)Math.Round(trials.Average(t => t.TombstoneCount)),
            MetadataOverheadPct: trials.Average(t => t.MetadataOverheadPct),
            EstimatedRamKb: trials.Average(t => t.EstimatedRamKb),
            InvariantPassRate: trials.Average(t => t.InvariantPassRatePct),
            OracleAgreementRate: trials.Average(t => t.OracleAgreementRatePct)
        );
    }

    /// <summary>
    /// Tính toán Ground Truth độc lập hoàn toàn từ nhật ký thao tác (Oracle):
    /// Với mỗi mặt hàng, tìm timestamp lớn nhất của thao tác ADD và thao tác REMOVE.
    /// Mặt hàng tồn tại khi và chỉ khi maxAddTs &gt; maxRemoveTs.
    /// </summary>
    static HashSet<string> ComputeOracleGroundTruth(
        List<(int clientIdx, string clientId, bool isAdd, string item, Timestamp ts)> operations)
    {
        var maxAdd = new Dictionary<string, Timestamp>();
        var maxRem = new Dictionary<string, Timestamp>();

        foreach (var op in operations)
        {
            if (op.isAdd)
            {
                if (!maxAdd.TryGetValue(op.item, out var cur) || op.ts > cur)
                    maxAdd[op.item] = op.ts;
            }
            else
            {
                if (!maxRem.TryGetValue(op.item, out var cur) || op.ts > cur)
                    maxRem[op.item] = op.ts;
            }
        }

        var active = new HashSet<string>();
        foreach (var (item, addTs) in maxAdd)
        {
            if (!maxRem.TryGetValue(item, out var remTs) || addTs > remTs)
            {
                active.Add(item);
            }
        }
        return active;
    }

    /// <summary>
    /// Đo tỷ lệ các cặp Replica có trạng thái trùng khớp nhau từng đôi một (Pairwise Convergence Rate %).
    /// </summary>
    static double MeasurePairwiseConvergenceRate(IReadOnlyList<HashSet<string>> states)
    {
        if (states.Count <= 1) return 100.0;
        int totalPairs = 0;
        int equalPairs = 0;
        for (int i = 0; i < states.Count; i++)
        {
            for (int j = i + 1; j < states.Count; j++)
            {
                totalPairs++;
                if (states[i].SetEquals(states[j]))
                    equalPairs++;
            }
        }
        return (double)equalPairs / totalPairs * 100.0;
    }

    /// <summary>
    /// Đo tỷ lệ các Replica có tập trạng thái cuối khớp tuyệt đối 100% với Ground Truth Oracle (Oracle Agreement Rate %).
    /// </summary>
    static double MeasureOracleAgreementRate(IReadOnlyList<HashSet<string>> states, HashSet<string> oracleTruth)
    {
        if (states.Count == 0) return 0.0;
        int matchingReplicas = states.Count(s => s.SetEquals(oracleTruth));
        return (double)matchingReplicas / states.Count * 100.0;
    }

    /// <summary>
    /// Đo tỷ lệ sai lệch tập trạng thái cuối (State Discrepancy Rate %: gồm cả phần tử thiếu / Lost Updates
    /// và phần tử thừa / Resurrected Items) trung bình trên các Replica so với Oracle Ground Truth.
    /// </summary>
    static (double RatePct, double AvgDiffItems) MeasureDiscrepancyRate(
        IReadOnlyList<HashSet<string>> states, HashSet<string> oracleTruth)
    {
        if (states.Count == 0) return (0.0, 0.0);
        double totalDiff = 0;
        double totalPct = 0;
        foreach (var s in states)
        {
            int missing = oracleTruth.Except(s).Count();
            int resurrected = s.Except(oracleTruth).Count();
            int diff = missing + resurrected;
            int unionCount = s.Union(oracleTruth).Count();
            totalDiff += diff;
            totalPct += unionCount > 0 ? (double)diff / unionCount * 100.0 : 0.0;
        }
        double avgDiff = totalDiff / states.Count;
        double ratePct = totalPct / states.Count;
        return (ratePct, avgDiff);
    }

    /// <summary>
    /// Kiểm tra thực nghiệm 4 bất biến toán học (Idempotence, Commutativity, Associativity, Oracle Consistency)
    /// trực tiếp trên các replica CRDT sau mỗi trial.
    /// </summary>
    static double MeasureCrdtInvariantsPassRate(IReadOnlyList<LWWElementSet<string>> replicas, HashSet<string> oracleTruth)
    {
        int passed = 0;
        int total = 4;

        var r0 = replicas[0];
        var r1 = replicas[Math.Min(1, replicas.Count - 1)];
        var rLast = replicas[replicas.Count - 1];

        // 1. Idempotence: r0 ⊔ r0 == r0
        var idem = r0.Clone();
        idem.Merge(r0);
        if (idem.SemilatticeEquals(r0)) passed++;

        // 2. Commutativity: r0 ⊔ r1 == r1 ⊔ r0
        var m01 = r0.Clone();
        m01.Merge(r1);
        var m10 = r1.Clone();
        m10.Merge(r0);
        if (m01.SemilatticeEquals(m10)) passed++;

        // 3. Associativity: (r0 ⊔ r1) ⊔ rLast == r0 ⊔ (r1 ⊔ rLast)
        var left = m01.Clone();
        left.Merge(rLast);
        var rightTemp = r1.Clone();
        rightTemp.Merge(rLast);
        var right = r0.Clone();
        right.Merge(rightTemp);
        if (left.SemilatticeEquals(right)) passed++;

        // 4. Causal Tombstone Consistency (No Resurrection vs Oracle)
        if (replicas.All(r => r.GetActiveElements().SetEquals(oracleTruth))) passed++;

        return (double)passed / total * 100.0;
    }

    /// <summary>
    /// Kiểm tra thực nghiệm cùng 4 bất biến toán học đó trực tiếp trên các replica Baseline sau mỗi trial.
    /// </summary>
    static double MeasureBaselineInvariantsPassRate(IReadOnlyList<NaiveLWWSet<string>> replicas, HashSet<string> oracleTruth)
    {
        int passed = 0;
        int total = 4;

        var r0 = replicas[0];
        var r1 = replicas[Math.Min(1, replicas.Count - 1)];
        var rLast = replicas[replicas.Count - 1];

        // 1. Idempotence của phép Merge hiện tại
        var idem = r0.Clone();
        idem.Merge(r0);
        if (idem.GetActiveElements().SetEquals(r0.GetActiveElements())) passed++;

        // 2. Commutativity của phép Merge hiện tại
        var m01 = r0.Clone();
        m01.Merge(r1);
        var m10 = r1.Clone();
        m10.Merge(r0);
        if (m01.GetActiveElements().SetEquals(m10.GetActiveElements())) passed++;

        // 3. Associativity của phép Merge hiện tại
        var left = m01.Clone();
        left.Merge(rLast);
        var rightTemp = r1.Clone();
        rightTemp.Merge(rLast);
        var right = r0.Clone();
        right.Merge(rightTemp);
        if (left.GetActiveElements().SetEquals(right.GetActiveElements())) passed++;

        // 4. Causal Tombstone Consistency (No Resurrection vs Oracle)
        if (replicas.All(r => r.GetActiveElements().SetEquals(oracleTruth))) passed++;

        return (double)passed / total * 100.0;
    }

    static double ComputeP95(List<double> samples)
    {
        if (samples.Count == 0) return 0.0;
        var sorted = samples.OrderBy(x => x).ToList();
        int idx = Math.Clamp((int)Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    static (double Mean, double Std) CalcMeanStd(List<double> values)
    {
        if (values.Count == 0) return (0, 0);
        double mean = values.Average();
        if (values.Count == 1) return (mean, 0);
        double sumSq = values.Sum(v => (v - mean) * (v - mean));
        double std = Math.Sqrt(sumSq / (values.Count - 1));
        return (mean, std);
    }

    static void ExportResults(List<BenchmarkResult> results, List<ErrorAnalysisCase> errorCases)
    {
        string csvPath = "results.csv";
        using var writer = new StreamWriter(csvPath);
        writer.WriteLine("ExperimentName,Clients,TotalOperations,ConvergenceRate(%),StateDiscrepancy_Mean(%),StateDiscrepancy_Std(%),MeanTrialP95BatchSyncElapsed(ms),Throughput_Mean(Ops/s),Throughput_Std(Ops/s),TombstoneCount,MetadataOverhead(%),EstimatedRAM(KB),InvariantPass(%),OracleAgreement(%)");
        foreach (var r in results)
        {
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3:F1},{4:F2},{5:F2},{6:F3},{7:F0},{8:F0},{9},{10:F1},{11:F1},{12:F1},{13:F1}",
                r.ExperimentName,
                r.Clients,
                r.TotalOperations,
                r.ConvergenceRate,
                r.StateDiscrepancyMean,
                r.StateDiscrepancyStd,
                r.MeanTrialP95BatchSyncElapsedMs,
                r.ThroughputMeanOpsPerSec,
                r.ThroughputStdOpsPerSec,
                r.TombstoneCount,
                r.MetadataOverheadPct,
                r.EstimatedRamKb,
                r.InvariantPassRate,
                r.OracleAgreementRate
            ));
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText("metrics_summary.json", JsonSerializer.Serialize(results, jsonOptions));
        File.WriteAllText("error_analysis_cases.json", JsonSerializer.Serialize(errorCases, jsonOptions));
    }
}

public record TrialMeasurement(
    double ConvergenceRatePct,
    double StateDiscrepancyRatePct,
    double AvgDiffItems,
    double TrialP95BatchSyncElapsedMs,
    double ThroughputOpsPerSec,
    double TombstoneCount,
    double MetadataOverheadPct,
    double EstimatedRamKb,
    double InvariantPassRatePct,
    double OracleAgreementRatePct
);

public record BenchmarkResult(
    string ExperimentName,
    int Clients,
    int TotalOperations,
    double ConvergenceRate,
    double StateDiscrepancyMean,
    double StateDiscrepancyStd,
    double MeanTrialP95BatchSyncElapsedMs,
    double ThroughputMeanOpsPerSec,
    double ThroughputStdOpsPerSec,
    int TombstoneCount,
    double MetadataOverheadPct,
    double EstimatedRamKb,
    double InvariantPassRate,
    double OracleAgreementRate
);

public record ErrorAnalysisCase(
    string CaseId,
    string Scenario,
    string AffectedItem,
    string OperationHistory,
    string BaselineOutcome,
    string CrdtOutcome
);
