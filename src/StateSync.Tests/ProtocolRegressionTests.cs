using Microsoft.AspNetCore.SignalR;
using StateSync.Core;
using StateSync.NetworkSim;
using StateSync.Server;

namespace StateSync.Tests;

public class ProtocolRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retry_Across_Connections_Keeps_Identity_Before_And_After_GC(bool gc)
    {
        var service = new SharedCartStateService();
        var item = new ShoppingCartItem("P1", "Book", 10m);
        service.ApplyOperation(item, 1000, "tab_a", true);
        service.ApplyOperation(item, 1010, "tab_a", false);
        if (gc) service.RunGarbageCollection();

        foreach (string connection in new[] { "same_connection", "new_connection", "another_connection" })
        {
            var (tick, id) = service.ResolveClientOperationClock(connection, "tab_a", 1000);
            Assert.Equal(1000, tick);
            Assert.Empty(service.ApplyOperation(item, tick, id, true).CrdtItems);
        }

        var next = service.GetSnapshot().ServerLogicalTick + 10;
        Assert.Single(service.ApplyOperation(item, next, "tab_a", true).CrdtItems);
        // A delayed duplicate Remove must not delete a genuinely newer Add.
        Assert.Single(service.ApplyOperation(item, 1010, "tab_a", false).CrdtItems);
    }

    [Fact]
    public async Task Concurrent_Clients_Produce_Consistent_Versioned_Snapshots()
    {
        var service = new SharedCartStateService();
        var tasks = Enumerable.Range(1, 100).Select(i => Task.Run(() =>
            service.ApplyOperation(new ShoppingCartItem($"P{i}", "Book", i), 1000 + i, $"tab_{i}", true)));
        var snapshots = await Task.WhenAll(tasks);
        Assert.Equal(100, snapshots.Select(s => s.Version).Distinct().Count());
        foreach (var snapshot in snapshots)
        {
            Assert.Equal(snapshot.Version, snapshot.CrdtItems.Count);
            Assert.Equal(snapshot.CrdtItems.Count, snapshot.BaselineItems.Count);
        }
        Assert.Equal(100, service.GetSnapshot().CrdtItems.Count);
    }

    [Theory]
    [InlineData(0, "tab")]
    [InlineData(-1, "tab")]
    [InlineData(long.MaxValue, "tab")]
    [InlineData(310, "")]
    public void Invalid_Clock_Or_Identity_Does_Not_Mutate_State(long tick, string id)
    {
        var service = new SharedCartStateService();
        Assert.Throws<HubException>(() => service.ApplyOperation(new("P", "Book", 1m), tick, id, true));
        Assert.Equal(300, service.MaxObservedTick);
        Assert.Empty(service.GetSnapshot().CrdtItems);
    }

    [Fact]
    public void Invalid_Payload_Does_Not_Poison_Dictionary_Or_Clock()
    {
        var service = new SharedCartStateService();
        foreach (var item in new[] { new ShoppingCartItem(null!, "Book", 1), new("P", null!, 1),
                     new("P", "Book", -1), new("P", "Book", 1, 0) })
            Assert.Throws<HubException>(() => service.ApplyOperation(item, 310, "tab", true));
        Assert.Equal(300, service.MaxObservedTick);
        Assert.Equal(0, service.GetSnapshot().Version);
    }

    [Fact]
    public void Equal_Timestamps_With_Distinct_Payloads_Have_A_Total_TieBreak_And_Join_Order()
    {
        // The former F4 key rounded both prices to the same text.
        var first = new ShoppingCartItem("P", "Book|Name", 1.00001m);
        var second = first with { Price = 1.00002m };
        Assert.NotEqual(first.GetCanonicalPayloadString(), second.GetCanonicalPayloadString());
        var ts = new Timestamp(10, "a");
        var a = new LWWElementSet<ShoppingCartItem>();
        var b = new LWWElementSet<ShoppingCartItem>();
        a.Add(first, ts);
        b.Add(second, ts);
        Assert.False(a.SemilatticeEquals(b));
        var ab = a.Clone(); ab.Merge(b);
        var ba = b.Clone(); ba.Merge(a);
        Assert.True(ab.SemilatticeEquals(ba));
        Assert.True(a.Compare(ab));
        Assert.True(b.Compare(ab));
        Assert.True(ab.GetActiveElements().Single().PayloadEquals(ba.GetActiveElements().Single()));
        var na = new NaiveLWWSet<ShoppingCartItem>();
        var nb = new NaiveLWWSet<ShoppingCartItem>();
        na.Add(first, ts); nb.Add(second, ts);
        var nab = na.Clone(); nab.Merge(nb);
        var nba = nb.Clone(); nba.Merge(na);
        Assert.True(nab.GetActiveElements().Single().PayloadEquals(nba.GetActiveElements().Single()));
    }

    [Fact]
    public async Task Async_Network_Awaits_All_Deliveries_And_Propagates_Handler_Errors()
    {
        var net = new SimulatedNetwork(new NetworkFaultConfig
        { MinLatencyMs = 1, MaxLatencyMs = 1, ReorderJitterMs = 5, DuplicationRate = 1 });
        int received = 0;
        net.RegisterNode("b", _ => Interlocked.Increment(ref received));
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => net.SendAsync("a", "b", i)));
        Assert.Equal(80, received);
        Assert.Equal(40, net.TotalSent);
        Assert.Equal(40, net.TotalDuplicated);
        Assert.Equal(80, net.TotalDelivered);
        net.RegisterNode("bad", _ => throw new InvalidOperationException("handler failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.SendAsync("a", "bad", "message"));
    }

    [Fact]
    public void Network_Partition_Loss_And_RemoveBeforeAdd_Are_Exercised()
    {
        var net = new SimulatedNetwork(NetworkFaultConfig.Ideal);
        var receiver = new ORSet<string>();
        net.RegisterNode("b", _ => receiver.Add("book", "tag"));
        net.Partition(new[] { "a" }, new[] { "b" });
        net.EnqueueVirtual("a", "b", "blocked", 0);
        net.FlushVirtualQueue();
        Assert.Equal(1, net.TotalDropped);
        Assert.False(receiver.Lookup("book"));
        net.HealPartition();
        receiver.ApplyRemoveTags("book", new[] { "tag" });
        net.EnqueueVirtual("a", "b", "delayed add", 0);
        net.FlushVirtualQueue();
        Assert.False(receiver.Lookup("book"));

        var loss = new SimulatedNetwork(new NetworkFaultConfig { PacketLossRate = 1 });
        loss.EnqueueVirtual("a", "b", "lost", 0);
        Assert.Equal(1, loss.TotalDropped);
    }

    [Fact]
    public void Network_Rejects_Invalid_Configuration_And_Copies_Caller_Settings()
    {
        foreach (var invalid in new[] { new NetworkFaultConfig { MinLatencyMs = -1 },
                     new() { MaxLatencyMs = 0 }, new() { PacketLossRate = double.NaN },
                     new() { DuplicationRate = 1.1 }, new() { ReorderJitterMs = double.PositiveInfinity } })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedNetwork(invalid));
        var config = NetworkFaultConfig.Ideal;
        var net = new SimulatedNetwork(config);
        config.PacketLossRate = 1;
        int delivered = 0;
        net.RegisterNode("b", _ => delivered++);
        net.EnqueueVirtual("a", "b", "message", 0);
        net.FlushVirtualQueue();
        Assert.Equal(1, delivered);
        Assert.Equal(0, net.TotalDropped);
    }

    [Fact]
    public void GC_Epochs_Stale_Snapshots_And_New_Updates_Match_An_Independent_Log_Oracle()
    {
        var rng = new Random(1642026);
        for (int trial = 0; trial < 100; trial++)
        {
            var replicas = Enumerable.Range(0, 3).Select(_ => new LWWElementSet<string>()).ToArray();
            var latest = new Dictionary<string, (long Tick, bool Add)>();
            var stale = new List<LWWElementSet<string>>();
            for (int epoch = 0; epoch < 5; epoch++)
            {
                for (int op = 1; op <= 30; op++)
                {
                    long tick = epoch * 30 + op;
                    string item = $"item_{rng.Next(12)}";
                    int origin = rng.Next(3);
                    bool add = rng.Next(2) == 0;
                    latest[item] = (tick, add);
                    if (add) replicas[origin].Add(item, new(tick, $"c{origin}"));
                    else replicas[origin].Remove(item, new(tick, $"c{origin}"));
                }
                // Complete state dissemination before declaring this prefix stable.
                for (int i = 1; i < 3; i++) replicas[0].Merge(replicas[i]);
                for (int i = 1; i < 3; i++) replicas[i].Merge(replicas[0]);
                stale.Add(replicas[0].Clone());
                replicas[0].PruneTombstones((_, _) => true);
                foreach (var snapshot in stale.OrderBy(_ => rng.Next()))
                    replicas[0].Merge(snapshot);
                for (int i = 1; i < 3; i++) replicas[i].Merge(replicas[0]);
                var truth = latest.Where(kv => kv.Value.Add).Select(kv => kv.Key).ToHashSet();
                foreach (var replica in replicas)
                {
                    Assert.True(replica.GetActiveElements().SetEquals(truth));
                    Assert.True(replica.SemilatticeEquals(replicas[0]));
                }
                var left = stale[0].Clone(); left.Merge(replicas[0]); left.Merge(stale[^1]);
                var rightPart = replicas[0].Clone(); rightPart.Merge(stale[^1]);
                var right = stale[0].Clone(); right.Merge(rightPart);
                Assert.True(left.SemilatticeEquals(right));
                Assert.True(left.GetActiveElements().SetEquals(truth));
            }
        }
    }

    [Fact]
    public async Task Reciprocal_Merge_Of_All_ThreadSafe_Core_Types_Completes()
    {
        var v1 = new VectorClock(); var v2 = new VectorClock();
        var o1 = new ORSet<string>(); var o2 = new ORSet<string>();
        var n1 = new NaiveLWWSet<string>(); var n2 = new NaiveLWWSet<string>();
        v1.AdvanceTo("a", 10); v2.AdvanceTo("b", 20);
        o1.Add("a"); o2.Add("b");
        n1.Add("a", new(10, "a")); n2.Add("b", new(20, "b"));
        using var start = new Barrier(2);
        var forward = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 1000; i++) { v1.Merge(v2); o1.Merge(o2); n1.Merge(n2); _ = v1.HappenedBefore(v2); }
        });
        var backward = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 1000; i++) { v2.Merge(v1); o2.Merge(o1); n2.Merge(n1); _ = v2.HappenedBefore(v1); }
        });
        await Task.WhenAll(forward, backward).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(20, v1.Get("b")); Assert.Equal(10, v2.Get("a"));
        Assert.True(o1.GetActiveElements().SetEquals(o2.GetActiveElements()));
        Assert.True(n1.GetActiveElements().SetEquals(n2.GetActiveElements()));
    }

    [Fact]
    public async Task GC_Callbacks_Can_Merge_Without_Deadlock_Or_Removing_A_Newer_Tombstone()
    {
        var a = new LWWElementSet<string>(); var b = new LWWElementSet<string>();
        a.Remove("book", new(10, "a")); b.Remove("book", new(10, "a"));
        using var gate = new Barrier(2);
        var first = Task.Run(() => a.PruneTombstones((_, _) =>
        {
            gate.SignalAndWait(); a.Merge(b); return true;
        }));
        var second = Task.Run(() => b.PruneTombstones((_, _) =>
        {
            gate.SignalAndWait(); b.Merge(a); return true;
        }));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(a.Lookup("book")); Assert.False(b.Lookup("book"));
        var changed = new LWWElementSet<string>();
        changed.Remove("book", new(10, "a"));
        int pruned = changed.PruneTombstones((_, _) =>
        {
            changed.Remove("book", new(20, "a")); return true;
        });
        Assert.Equal(0, pruned);
        Assert.Equal(1, changed.TombstoneCount);
        Assert.Equal(0, changed.PrunedHorizon.Ticks);
    }
}
