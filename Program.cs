using System.Net;
using System.Text.Json.Serialization;
using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

// "Folio --decrypt-backup <file.enc> [output.db]" turns an encrypted off-PC backup copy back into a normal backup.
if (args is ["--decrypt-backup", ..]) return DecryptBackupCommand(args);

var builder = WebApplication.CreateBuilder(args);

// Your Finnhub key lives in user secrets on this PC; read them in every environment (the installed copy runs
// as Production) except the test suite's, so tests never use your real key.
// On Windows the key is kept encrypted (Windows data protection) and decrypted into memory here; a plain-text key
// found in user secrets is encrypted on the spot (see ApiKeyStore).
string? apiKeyNote = null;
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
    apiKeyNote = ApiKeyStore.ProtectAndLoad(builder.Configuration, ApiKeyStore.SecretsPath());
}

// The installed copy keeps its data outside the program folder (Folio:DataDirectory, e.g. %LOCALAPPDATA%\Folio),
// so reinstalling never touches it. Without it, folio.db sits next to the app as before.
var dataDir = builder.Configuration["Folio:DataDirectory"];
if (!string.IsNullOrWhiteSpace(dataDir))
{
    dataDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDir));
    Directory.CreateDirectory(dataDir);
    builder.Configuration["ConnectionStrings:Folio"] = $"Data Source={Path.Combine(dataDir, "folio.db")}";
    if (builder.Configuration["Folio:BackupDirectory"] is null or "backups")
        builder.Configuration["Folio:BackupDirectory"] = Path.Combine(dataDir, "backups");
}

// A log file for the installed copy, which has no console: Folio:LogDirectory, or "logs" in the data folder.
var logDir = builder.Configuration["Folio:LogDirectory"];
if (string.IsNullOrWhiteSpace(logDir) && !string.IsNullOrWhiteSpace(dataDir)) logDir = Path.Combine(dataDir, "logs");
if (!string.IsNullOrWhiteSpace(logDir))
    builder.Logging.AddProvider(new FileLoggerProvider(Path.GetFullPath(Environment.ExpandEnvironmentVariables(logDir)), TimeProvider.System));

// One Folio per database. Starting it again (the desktop shortcut) just opens the running one in the browser.
var openBrowser = args.Contains("--open");
var siteUrl = builder.Configuration["Kestrel:Endpoints:Https:Url"] ?? "https://localhost:7245";
var dbPath = Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Folio") ?? "Data Source=folio.db").DataSource, builder.Environment.ContentRootPath);
var instanceName = "Local\\Folio-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(dbPath.ToLowerInvariant())))[..16];
using var instance = new Mutex(true, instanceName, out var firstInstance);
// Local access key (see AccessKey): on by default for the installed copy (it has a data folder), off for development.
var contentRoot = builder.Environment.ContentRootPath;
var requireKey = builder.Configuration.GetValue<bool?>("Folio:RequireAccessKey") ?? !string.IsNullOrWhiteSpace(dataDir);
var keyFile = builder.Configuration["Folio:AccessKeyFile"] is { Length: > 0 } configuredKeyFile
    ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredKeyFile), contentRoot)
    : Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? contentRoot : dataDir, "access.key");

if (!firstInstance)
{
    if (openBrowser) OpenBrowser(requireKey && AccessKey.TryLoad(keyFile) is { } existing ? existing.UnlockUrl(siteUrl) : siteUrl);
    return 0;
}
var accessKey = requireKey ? AccessKey.LoadOrCreate(keyFile) : null;

// Small attack surface: no server banner, small request bodies (the largest real request is a few hundred bytes).
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

builder.Services.AddDbContext<FolioDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Folio") ?? "Data Source=folio.db"));

// Shut down within 10 s (the tray waits 15 s before ending Folio forcibly).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

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
    regularHoursOnly: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FolioOptions>>().Value.EnforceMarketHours,
    log: sp.GetRequiredService<ILogger<LivePrices>>()));
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
builder.Services.AddScoped<BackupService>();
// Logos are fetched by Folio, never by your browser: https on finnhub.io only (checked in LogoService), no redirects.
builder.Services.AddHttpClient(LogoService.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.MaxResponseContentBufferSize = LogoService.MaxBytes;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<LogoService>();
builder.Services.AddHostedService<BackupWorker>();
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
        BadHttpRequestException e => (e.StatusCode, e.Message), // e.g. 413 for an oversized body
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

// Security (see README): headers on every response, HTTPS only, and no API calls from other websites.
// Kestrel listens on localhost only and AllowedHosts rejects other host names (DNS rebinding); both in appsettings.json.
app.UseSecurityHeaders();
app.UseHttpsRedirection();
if (accessKey is not null) app.UseAccessKey(accessKey);
app.UseCrossSiteGuard();

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

// Backups and full export/import (see BackupService).
api.MapGet("/backups", (BackupService b) => b.List());

api.MapPost("/backups", async (BackupService b, CancellationToken ct) => Results.Created("/api/backups", await b.CreateAsync("manual", ct)));

api.MapPost("/backups/passphrase", async (PassphraseRequest req, BackupService b, CancellationToken ct) => Results.Ok(await b.SetPassphraseAsync(req.Passphrase, ct)));

// Company logos, served from Folio's cache (see LogoService).
api.MapGet("/logo/{symbol}", async (string symbol, LogoService logos, HttpContext http, CancellationToken ct) =>
{
    if (await logos.GetAsync(symbol, ct) is not { } logo) return Results.NotFound();
    http.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(logo.Bytes, logo.ContentType);
});

api.MapGet("/export/folio.json", async (BackupService b, Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, TimeProvider time, CancellationToken ct) =>
{
    var archive = await b.ExportAsync(ct);
    var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(archive, json.Value.SerializerOptions);
    return Results.File(bytes, "application/json", $"folio-export-{time.GetUtcNow():yyyy-MM-dd}.json");
});

api.MapPost("/import", async (FolioArchive archive, BackupService b, LivePrices live, CancellationToken ct) =>
{
    var result = await b.ImportAsync(archive, ct);
    live.RequestResync();
    return Results.Ok(result);
})
// The one endpoint allowed a big body: everything else stays under the 64 KB limit.
.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(BackupService.MaxImportBytes));

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
api.MapGet("/stream", (HttpContext http, LivePrices live, IHostApplicationLifetime lifetime) =>
{
    // End the stream when Folio shuts down, not only when the browser leaves: an open stream would
    // otherwise hold up a clean shutdown until the timeout.
    var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
    http.Response.RegisterForDispose(stop);
    return TypedResults.ServerSentEvents(live.StreamAsync(stop.Token));
});

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

await app.StartAsync();
app.Logger.LogInformation("Folio started on {Url} (database {Database})", siteUrl, dbPath);
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Folio is stopping"));

// The tray icon asks for a clean shutdown by setting this per-process signal (Windows only; there's no console
// to press Ctrl+C in). Anything in your Windows session could set it, which at worst stops Folio.
RegisteredWaitHandle? stopWait = null;
if (OperatingSystem.IsWindows())
{
    var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\Folio-Stop-{Environment.ProcessId}");
    stopWait = ThreadPool.RegisterWaitForSingleObject(stopSignal, (_, _) => app.Lifetime.StopApplication(), null, Timeout.Infinite, executeOnlyOnce: true);
    app.Lifetime.ApplicationStopped.Register(() => { stopWait.Unregister(null); stopSignal.Dispose(); });
}

if (apiKeyNote is not null)
    app.Logger.Log(apiKeyNote.Contains("couldn't", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information, "{Note}", apiKeyNote);
if (openBrowser) OpenBrowser(accessKey?.UnlockUrl(siteUrl) ?? siteUrl);
await app.WaitForShutdownAsync();
return 0;

static int DecryptBackupCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: Folio --decrypt-backup <backup.db.enc> [output.db]");
        return 2;
    }
    var input = Path.GetFullPath(args[1]);
    var output = args.Length > 2 ? Path.GetFullPath(args[2])
        : input.EndsWith(BackupEncryption.Extension, StringComparison.OrdinalIgnoreCase) ? input[..^BackupEncryption.Extension.Length] : input + ".db";
    if (!File.Exists(input)) { Console.Error.WriteLine($"Not found: {input}"); return 2; }
    if (File.Exists(output)) { Console.Error.WriteLine($"Won't overwrite existing file: {output}"); return 2; }

    Console.Write("Passphrase: ");
    var passphrase = new System.Text.StringBuilder();
    if (Console.IsInputRedirected) passphrase.Append(Console.ReadLine());
    else
    {
        for (var k = Console.ReadKey(intercept: true); k.Key != ConsoleKey.Enter; k = Console.ReadKey(intercept: true))
        {
            if (k.Key == ConsoleKey.Backspace) { if (passphrase.Length > 0) passphrase.Length--; }
            else if (!char.IsControl(k.KeyChar)) passphrase.Append(k.KeyChar);
        }
    }
    Console.WriteLine();
    try
    {
        BackupService.DecryptFile(input, output, passphrase.ToString());
        Console.WriteLine($"Decrypted to {output}");
        Console.WriteLine("To restore it: stop Folio, copy it over folio.db (delete folio.db-wal and folio.db-shm), start Folio.");
        return 0;
    }
    catch (System.Security.Cryptography.CryptographicException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static void OpenBrowser(string url)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no browser registered */ }
}

record ResetRequest(decimal StartingCash);

record MarkSeenRequest(int[]? Ids);

/// <summary>Lets the integration tests host the app with WebApplicationFactory.</summary>
public partial class Program;
