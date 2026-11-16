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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MarketClock>();
builder.Services.AddSingleton(sp => new LivePrices(
    sp.GetRequiredService<TimeProvider>(),
    regularHoursOnly: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FolioOptions>>().Value.EnforceMarketHours));
builder.Services.AddSingleton<IStreamSymbols, DbStreamSymbols>();
builder.Services.AddHostedService<FinnhubStream>();
builder.Services.AddScoped<PortfolioContext>();
builder.Services.AddScoped<PortfoliosService>();
builder.Services.AddScoped<TradingService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddHostedService<SnapshotWorker>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddHostedService<OrderWorker>();
builder.Services.AddScoped<WatchlistService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<TradeHistoryService>();
builder.Services.AddScoped<PerformanceService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddHostedService<AlertWorker>();

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
        PortfolioNotFoundException e => (404, e.Message),
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

// Every API call works on one portfolio: ?portfolio=<id>, or the first portfolio when omitted.
api.AddEndpointFilter(async (ctx, next) =>
{
    var http = ctx.HttpContext;
    var db = http.RequestServices.GetRequiredService<FolioDb>();
    var raw = http.Request.Query["portfolio"].ToString();
    int id;
    if (raw.Length == 0) id = await db.DefaultAccountIdAsync(http.RequestAborted);
    else if (!int.TryParse(raw, out id) || !await db.Accounts.AnyAsync(a => a.Id == id, http.RequestAborted))
        return Results.NotFound(new { error = "That portfolio doesn't exist. It may have been deleted." });
    http.RequestServices.GetRequiredService<PortfolioContext>().Id = id;
    return await next(ctx);
});

api.MapGet("/portfolios", (PortfoliosService p, CancellationToken ct) => p.ListAsync(ct));

api.MapPost("/portfolios", async (CreatePortfolioRequest req, PortfoliosService p, SnapshotService s, CancellationToken ct) =>
{
    var created = await p.CreateAsync(req, ct);
    try { await s.RecordAsync(created.Id, ct); } catch (FinnhubException) { /* benchmark baseline is best-effort */ }
    return Results.Created($"/api/portfolios/{created.Id}", created);
});

api.MapPatch("/portfolios/{id:int}", async (int id, UpdatePortfolioRequest req, PortfoliosService p, CancellationToken ct) =>
{
    await p.UpdateAsync(id, req, ct);
    return Results.NoContent();
});

api.MapDelete("/portfolios/{id:int}", async (int id, PortfoliosService p, CancellationToken ct) =>
{
    await p.DeleteAsync(id, ct);
    return Results.NoContent();
});

api.MapGet("/status", (FinnhubClient f, LivePrices live, Microsoft.Extensions.Options.IOptions<FinnhubOptions> o) =>
    new { finnhubConfigured = f.IsConfigured, streaming = f.IsConfigured && o.Value.Streaming, streamConnected = live.Connected });

api.MapGet("/market", (MarketClock m) => m.Status());

// Live prices for the page, pushed as server-sent events (see LivePrices.StreamAsync).
api.MapGet("/stream", (LivePrices live, CancellationToken ct) => TypedResults.ServerSentEvents(live.StreamAsync(ct)));

api.MapGet("/portfolio", (PortfolioService p, CancellationToken ct) => p.GetAsync(ct));

api.MapPost("/portfolio/reset", async (ResetRequest req, TradingService t, SnapshotService s, CancellationToken ct) =>
{
    await t.ResetAsync(req.StartingCash, ct);
    await s.RecordAsync(ct); // captures the new benchmark baseline right away
    return Results.NoContent();
});

api.MapGet("/trades", (int? page, int? pageSize, TradeHistoryService h, CancellationToken ct) =>
    h.PageAsync(page, pageSize, ct));

api.MapGet("/orders", (string? status, int? limit, OrderService o, CancellationToken ct) =>
    o.ListAsync(status, limit ?? 100, ct));

api.MapPost("/orders", async (PlaceOrderRequest req, OrderService o, SnapshotService s, LivePrices live, CancellationToken ct) =>
{
    var order = await o.PlaceAsync(req, ct);
    live.RequestResync(); // stream the order's symbol so it can trigger on the next tick
    if (order.Status == OrderStatus.Filled)
        try { await s.RecordAsync(ct); } catch (FinnhubException) { /* best-effort */ }
    return Results.Created($"/api/orders/{order.Id}", order);
});

api.MapDelete("/orders/{id:int}", (int id, OrderService o, CancellationToken ct) => o.CancelAsync(id, ct));

api.MapPost("/trades", async (TradeRequest req, TradingService t, SnapshotService s, LivePrices live, CancellationToken ct) =>
{
    var trade = await t.ExecuteAsync(req, ct);
    live.RequestResync();
    try { await s.RecordAsync(ct); } catch (FinnhubException) { /* chart point is best-effort */ }
    return Results.Created("/api/trades", trade);
});

// Stock splits apply to every portfolio, so they ignore ?portfolio.
api.MapGet("/splits", (string? symbol, TradingService t, CancellationToken ct) => t.ListSplitsAsync(symbol, ct));

// No snapshot here: cached quotes may still carry the pre-split price for a few seconds.
api.MapPost("/splits", async (SplitRequest req, OrderService o, CancellationToken ct) =>
{
    var result = await o.ApplySplitAsync(req, ct);
    return Results.Created($"/api/splits/{result.Id}", result);
});

api.MapGet("/watchlist", (WatchlistService w, CancellationToken ct) => w.ListAsync(ct));

api.MapPost("/watchlist", async (WatchlistAddRequest req, WatchlistService w, LivePrices live, CancellationToken ct) =>
{
    var item = await w.AddAsync(req.Symbol, ct);
    live.RequestResync();
    return Results.Created($"/api/watchlist/{item.Symbol}", item);
});

api.MapDelete("/watchlist/{symbol}", async (string symbol, WatchlistService w, CancellationToken ct) =>
    await w.RemoveAsync(symbol, ct)
        ? Results.NoContent()
        : Results.NotFound(new { error = $"{symbol.ToUpperInvariant()} isn't on your watchlist." }));

// Price alerts on watchlist symbols. Shared by every portfolio, so they ignore ?portfolio.
api.MapGet("/alerts", (AlertService a, CancellationToken ct) => a.ListAsync(ct));

api.MapPost("/alerts", async (CreateAlertRequest req, AlertService a, CancellationToken ct) =>
{
    var alert = await a.CreateAsync(req, ct);
    return Results.Created($"/api/alerts/{alert.Id}", alert);
});

api.MapDelete("/alerts/{id:int}", async (int id, AlertService a, CancellationToken ct) =>
    await a.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "That alert doesn't exist any more." }));

api.MapDelete("/alerts/triggered", async (AlertService a, CancellationToken ct) =>
{
    await a.ClearTriggeredAsync(ct);
    return Results.NoContent();
});

api.MapPost("/alerts/seen", async (MarkSeenRequest req, AlertService a, CancellationToken ct) =>
{
    await a.MarkSeenAsync(req.Ids ?? [], ct);
    return Results.NoContent();
});

// CSV downloads. The UTF-8 BOM makes Excel read the file as UTF-8.
var csvFile = (CsvFile f) => Results.File(
    [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(f.Content)],
    "text/csv; charset=utf-8", f.FileName);
api.MapGet("/export/trades.csv", async (ExportService e, CancellationToken ct) => csvFile(await e.TradesAsync(ct)));
api.MapGet("/export/holdings.csv", async (ExportService e, CancellationToken ct) => csvFile(await e.HoldingsAsync(ct)));
api.MapGet("/export/orders.csv", async (ExportService e, CancellationToken ct) => csvFile(await e.OrdersAsync(ct)));

api.MapGet("/performance", (PerformanceService p, CancellationToken ct) => p.GetAsync(ct));

api.MapGet("/history", (string? range, SnapshotService s, CancellationToken ct) => s.GetHistoryAsync(range, ct));

api.MapGet("/quote/{symbol}", async (string symbol, FinnhubClient f, LivePrices live, CancellationToken ct) =>
{
    var sym = TradingService.NormalizeSymbol(symbol);
    var quote = await f.GetQuoteAsync(sym, ct);
    if (quote is not null) live.Touch(sym); // stream it while it's in the order form
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

record MarkSeenRequest(int[]? Ids);
