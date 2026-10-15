using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using StateSync.Core;

namespace StateSync.Server;

/// <summary>
/// SignalR Hub điều phối đồng bộ trạng thái Giỏ hàng dùng chung (Collaborative Shopping Cart)
/// theo cơ chế Operation-based (CmRDT) kết hợp State Snapshot cho client mới kết nối.
/// Hỗ trợ song song cả 2 kho trạng thái: CRDT (LWW-Element-Set) và Baseline (Naive LWW) để đối chứng trực quan.
/// </summary>
public class StateSyncHub : Hub
{
    private readonly SharedCartStateService _stateService;

    public StateSyncHub(SharedCartStateService stateService)
    {
        _stateService = stateService;
    }

    public override async Task OnConnectedAsync()
    {
        await BroadcastStateAsync("CLIENT_CONNECTED");
        await base.OnConnectedAsync();
    }

    public async Task BroadcastAdd(ShoppingCartItem item, long logicalTicks, string clientId)
    {
        var ts = new Timestamp(logicalTicks, clientId);

        _stateService.CrdtCart.Add(item, ts);
        _stateService.BaselineCart.Add(item, ts);
        _stateService.StabilityTracker.RecordReplicaProgress(clientId, logicalTicks);

        await BroadcastStateAsync($"ADD {item.Name} (@t={logicalTicks}, {clientId})");
    }

    public async Task BroadcastRemove(ShoppingCartItem item, long logicalTicks, string clientId)
    {
        var ts = new Timestamp(logicalTicks, clientId);

        _stateService.CrdtCart.Remove(item, ts);
        _stateService.BaselineCart.Remove(item, ts);
        _stateService.StabilityTracker.RecordReplicaProgress(clientId, logicalTicks);

        await BroadcastStateAsync($"REMOVE {item.Name} (@t={logicalTicks}, {clientId})");
    }

    /// <summary>
    /// Kịch bản tái hiện trực tiếp lỗi Out-of-Order (Gói tin Remove đến trước, gói tin Add cũ bị lag đến sau)
    /// ngay trên SignalR Server để chứng minh sự khác biệt giữa CRDT và Baseline.
    /// </summary>
    public async Task SimulateOutOfOrderAnomaly()
    {
        var item = new ShoppingCartItem("BOOK_DP_01", "Design Patterns (GoF)", 45.0m, 1);
        var addTs = new Timestamp(100, "client_A");
        var removeTs = new Timestamp(200, "client_A");

        // Do mạng jitter, gói tin Remove(t=200) đến trước gói tin Add(t=100)
        _stateService.CrdtCart.Remove(item, removeTs);
        _stateService.BaselineCart.Remove(item, removeTs);

        _stateService.CrdtCart.Add(item, addTs);
        _stateService.BaselineCart.Add(item, addTs);

        await BroadcastStateAsync("ANOMALY_TEST: Out-of-Order Packet (Remove@t=200 arrived before delayed Add@t=100)");
    }

    public async Task RunGarbageCollection()
    {
        int pruned = _stateService.CrdtCart.PruneTombstones((_, _) => true);
        await BroadcastStateAsync($"GC_PRUNED: Đã dọn sạch {pruned} Tombstones ổn định");
    }

    private async Task BroadcastStateAsync(string lastEvent)
    {
        var overhead = _stateService.CrdtCart.CalculateOverhead();
        await Clients.All.SendAsync("StateUpdated", new
        {
            LastEvent = lastEvent,
            CrdtItems = _stateService.CrdtCart.GetActiveElements().OrderBy(x => x.ProductId).ToList(),
            BaselineItems = _stateService.BaselineCart.GetActiveElements().OrderBy(x => x.ProductId).ToList(),
            TombstoneCount = _stateService.CrdtCart.TombstoneCount,
            OverheadPercent = overhead.OverheadPercentage,
            RamKb = overhead.EstimatedRamBytes / 1024.0
        });
    }
}

public class SharedCartStateService
{
    public LWWElementSet<ShoppingCartItem> CrdtCart { get; } = new();
    public NaiveLWWSet<ShoppingCartItem> BaselineCart { get; } = new();
    public CausalStabilityTracker StabilityTracker { get; } = new(new[] { "web_client_1", "web_client_2" });
}
