using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using StateSync.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<SharedCartStateService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<StateSyncHub>("/hubs/statesync");
app.MapGet("/api/status", (SharedCartStateService state) =>
{
    var snapshot = state.GetSnapshot();
    return new
    {
        Status = "StateSync ASP.NET Core SignalR Server Running",
        CrdtActiveItems = snapshot.CrdtItems,
        BaselineActiveItems = snapshot.BaselineItems,
        snapshot.Version,
        snapshot.ServerLogicalTick,
        snapshot.PrunedHorizonTick,
        snapshot.TombstoneCount,
        MetadataOverheadPercent = snapshot.OverheadPercent,
        snapshot.OverheadDisplay
    };
});

app.Run();
