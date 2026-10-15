using System;
using System.Collections.Generic;
using System.Linq;
using StateSync.Core;
using StateSync.NetworkSim;
using Xunit;

namespace StateSync.Tests;

/// <summary>
/// Kiểm chứng các thuộc tính toán học của bán dàn (Join-semilattice) và Bất biến (Invariants)
/// bằng cả Unit Test và Property-Based Randomized Testing (200 kịch bản ngẫu nhiên mỗi thuộc tính)
/// theo định nghĩa của Marc Shapiro et al. (INRIA RR-7687 & RR-7506).
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

        var vc1 = new VectorClock(new Dictionary<string, long> { ["c1"] = 30 });
        var vc2 = new VectorClock(new Dictionary<string, long> { ["c2"] = 26 });
        var vc3 = new VectorClock(new Dictionary<string, long> { ["c3"] = 28 });

        Assert.True( new VectorClock(new Dictionary<string, long> { ["c1"] = 10 }).HappenedBefore(vc1) );

        crdt.Add("DeletedBook", new Timestamp(10, "c1"));
        crdt.Remove("DeletedBook", new Timestamp(20, "c1"));

        crdt.Add("ActiveBook", new Timestamp(15, "c1"));
        crdt.Remove("ActiveBook", new Timestamp(25, "c1"));
        crdt.Add("ActiveBook", new Timestamp(30, "c1"));

        stabilityTracker.ObserveVectorClock("c1", vc1);
        stabilityTracker.ObserveVectorClock("c2", vc2);
        stabilityTracker.ObserveVectorClock("c3", vc3);

        Assert.Equal(26, stabilityTracker.GetStableHorizon());

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
}
