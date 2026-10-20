using Microsoft.AspNetCore.SignalR;
using StateSync.Core;

namespace StateSync.Server;

/// <summary>
/// Centralized demo: clients submit timestamped operations; the server owns both
/// sets and broadcasts snapshots. Independent replicas live in the benchmark.
/// </summary>
public class StateSyncHub(SharedCartStateService stateService) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("StateUpdated", stateService.GetSnapshot("CLIENT_CONNECTED"));
        await base.OnConnectedAsync();
    }

    public Task BroadcastAdd(ShoppingCartItem item, long logicalTicks, string clientId) =>
        Clients.All.SendAsync("StateUpdated", stateService.ApplyOperation(item, logicalTicks, clientId, true));

    public Task BroadcastRemove(ShoppingCartItem item, long logicalTicks, string clientId) =>
        Clients.All.SendAsync("StateUpdated", stateService.ApplyOperation(item, logicalTicks, clientId, false));

    public Task SimulateOutOfOrderAnomaly() =>
        Clients.All.SendAsync("StateUpdated", stateService.SimulateOutOfOrderAnomaly());

    public Task RunGarbageCollection() =>
        Clients.All.SendAsync("StateUpdated", stateService.RunGarbageCollection());
}

public record CartSnapshot(
    long Version, string LastEvent, long ServerLogicalTick, long PrunedHorizonTick,
    IReadOnlyList<ShoppingCartItem> CrdtItems, IReadOnlyList<ShoppingCartItem> BaselineItems,
    int TombstoneCount, long PayloadBytes, long MetadataBytes,
    double OverheadPercent, string OverheadDisplay, double RamKb);

public class SharedCartStateService
{
    public const long MaxClientTick = 9_007_199_254_740_000;
    private readonly object _stateLock = new();
    private long _maxObservedTick = 300;
    private long _version;

    public LWWElementSet<ShoppingCartItem> CrdtCart { get; } = new();
    public NaiveLWWSet<ShoppingCartItem> BaselineCart { get; } = new();

    public long MaxObservedTick
    {
        get { lock (_stateLock) return _maxObservedTick; }
    }

    /// <summary>
    /// Operation identity is (clientId, proposedTick), independent of the connection.
    /// Never retimestamp a retry: doing so turns a stale Add into a new Add.
    /// New operations must advance the client's clock after reading a snapshot.
    /// </summary>
    public (long EffectiveTick, string EffectiveClientId) ResolveClientOperationClock(
        string connectionId, string clientId, long proposedTick, bool isAdd = true)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128)
            throw new HubException("ClientId phải có từ 1 đến 128 ký tự và được giữ nguyên khi reconnect.");
        if (proposedTick <= 0 || proposedTick > MaxClientTick)
            throw new HubException("Timestamp phải là số nguyên dương trong phạm vi đồng hồ JavaScript.");

        lock (_stateLock)
        {
            _maxObservedTick = Math.Max(_maxObservedTick, proposedTick);
            return (proposedTick, clientId);
        }
    }

    public CartSnapshot ApplyOperation(ShoppingCartItem item, long logicalTicks, string clientId, bool isAdd)
    {
        ValidateItem(item);
        lock (_stateLock)
        {
            var (tick, id) = ResolveClientOperationClock(string.Empty, clientId, logicalTicks, isAdd);
            var ts = new Timestamp(tick, id);
            if (isAdd)
            {
                CrdtCart.Add(item, ts);
                BaselineCart.Add(item, ts);
            }
            else
            {
                CrdtCart.Remove(item, ts);
                BaselineCart.Remove(item, ts);
            }
            _version++;
            return GetSnapshot($"{(isAdd ? "ADD" : "REMOVE")} {item.Name} (Qty={item.Quantity}, @t={tick}, {id})");
        }
    }

    public CartSnapshot SimulateOutOfOrderAnomaly()
    {
        lock (_stateLock)
        {
            if (_maxObservedTick > MaxClientTick - 250)
                throw new HubException("Đồng hồ đã đạt giới hạn; hãy khởi động lại demo.");
            var item = new ShoppingCartItem("BOOK_DP_01", "Design Patterns (GoF)", 45m, 1);
            var add = new Timestamp(_maxObservedTick + 100, "web_client_anomaly");
            var remove = new Timestamp(_maxObservedTick + 200, "web_client_anomaly");
            CrdtCart.Remove(item, remove);
            BaselineCart.Remove(item, remove);
            CrdtCart.Add(item, add);
            BaselineCart.Add(item, add);
            _maxObservedTick += 250;
            _version++;
            return GetSnapshot($"ANOMALY_TEST: Remove@{remove.Ticks} trước Add@{add.Ticks}");
        }
    }

    public CartSnapshot RunGarbageCollection()
    {
        lock (_stateLock)
        {
            // One authoritative owner; browsers do not maintain offline replicas.
            // Compaction commits a cutoff, rejecting future stale operations.
            // This is NOT an assertion that browsers acknowledged a broadcast.
            int pruned = CrdtCart.PruneTombstones((_, _) => true);
            _version++;
            return GetSnapshot($"GC_PRUNED: {pruned} tombstones (server compaction, Cutoff={CrdtCart.PrunedHorizon.Ticks})");
        }
    }

    public CartSnapshot GetSnapshot(string lastEvent = "STATUS")
    {
        lock (_stateLock)
        {
            var overhead = CrdtCart.CalculateOverhead();
            string display = overhead.PayloadBytes == 0 && overhead.MetadataBytes > 0
                ? $"N/A (Payload 0 B, Metadata {overhead.MetadataBytes} B)"
                : $"{overhead.OverheadPercentage:F1}% ({overhead.MetadataBytes} B / {overhead.PayloadBytes} B)";
            return new CartSnapshot(_version, lastEvent, _maxObservedTick, CrdtCart.PrunedHorizon.Ticks,
                CrdtCart.GetActiveElements().OrderBy(x => x.ProductId, StringComparer.Ordinal).ToArray(),
                BaselineCart.GetActiveElements().OrderBy(x => x.ProductId, StringComparer.Ordinal).ToArray(),
                CrdtCart.TombstoneCount, overhead.PayloadBytes, overhead.MetadataBytes,
                overhead.OverheadPercentage, display, overhead.EstimatedRamBytes / 1024.0);
        }
    }

    private static void ValidateItem(ShoppingCartItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.ProductId) || item.ProductId.Length > 128 ||
            string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256 || item.Price < 0 || item.Quantity <= 0)
            throw new HubException("Sản phẩm cần ID (1–128), tên (1–256), giá không âm và số lượng dương.");
    }
}
