using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StateSync.Core;
using StateSync.NetworkSim;
using Xunit;

namespace StateSync.Tests;

/// <summary>
/// Kiểm chứng các thuộc tính toán học của bán dàn (Join-semilattice), Bất biến (Invariants)
/// và các bài kiểm thử hồi quy cho BUG-01, BUG-02, LIMIT-04, BUG-05
/// theo định nghĩa của Marc Shapiro et al. (INRIA RR-7687 &amp; RR-7506).
/// </summary>
public class CRDTPropertiesTests
{
    [Fact]
    public void LWWElementSet_Is_Commutative()
    {
        var replica1 = new LWWElementSet<string>();
        var replica2 = new LWWElementSet<string>();

        replica1.Add("Book_CSharp", new Timestamp(100, "client_1"));
        replica1.Add("Book_Rust", new Timestamp(150, "client_1"));

        replica2.Add("Book_Golang", new Timestamp(120, "client_2"));
        replica2.Remove("Book_CSharp", new Timestamp(200, "client_2"));

        var merge12 = replica1.Clone();
        merge12.Merge(replica2);

        var merge21 = replica2.Clone();
        merge21.Merge(replica1);

        Assert.True(merge12.SemilatticeEquals(merge21));
        Assert.Equal(merge12.GetActiveElements().OrderBy(x => x), merge21.GetActiveElements().OrderBy(x => x));
        Assert.False(merge12.Lookup("Book_CSharp"));
        Assert.True(merge12.Lookup("Book_Rust"));
        Assert.True(merge12.Lookup("Book_Golang"));
    }

    [Fact]
    public void LWWElementSet_Is_Associative()
    {
        var r1 = new LWWElementSet<string>();
        var r2 = new LWWElementSet<string>();
        var r3 = new LWWElementSet<string>();

        r1.Add("A", new Timestamp(10, "c1"));
        r2.Add("B", new Timestamp(20, "c2"));
        r3.Add("C", new Timestamp(30, "c3"));

        var left = r1.Clone();
        left.Merge(r2);
        left.Merge(r3);

        var right = r2.Clone();
        right.Merge(r3);
        var rightTotal = r1.Clone();
        rightTotal.Merge(right);

        Assert.True(left.SemilatticeEquals(rightTotal));
        Assert.Equal(left.GetActiveElements().OrderBy(x => x), rightTotal.GetActiveElements().OrderBy(x => x));
    }

    [Fact]
    public void LWWElementSet_Is_Idempotent()
    {
        var r1 = new LWWElementSet<string>();
        r1.Add("A", new Timestamp(10, "c1"));
        r1.Remove("A", new Timestamp(20, "c1"));
        r1.Add("B", new Timestamp(15, "c1"));

        var before = r1.Clone();

        r1.Merge(r1);
        r1.Merge(r1);
        r1.Merge(r1);

        Assert.True(before.SemilatticeEquals(r1));
        Assert.Equal(before.GetActiveElements().OrderBy(x => x), r1.GetActiveElements().OrderBy(x => x));
    }

    [Fact]
    public void PropertyBased_Semilattice_Laws_And_Monotonicity_Hold_Across_200_Random_Workloads()
    {
        var rng = new Random(2026);

        for (int trial = 0; trial < 200; trial++)
        {
            var r1 = GenerateRandomReplica(rng, "c1", 50);
            var r2 = GenerateRandomReplica(rng, "c2", 50);
            var r3 = GenerateRandomReplica(rng, "c3", 50);

            // 1. Property: Idempotence (r1 ⊔ r1 ≡ r1)
            var idem = r1.Clone();
            idem.Merge(r1);
            Assert.True(r1.SemilatticeEquals(idem));
            Assert.True(r1.GetActiveElements().SetEquals(idem.GetActiveElements()));

            // 2. Property: Commutativity (r1 ⊔ r2 ≡ r2 ⊔ r1)
            var m12 = r1.Clone();
            m12.Merge(r2);
            var m21 = r2.Clone();
            m21.Merge(r1);
            Assert.True(m12.SemilatticeEquals(m21));
            Assert.True(m12.GetActiveElements().SetEquals(m21.GetActiveElements()));

            // 3. Property: Monotonicity & Least Upper Bound (r1 <= r1 ⊔ r2 AND r2 <= r1 ⊔ r2)
            Assert.True(r1.Compare(m12));
            Assert.True(r2.Compare(m12));

            // 4. Property: Associativity ((r1 ⊔ r2) ⊔ r3 ≡ r1 ⊔ (r2 ⊔ r3))
            var assocLeft = m12.Clone();
            assocLeft.Merge(r3);

            var m23 = r2.Clone();
            m23.Merge(r3);
            var assocRight = r1.Clone();
            assocRight.Merge(m23);

            Assert.True(assocLeft.SemilatticeEquals(assocRight));
            Assert.True(assocLeft.GetActiveElements().SetEquals(assocRight.GetActiveElements()));
        }
    }

    [Fact]
    public void PropertyBased_Convergence_Holds_Under_Network_Reordering_And_Duplication()
    {
        var netConfig = new NetworkFaultConfig
        {
            Seed = 999,
            MinLatencyMs = 10,
            MaxLatencyMs = 200,
            ReorderJitterMs = 150,
            DuplicationRate = 0.25,
            PacketLossRate = 0.0
        };

        var network = new SimulatedNetwork(netConfig);
        int clientCount = 5;
        var replicas = new Dictionary<string, LWWElementSet<string>>();

        for (int i = 0; i < clientCount; i++)
        {
            string id = $"node_{i}";
            replicas[id] = new LWWElementSet<string>();
            network.RegisterNode(id, msg =>
            {
                var (isAdd, item, ts) = ((bool, string, Timestamp))msg.Payload;
                if (isAdd)
                    replicas[msg.ReceiverId].Add(item, ts);
                else
                    replicas[msg.ReceiverId].Remove(item, ts);
            });
        }

        var rng = new Random(999);
        for (int opIdx = 0; opIdx < 500; opIdx++)
        {
            string sender = $"node_{rng.Next(clientCount)}";
            string item = $"CartItem_{rng.Next(30)}";
            bool isAdd = rng.NextDouble() > 0.4;
            var ts = new Timestamp(opIdx + 1, sender);

            if (isAdd) replicas[sender].Add(item, ts);
            else replicas[sender].Remove(item, ts);

            for (int r = 0; r < clientCount; r++)
            {
                string receiver = $"node_{r}";
                if (receiver != sender)
                {
                    network.EnqueueVirtual(sender, receiver, (isAdd, item, ts), opIdx * 5);
                }
            }
        }

        network.FlushVirtualQueue();

        Assert.True(network.TotalReordered > 0);
        Assert.True(network.TotalDuplicated > 0);

        var referenceReplica = replicas["node_0"];
        foreach (var (nodeId, replica) in replicas)
        {
            Assert.True(replica.SemilatticeEquals(referenceReplica), $"Node {nodeId} semilattice diverged!");
            Assert.True(replica.GetActiveElements().SetEquals(referenceReplica.GetActiveElements()), $"Node {nodeId} query diverged!");
        }
    }

    [Fact]
    public void TombstoneGC_With_WuuBernstein_VectorClock_Stability_Does_Not_Resurrect_Deleted_Elements()
    {
        var crdt = new LWWElementSet<string>();
        var stabilityTracker = new CausalStabilityTracker(new[] { "c1", "c2", "c3" });

        crdt.Add("DeletedBook", new Timestamp(10, "c1"));
        crdt.Remove("DeletedBook", new Timestamp(20, "c1"));

        crdt.Add("ActiveBook", new Timestamp(15, "c1"));
        crdt.Remove("ActiveBook", new Timestamp(25, "c1"));
        crdt.Add("ActiveBook", new Timestamp(30, "c1"));

        // Giai đoạn 1: Các replica mới chỉ cập nhật tiến độ cục bộ, CHƯA trao đổi VectorClock với nhau.
        // Ma trận Wuu-Bernstein 2D phải chặn dọn rác (không được dùng fallback vô hướng).
        stabilityTracker.RecordReplicaProgress("c1", 30);
        stabilityTracker.RecordReplicaProgress("c2", 26);
        stabilityTracker.RecordReplicaProgress("c3", 28);

        Assert.Equal(0, stabilityTracker.GetStableHorizon());
        Assert.False(stabilityTracker.IsStable(new Timestamp(20, "c1")));
        Assert.Equal(0, crdt.PruneTombstones((_, ts) => stabilityTracker.IsStable(ts)));
        Assert.Equal(2, crdt.TombstoneCount);

        // Giai đoạn 2: c2 và c3 đã nhận và hợp nhất VectorClock từ c1 (c2 biết c1 tới 26, c3 biết c1 tới 28).
        var vc1 = new VectorClock(new Dictionary<string, long> { ["c1"] = 30, ["c2"] = 26, ["c3"] = 28 });
        var vc2 = new VectorClock(new Dictionary<string, long> { ["c1"] = 26, ["c2"] = 26, ["c3"] = 25 });
        var vc3 = new VectorClock(new Dictionary<string, long> { ["c1"] = 28, ["c2"] = 26, ["c3"] = 28 });

        Assert.True(new VectorClock(new Dictionary<string, long> { ["c1"] = 10 }).HappenedBefore(vc1));

        stabilityTracker.ObserveVectorClock("c1", vc1);
        stabilityTracker.ObserveVectorClock("c2", vc2);
        stabilityTracker.ObserveVectorClock("c3", vc3);

        Assert.Equal(25, stabilityTracker.GetStableHorizon());
        Assert.True(stabilityTracker.IsStable(new Timestamp(25, "c1")));
        Assert.False(stabilityTracker.IsStable(new Timestamp(27, "c1")));

        var overheadBefore = crdt.CalculateOverhead();
        int pruned = crdt.PruneTombstones((_, ts) => stabilityTracker.IsStable(ts));
        var overheadAfter = crdt.CalculateOverhead();

        Assert.Equal(2, pruned);
        Assert.Equal(0, crdt.TombstoneCount);
        Assert.True(overheadAfter.MetadataBytes < overheadBefore.MetadataBytes);
        Assert.False(crdt.Lookup("DeletedBook"));
        Assert.True(crdt.Lookup("ActiveBook"));
    }

    [Fact]
    public void BUG01_AfterGC_DelayedDuplicateAdd_And_StaleSnapshotMerge_DoNotResurrect_While_NewerAdd_Works()
    {
        var tracker = new CausalStabilityTracker(new[] { "a", "b" });
        foreach (var id in new[] { "a", "b" })
        {
            tracker.ObserveVectorClock(id, new VectorClock(new Dictionary<string, long> { ["a"] = 20, ["b"] = 20 }));
        }

        // 1. Sau khi GC, nhận lại bản tin Add cũ/trùng ở timestamp 10 không làm phần tử sống lại
        var set1 = new LWWElementSet<string>();
        set1.Add("book", new Timestamp(10, "a"));
        set1.Remove("book", new Timestamp(20, "a"));
        int pruned1 = set1.PruneTombstones((_, t) => tracker.IsStable(t));
        Assert.Equal(1, pruned1);
        Assert.False(set1.Add("book", new Timestamp(10, "a")));
        Assert.False(set1.Lookup("book"));

        // 2. Merge với một snapshot cũ chụp trước khi xóa và trước khi GC không làm phần tử sống lại (cả 2 chiều)
        var set2 = new LWWElementSet<string>();
        set2.Add("book", new Timestamp(10, "a"));
        set2.Add("kept_item", new Timestamp(5, "a"));
        var staleSnapshot = set2.Clone();

        set2.Remove("book", new Timestamp(20, "a"));
        set2.PruneTombstones((_, t) => tracker.IsStable(t));

        // Trước khi Merge, trạng thái đã xóa & GC (set2) và snapshot cũ (staleSnapshot) KHÔNG tương đương bán dàn:
        // staleSnapshot <= set2 là true, nhưng set2 <= staleSnapshot là false!
        Assert.True(staleSnapshot.Compare(set2));
        Assert.False(set2.Compare(staleSnapshot));
        Assert.False(set2.SemilatticeEquals(staleSnapshot));
        Assert.False(staleSnapshot.SemilatticeEquals(set2));

        var mergedForward = set2.Clone();
        mergedForward.Merge(staleSnapshot);
        Assert.False(mergedForward.Lookup("book"));
        Assert.True(mergedForward.Lookup("kept_item"));

        var mergedBackward = staleSnapshot.Clone();
        mergedBackward.Merge(set2);
        Assert.False(mergedBackward.Lookup("book"));
        Assert.True(mergedBackward.Lookup("kept_item"));
        Assert.True(mergedForward.SemilatticeEquals(mergedBackward));

        // 3. Lần Add mới hợp lệ (timestamp > PrunedHorizon) vẫn hoạt động bình thường
        Assert.True(mergedForward.Add("book", new Timestamp(21, "a")));
        Assert.True(mergedForward.Lookup("book"));
    }

    [Fact]
    public void WebHub_DuplicateAdd_TabGC_And_ReloadClock_Work_Correctly()
    {
        var stateService = new StateSync.Server.SharedCartStateService();
        var item = new ShoppingCartItem("LAPTOP_01", "ThinkPad X1 Carbon", 1850m, 1);

        // 1. Kịch bản Thêm @330 -> Xóa @340 -> Gửi lại Thêm trùng @330 trên cùng client/connection:
        // Bản tin trùng @330 phải giữ nguyên timestamp 330 và KHÔNG làm sản phẩm sống lại
        var (t1, c1) = stateService.ResolveClientOperationClock("conn_1", "web_tab_a", 330, isAdd: true);
        stateService.CrdtCart.Add(item, new Timestamp(t1, c1));
        Assert.Equal(330, t1);
        Assert.True(stateService.CrdtCart.Lookup(item));

        var (t2, c2) = stateService.ResolveClientOperationClock("conn_1", "web_tab_a", 340, isAdd: false);
        stateService.CrdtCart.Remove(item, new Timestamp(t2, c2));
        Assert.Equal(340, t2);
        Assert.False(stateService.CrdtCart.Lookup(item));

        var (t3Replay, c3) = stateService.ResolveClientOperationClock("conn_1", "web_tab_a", 330, isAdd: true);
        stateService.CrdtCart.Add(item, new Timestamp(t3Replay, c3));
        Assert.Equal(330, t3Replay);
        Assert.False(stateService.CrdtCart.Lookup(item));

        // 2. Kịch bản nút GC với ID tab "web_tab_a":
        // Demo tập trung thu gọn trạng thái server; không giả lập ACK từ browser.
        Assert.Equal(1, stateService.CrdtCart.TombstoneCount);
        stateService.RunGarbageCollection();
        Assert.Equal(0, stateService.CrdtCart.TombstoneCount);
        Assert.Equal(340, stateService.CrdtCart.PrunedHorizon.Ticks);

        // 3. Reconnect giữ nguyên định danh thao tác; thao tác mới phải lấy clock từ snapshot.
        var reloadService = new StateSync.Server.SharedCartStateService();
        var (r1, rc1) = reloadService.ResolveClientOperationClock("conn_a", "web_tab_a", 310, isAdd: true);
        reloadService.CrdtCart.Add(item, new Timestamp(r1, rc1));
        var (r2, rc2) = reloadService.ResolveClientOperationClock("conn_a", "web_tab_a", 320, isAdd: false);
        reloadService.CrdtCart.Remove(item, new Timestamp(r2, rc2));
        Assert.False(reloadService.CrdtCart.Lookup(item));

        // Gửi lại thao tác 310 sau reconnect vẫn là thao tác cũ, không được đổi thành 330.
        var (r3Reload, rc3) = reloadService.ResolveClientOperationClock("conn_b", "web_tab_a", 310, isAdd: true);
        Assert.Equal(310, r3Reload);
        reloadService.CrdtCart.Add(item, new Timestamp(r3Reload, rc3));
        Assert.False(reloadService.CrdtCart.Lookup(item));
        // Thao tác mới lấy clock từ snapshot và tăng lên trước khi gửi.
        var snapshot = reloadService.GetSnapshot();
        reloadService.ApplyOperation(item, snapshot.ServerLogicalTick + 10, "web_tab_a", true);
        Assert.True(reloadService.CrdtCart.Lookup(item));
    }

    [Fact]
    public async Task BUG02_Reciprocal_Merge_And_Compare_Complete_Without_Deadlock()
    {
        var a = new LWWElementSet<string>();
        var b = new LWWElementSet<string>();
        a.Add("itemA", new Timestamp(10, "a"));
        b.Add("itemB", new Timestamp(20, "b"));

        using var gate = new Barrier(2);
        var t1 = Task.Run(() =>
        {
            gate.SignalAndWait();
            for (int i = 0; i < 1000; i++)
            {
                a.Merge(b);
                _ = a.Compare(b);
            }
        });
        var t2 = Task.Run(() =>
        {
            gate.SignalAndWait();
            for (int i = 0; i < 1000; i++)
            {
                b.Merge(a);
                _ = b.Compare(a);
            }
        });

        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(a.SemilatticeEquals(b));
        Assert.True(a.Lookup("itemA") && a.Lookup("itemB"));
    }

    [Fact]
    public void LIMIT04_ShoppingCartItem_Payload_Updates_And_Merges_Converge_On_All_Attributes()
    {
        var a = new LWWElementSet<ShoppingCartItem>();
        var b = new LWWElementSet<ShoppingCartItem>();

        a.Add(new ShoppingCartItem("P1", "Book", 10m, Quantity: 1), new Timestamp(10, "a"));
        b.Add(new ShoppingCartItem("P1", "Book", 10m, Quantity: 5), new Timestamp(20, "b"));

        // Trước khi Merge, hai replica có Quantity khác nhau
        Assert.False(a.SemilatticeEquals(b));

        a.Merge(b);
        b.Merge(a);

        int qa = a.GetActiveElements().Single().Quantity;
        int qb = b.GetActiveElements().Single().Quantity;

        Assert.Equal(5, qa);
        Assert.Equal(5, qb);
        Assert.True(a.SemilatticeEquals(b));

        // Kiểm tra tương tự trên Baseline NaiveLWWSet
        var baseA = new NaiveLWWSet<ShoppingCartItem>();
        var baseB = new NaiveLWWSet<ShoppingCartItem>();
        baseA.Add(new ShoppingCartItem("P1", "Book", 10m, Quantity: 1), new Timestamp(10, "a"));
        baseB.Add(new ShoppingCartItem("P1", "Book", 10m, Quantity: 5), new Timestamp(20, "b"));
        baseA.Merge(baseB);
        baseB.Merge(baseA);
        Assert.Equal(5, baseA.GetActiveElements().Single().Quantity);
        Assert.Equal(5, baseB.GetActiveElements().Single().Quantity);
    }

    [Fact]
    public void BUG05_CalculateOverhead_Distinguishes_Empty_Cart_From_Empty_Payload_With_Tombstones()
    {
        var s = new LWWElementSet<string>();

        // Giỏ trống hoàn toàn: Payload = 0, Metadata = 0, Overhead = 0%
        var emptyMetrics = s.CalculateOverhead();
        Assert.Equal(0, emptyMetrics.PayloadBytes);
        Assert.Equal(0, emptyMetrics.MetadataBytes);
        Assert.Equal(0.0, emptyMetrics.OverheadPercentage);

        // Giỏ đã xóa hết mặt hàng nhưng còn Tombstone: Payload = 0, Metadata > 0, Overhead = 100% (không phải 0%)
        s.Add("book", new Timestamp(10, "a"));
        s.Remove("book", new Timestamp(20, "a"));
        var tombstoneOnlyMetrics = s.CalculateOverhead();
        Assert.Equal(0, tombstoneOnlyMetrics.PayloadBytes);
        Assert.True(tombstoneOnlyMetrics.MetadataBytes > 0);
        Assert.True(tombstoneOnlyMetrics.OverheadPercentage > 0);
    }

    [Fact]
    public void ORSet_Satisfies_Concurrent_AddWins_Semantics()
    {
        var r1 = new ORSet<string>();
        var r2 = new ORSet<string>();

        r1.Add("Headphones");
        r2.Merge(r1);

        r1.Remove("Headphones");
        r2.Add("Headphones");

        var m1 = new ORSet<string>();
        m1.Merge(r1);
        m1.Merge(r2);

        Assert.True(m1.Lookup("Headphones"));
    }

    [Fact]
    public void PropertyBased_ORSet_Semilattice_Laws_Hold_Across_200_Random_Workloads()
    {
        var rng = new Random(2027);

        for (int trial = 0; trial < 200; trial++)
        {
            var r1 = GenerateRandomORSet(rng, 40);
            var r2 = GenerateRandomORSet(rng, 40);
            var r3 = GenerateRandomORSet(rng, 40);

            // 1. Idempotence: r1 ⊔ r1 == r1
            var idem = r1.Clone();
            idem.Merge(r1);
            Assert.True(r1.GetActiveElements().SetEquals(idem.GetActiveElements()));

            // 2. Commutativity: r1 ⊔ r2 == r2 ⊔ r1
            var m12 = r1.Clone();
            m12.Merge(r2);
            var m21 = r2.Clone();
            m21.Merge(r1);
            Assert.True(m12.GetActiveElements().SetEquals(m21.GetActiveElements()));

            // 3. Associativity: (r1 ⊔ r2) ⊔ r3 == r1 ⊔ (r2 ⊔ r3)
            var left = m12.Clone();
            left.Merge(r3);

            var m23 = r2.Clone();
            m23.Merge(r3);
            var right = r1.Clone();
            right.Merge(m23);
            Assert.True(left.GetActiveElements().SetEquals(right.GetActiveElements()));
        }
    }

    private static LWWElementSet<string> GenerateRandomReplica(Random rng, string clientId, int opsCount)
    {
        var set = new LWWElementSet<string>();
        for (int i = 0; i < opsCount; i++)
        {
            string item = $"Item_{rng.Next(15)}";
            var ts = new Timestamp(rng.Next(1, 500), clientId);
            if (rng.NextDouble() > 0.4)
                set.Add(item, ts);
            else
                set.Remove(item, ts);
        }
        return set;
    }

    private static ORSet<string> GenerateRandomORSet(Random rng, int opsCount)
    {
        var set = new ORSet<string>();
        for (int i = 0; i < opsCount; i++)
        {
            string item = $"Item_{rng.Next(12)}";
            if (rng.NextDouble() > 0.4)
                set.Add(item);
            else
                set.Remove(item);
        }
        return set;
    }
}
