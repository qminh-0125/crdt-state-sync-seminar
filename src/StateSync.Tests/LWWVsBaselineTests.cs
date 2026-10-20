using StateSync.Core;
using Xunit;

namespace StateSync.Tests;

/// <summary>
/// Thử nghiệm đối chứng: Tái hiện lỗi kinh điển của Amazon Shopping Cart
/// trên Baseline Naive LWW (cả giao thức NaiveLWWSet.Merge và Full-Snapshot Overwrite)
/// và chứng minh CRDT khắc phục triệt để (EVID-06).
/// </summary>
public class LWWVsBaselineTests
{
    [Fact]
    public void Baseline_Fails_On_Out_Of_Order_Delete_And_Add()
    {
        var baseline = new NaiveLWWSet<string>();

        // Giả lập gói tin Remove(t=200) đến trước do delay mạng
        baseline.Remove("Design_Patterns", new Timestamp(200, "client_1"));

        // Gói tin Add(t=100) cũ bị lag đến sau
        baseline.Add("Design_Patterns", new Timestamp(100, "client_1"));

        // HẬU QUẢ CỦA BASELINE:
        // Cuốn sách đáng lẽ đã bị xóa thì lại xuất hiện lại (Resurrection Anomaly)!
        Assert.True(baseline.Lookup("Design_Patterns"));
    }

    [Fact]
    public void CRDT_Succeeds_On_Out_Of_Order_Delete_And_Add()
    {
        var crdt = new LWWElementSet<string>();

        // Gói tin Remove(t=200) đến trước
        crdt.Remove("Design_Patterns", new Timestamp(200, "client_1"));

        // Gói tin Add(t=100) bị lag đến sau
        crdt.Add("Design_Patterns", new Timestamp(100, "client_1"));

        // VỚI CRDT:
        // Nhờ có Tombstone lưu vết timestamp lúc xóa là 200 > 100,
        // cuốn sách vẫn được xác định chính xác là ĐÃ BỊ XÓA!
        Assert.False(crdt.Lookup("Design_Patterns"));
    }

    [Fact]
    public void Baseline_NaiveMerge_Preserves_Distinct_Adds_But_Loses_Concurrent_Delete_Updates()
    {
        // EVID-06: Kiểm chứng trực tiếp trên phương thức NaiveLWWSet.Merge (giao thức dùng trong Benchmark):
        // 1. Khi 2 replica thêm 2 mặt hàng khác nhau ("Laptop" và "Mouse"), NaiveLWWSet.Merge giữ được cả hai.
        var clientA = new NaiveLWWSet<string>();
        var clientB = new NaiveLWWSet<string>();

        clientA.Add("Laptop", new Timestamp(100, "client_A"));
        clientB.Add("Mouse", new Timestamp(105, "client_B"));

        clientA.Merge(clientB);
        clientB.Merge(clientA);

        Assert.True(clientA.Lookup("Laptop") && clientA.Lookup("Mouse"));
        Assert.True(clientB.Lookup("Laptop") && clientB.Lookup("Mouse"));

        // 2. Tuy nhiên, khi Client A xóa "Laptop" ở t=200 trong lúc Client B thêm "Keyboard" ở t=210
        // (mà gói tin xóa chưa tới được Client B do đứt mạng/jitter), khi gọi NaiveLWWSet.Merge:
        // thao tác xóa "Laptop"@200 của Client A bị MẤT HOÀN TOÀN (Lost Delete Update) vì bản sao "Laptop"@100
        // từ Client B bị chép đè ngược lại sang Client A!
        clientA.Remove("Laptop", new Timestamp(200, "client_A"));
        clientB.Add("Keyboard", new Timestamp(210, "client_B"));

        Assert.False(clientA.Lookup("Laptop"));

        clientA.Merge(clientB);
        clientB.Merge(clientA);

        // Lỗi của NaiveLWWSet.Merge: "Laptop" đã xóa ở t=200 bị sống lại từ bản sao cũ t=100!
        Assert.True(clientA.Lookup("Laptop"));
        Assert.True(clientB.Lookup("Laptop"));
        Assert.True(clientA.Lookup("Keyboard"));
    }

    [Fact]
    public void Baseline_FullSnapshotOverwrite_Causes_Lost_Updates_During_Concurrent_Sync()
    {
        // EVID-06: Minh họa riêng cho cơ chế đồng bộ ghi đè toàn bộ Snapshot (Full-Snapshot Overwrite):
        // Client A thêm "Laptop", Client B thêm "Mouse" trên bản chụp giỏ hàng ban đầu rồi ghi đè lên Server.
        var serverBaseline = new NaiveLWWSet<string>();
        var clientA = new NaiveLWWSet<string>();
        var clientB = new NaiveLWWSet<string>();

        clientA.Add("Laptop", new Timestamp(100, "client_A"));
        clientB.Add("Mouse", new Timestamp(105, "client_B"));

        serverBaseline = clientA.Clone();
        serverBaseline = clientB.Clone();

        Assert.False(serverBaseline.Lookup("Laptop"));
        Assert.True(serverBaseline.Lookup("Mouse"));
    }

    [Fact]
    public void CRDT_Preserves_All_Concurrent_Updates_And_Deletions_During_Merge()
    {
        var crdtA = new LWWElementSet<string>();
        var crdtB = new LWWElementSet<string>();

        crdtA.Add("Laptop", new Timestamp(100, "client_A"));
        crdtB.Add("Mouse", new Timestamp(105, "client_B"));

        crdtA.Merge(crdtB);
        crdtB.Merge(crdtA);

        // Đồng thời: A xóa "Laptop" ở t=200, B thêm "Keyboard" ở t=210
        crdtA.Remove("Laptop", new Timestamp(200, "client_A"));
        crdtB.Add("Keyboard", new Timestamp(210, "client_B"));

        crdtA.Merge(crdtB);
        crdtB.Merge(crdtA);

        // CRDT bảo toàn cả mặt hàng mới ("Mouse", "Keyboard") lẫn thao tác xóa ("Laptop")!
        Assert.False(crdtA.Lookup("Laptop"));
        Assert.False(crdtB.Lookup("Laptop"));
        Assert.True(crdtA.Lookup("Mouse") && crdtA.Lookup("Keyboard"));
        Assert.True(crdtB.Lookup("Mouse") && crdtB.Lookup("Keyboard"));
    }
}
