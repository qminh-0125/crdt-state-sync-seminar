using StateSync.Core;
using Xunit;

namespace StateSync.Tests;

/// <summary>
/// Thử nghiệm đối chứng: Tái hiện lỗi kinh điển của Amazon Shopping Cart
/// trên Baseline Naive LWW và chứng minh CRDT khắc phục triệt để.
/// </summary>
public class LWWVsBaselineTests
{
    [Fact]
    public void Baseline_Fails_On_Out_Of_Order_Delete_And_Add()
    {
        // Kịch bản:
        // Client 1 thêm sách "Design_Patterns" lúc t=100.
        // Sau đó Client 1 xóa sách "Design_Patterns" lúc t=200.
        // Tuy nhiên, do mạng bị trễ hoặc gửi lại gói tin cũ (Retransmission/Reordering),
        // gói tin Add(t=100) lại đến Server SAU gói tin Remove(t=200).

        var baseline = new NaiveLWWSet<string>();

        // Giả lập gói tin Remove đến trước do delay mạng
        baseline.Remove("Design_Patterns", new Timestamp(200, "client_1"));

        // Gói tin Add cũ bị lag đến sau
        baseline.Add("Design_Patterns", new Timestamp(100, "client_1"));

        // HẬU QUẢ CỦA BASELINE:
        // Cuốn sách đáng lẽ đã bị xóa thì lại xuất hiện lại (Resurrection Anomaly)!
        Assert.True(baseline.Lookup("Design_Patterns")); // Lỗi mất tính nhất quán!
    }

    [Fact]
    public void CRDT_Succeeds_On_Out_Of_Order_Delete_And_Add()
    {
        // Cùng một kịch bản mạng bị xáo trộn thứ tự:
        var crdt = new LWWElementSet<string>();

        // Gói tin Remove(t=200) đến trước
        crdt.Remove("Design_Patterns", new Timestamp(200, "client_1"));

        // Gói tin Add(t=100) bị lag đến sau
        crdt.Add("Design_Patterns", new Timestamp(100, "client_1"));

        // VỚI CRDT:
        // Nhờ có Tombstone lưu vết timestamp lúc xóa là 200 > 100,
        // cuốn sách vẫn được xác định chính xác là ĐÃ BỊ XÓA!
        Assert.False(crdt.Lookup("Design_Patterns")); // Tính đúng đắn tuyệt đối!
    }

    [Fact]
    public void Baseline_Causes_Lost_Updates_During_Concurrent_Sync()
    {
        // Kịch bản: 2 Client cùng có giỏ hàng ban đầu trống.
        // Client A thêm "Laptop"
        // Client B thêm "Mouse"
        // Cả 2 cùng đồng bộ với Server theo kiểu Naive Snapshot Overwrite

        var serverBaseline = new NaiveLWWSet<string>();
        var clientA = new NaiveLWWSet<string>();
        var clientB = new NaiveLWWSet<string>();

        clientA.Add("Laptop", new Timestamp(100, "client_A"));
        clientB.Add("Mouse", new Timestamp(105, "client_B"));

        // Nếu client A đồng bộ trước, sau đó Client B ghi đè toàn bộ trạng thái của mình lên server
        serverBaseline = clientA.Clone();
        // Client B ghi đè mà không biết đến Laptop
        serverBaseline = clientB.Clone();

        // Mất mát dữ liệu ở Baseline: "Laptop" bị biến mất hoàn toàn!
        Assert.False(serverBaseline.Lookup("Laptop")); // Lost Update!
        Assert.True(serverBaseline.Lookup("Mouse"));
    }

    [Fact]
    public void CRDT_Preserves_All_Concurrent_Updates()
    {
        var crdtA = new LWWElementSet<string>();
        var crdtB = new LWWElementSet<string>();

        crdtA.Add("Laptop", new Timestamp(100, "client_A"));
        crdtB.Add("Mouse", new Timestamp(105, "client_B"));

        // Gộp trạng thái CRDT (hoặc áp dụng cả 2 operation)
        crdtA.Merge(crdtB);
        crdtB.Merge(crdtA);

        // Cả 2 client đều hội tụ và giữ trọn vẹn cả Laptop và Mouse!
        Assert.True(crdtA.Lookup("Laptop"));
        Assert.True(crdtA.Lookup("Mouse"));
        Assert.True(crdtB.Lookup("Laptop"));
        Assert.True(crdtB.Lookup("Mouse"));
    }
}
