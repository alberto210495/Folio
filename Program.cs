using System.Net;
using System.Text.Json.Serialization;
using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FolioDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Folio") ?? "Data Source=folio.db"));

builder.Services.AddMemoryCache();
builder.Services.Configure<FinnhubOptions>(builder.Configuration.GetSection("Finnhub"));
builder.Services.Configure<FolioOptions>(builder.Configuration.GetSection("Folio"));
builder.Services.AddHttpClient<FinnhubClient>((sp, http) =>
{
    var opt = builder.Configuration.GetSection("Finnhub").Get<FinnhubOptions>() ?? new();
    http.BaseAddress = new Uri(opt.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<TradingService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddHostedService<SnapshotWorker>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddHostedService<OrderWorker>();
builder.Services.AddScoped<WatchlistService>();
builder.Services.AddScoped<ExportService>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Domain errors become clean JSON { error } instead of 500s.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    (int status, string message) = ex switch
    {
        TradeRejectedException e => (400, e.Message),
        FinnhubException { Status: HttpStatusCode.TooManyRequests } e => (429, e.Message),
        FinnhubException e => (503, e.Message),
        BadHttpRequestException e => (400, e.Message),
        _ => (500, "Something went wrong."),
    };
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FolioDb>();
    await db.Database.MigrateAsync();
    await db.GetAccountAsync();

    if (!scope.ServiceProvider.GetRequiredService<FinnhubClient>().IsConfigured)
        app.Logger.LogWarning("Finnhub:ApiKey is not set. Quotes and trading are disabled until you add one (see README).");
}

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", (FinnhubClient f) => new { finnhubConfigured = f.IsConfigured });

api.MapGet("/portfolio", (PortfolioService p, CancellationToken ct) => p.GetAsync(ct));

api.MapPost("/portfolio/reset", async (ResetRequest req, TradingService t, SnapshotService s, CancellationToken ct) =>
{
    await t.ResetAsync(req.StartingCash, ct);
    await s.RecordAsync(ct); // captures the new benchmark baseline right away
    return Results.NoContent();
});

api.MapGet("/trades", async (FolioDb db, int? limit, CancellationToken ct) =>
{
    var trades = await db.Trades.AsNoTracking()
        .OrderByDescending(t => t.ExecutedAt)
        .Take(Math.Clamp(limit ?? 100, 1, 1000))
        .ToListAsync(ct);
    var orderIds = trades.Where(t => t.OrderId != null).Select(t => t.OrderId!.Value).Distinct().ToList();
    var types = await db.Orders.AsNoTracking()
        .Where(o => orderIds.Contains(o.Id))
        .ToDictionaryAsync(o => o.Id, o => o.Type, ct);
    return trades.Select(t => new
    {
        t.Id, t.Symbol, t.Side, t.Quantity, t.Price, t.RealizedPnl, t.ExecutedAt, t.OrderId,
        OrderType = t.OrderId is int id && types.TryGetValue(id, out var type) ? type.ToString() : "Market",
    });
});

api.MapGet("/orders", (string? status, int? limit, OrderService o, CancellationToken ct) =>
    o.ListAsync(status, limit ?? 100, ct));

api.MapPost("/orders", async (PlaceOrderRequest req, OrderService o, SnapshotService s, CancellationToken ct) =>
{
    var order = await o.PlaceAsync(req, ct);
    if (order.Status == OrderStatus.Filled)
        try { await s.RecordAsync(ct); } catch (FinnhubException) { /* best-effort */ }
    return Results.Created($"/api/orders/{order.Id}", order);
});

api.MapDelete("/orders/{id:int}", (int id, OrderService o, CancellationToken ct) => o.CancelAsync(id, ct));

api.MapPost("/trades", async (TradeRequest req, TradingService t, SnapshotService s, CancellationToken ct) =>
{
    var trade = await t.ExecuteAsync(req, ct);
    try { await s.RecordAsync(ct); } catch (FinnhubException) { /* chart point is best-effort */ }
    return Results.Created("/api/trades", trade);
});

api.MapGet("/watchlist", (WatchlistService w, CancellationToken ct) => w.ListAsync(ct));

api.MapPost("/watchlist", async (WatchlistAddRequest req, WatchlistService w, CancellationToken ct) =>
{
    var item = await w.AddAsync(req.Symbol, ct);
    return Results.Created($"/api/watchlist/{item.Symbol}", item);
});

api.MapDelete("/watchlist/{symbol}", async (string symbol, WatchlistService w, CancellationToken ct) =>
    await w.RemoveAsync(symbol, ct)
        ? Results.NoContent()
        : Results.NotFound(new { error = $"{symbol.ToUpperInvariant()} isn't on your watchlist." }));

// CSV downloads. The UTF-8 BOM makes Excel read the file as UTF-8.
var csvFile = (CsvFile f) => Results.File(
    [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(f.Content)],
    "text/csv; charset=utf-8", f.FileName);
api.MapGet("/export/trades.csv", async (ExportService e, CancellationToken ct) => csvFile(await e.TradesAsync(ct)));
api.MapGet("/export/holdings.csv", async (ExportService e, CancellationToken ct) => csvFile(await e.HoldingsAsync(ct)));
api.MapGet("/export/orders.csv", async (ExportService e, CancellationToken ct) => csvFile(await e.OrdersAsync(ct)));

api.MapGet("/history", (string? range, SnapshotService s, CancellationToken ct) => s.GetHistoryAsync(range, ct));

api.MapGet("/quote/{symbol}", async (string symbol, FinnhubClient f, CancellationToken ct) =>
{
    var quote = await f.GetQuoteAsync(TradingService.NormalizeSymbol(symbol), ct);
    return quote is null ? Results.NotFound(new { error = $"No quote for {symbol.ToUpperInvariant()}." }) : Results.Ok(quote);
});

// Name and logo for the order form. Fetched from Finnhub once per symbol, then served from the database.
api.MapGet("/company/{symbol}", async (string symbol, TradingService t, CancellationToken ct) =>
{
    var s = TradingService.NormalizeSymbol(symbol);
    var c = await t.EnsureCompanyAsync(s, ct);
    return new { symbol = s, name = c?.Name, industry = c?.Industry, logo = FinnhubClient.SafeLogo(c?.Logo) };
});

api.MapGet("/search", async (string q, FinnhubClient f, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(q) || q.Length > 30 ? Results.Ok(Array.Empty<SymbolMatch>()) : Results.Ok(await f.SearchAsync(q.Trim(), ct)));

app.Run();

record ResetRequest(decimal StartingCash);
