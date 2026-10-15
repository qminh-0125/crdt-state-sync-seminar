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
    var overhead = state.CrdtCart.CalculateOverhead();
    return new
    {
        Status = "StateSync ASP.NET Core SignalR Server Running",
        CrdtActiveItems = state.CrdtCart.GetActiveElements(),
        BaselineActiveItems = state.BaselineCart.GetActiveElements(),
        TombstoneCount = state.CrdtCart.TombstoneCount,
        MetadataOverheadPercent = overhead.OverheadPercentage
    };
});

app.Run();
