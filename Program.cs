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

api.MapPost("/portfolio/reset", async (ResetRequest req, TradingService t, CancellationToken ct) =>
{
    await t.ResetAsync(req.StartingCash, ct);
    return Results.NoContent();
});

api.MapGet("/trades", async (FolioDb db, int? limit, CancellationToken ct) =>
    await db.Trades.AsNoTracking()
        .OrderByDescending(t => t.ExecutedAt)
        .Take(Math.Clamp(limit ?? 100, 1, 1000))
        .ToListAsync(ct));

api.MapPost("/trades", async (TradeRequest req, TradingService t, SnapshotService s, CancellationToken ct) =>
{
    var trade = await t.ExecuteAsync(req, ct);
    try { await s.RecordAsync(ct); } catch (FinnhubException) { /* chart point is best-effort */ }
    return Results.Created("/api/trades", trade);
});

api.MapGet("/history", (string? range, SnapshotService s, CancellationToken ct) => s.GetHistoryAsync(range, ct));

api.MapGet("/quote/{symbol}", async (string symbol, FinnhubClient f, CancellationToken ct) =>
{
    var quote = await f.GetQuoteAsync(TradingService.NormalizeSymbol(symbol), ct);
    return quote is null ? Results.NotFound(new { error = $"No quote for {symbol.ToUpperInvariant()}." }) : Results.Ok(quote);
});

api.MapGet("/search", async (string q, FinnhubClient f, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(q) || q.Length > 30 ? Results.Ok(Array.Empty<SymbolMatch>()) : Results.Ok(await f.SearchAsync(q.Trim(), ct)));

app.Run();

record ResetRequest(decimal StartingCash);
