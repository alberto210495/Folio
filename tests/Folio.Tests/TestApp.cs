using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Folio.Data;
using Folio.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Folio.Tests;

/// <summary>Stands in for Finnhub. Prices are mutable; symbols in <see cref="Failing"/> return HTTP errors.</summary>
public sealed class FakeFinnhub : HttpMessageHandler
{
    public Dictionary<string, decimal> Prices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Logo URL returned by /stock/profile2; empty when not set.</summary>
    public Dictionary<string, string> Logos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ProfileCalls { get; private set; }
    public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.TooManyRequests;

    public enum Fault { Timeout, BadJson }
    /// <summary>Symbols whose /quote times out (like HttpClient's timeout) or answers with something that isn't JSON.</summary>
    public Dictionary<string, Fault> QuoteFaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The same for /stock/profile2.</summary>
    public Dictionary<string, Fault> ProfileFaults { get; } = new(StringComparer.OrdinalIgnoreCase);

    private object Profile(string symbol)
    {
        ProfileCalls++;
        return new { name = $"{symbol} Inc", finnhubIndustry = "Technology", logo = Logos.GetValueOrDefault(symbol, "") };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var symbol = HttpUtility.ParseQueryString(request.RequestUri.Query)["symbol"] ?? "";

        if (Failing.Contains(symbol))
            return Task.FromResult(new HttpResponseMessage(FailureStatus));

        var faults = path.EndsWith("/quote") ? QuoteFaults : path.EndsWith("/stock/profile2") ? ProfileFaults : null;
        if (faults is not null && faults.TryGetValue(symbol, out var fault))
            return fault == Fault.Timeout
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request timed out.", new TimeoutException()))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "application/json"),
                });

        object body = path switch
        {
            _ when path.EndsWith("/quote") => Prices.TryGetValue(symbol, out var p)
                ? new { c = p, d = 1m, dp = 0.5m, pc = p - 1m, t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                : new { c = 0m, d = (decimal?)null, dp = (decimal?)null, pc = 0m, t = 0L },
            _ when path.EndsWith("/stock/profile2") => Profile(symbol),
            _ => new { result = Array.Empty<object>() },
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>A settable clock. Defaults to a Wednesday at 10:00 AM ET, when the market is open.</summary>
public sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// One in-memory SQLite database (real migrations applied) shared by every <see cref="Scope"/>.
/// Each scope gets a fresh DbContext and quote cache, so price changes are seen immediately.
/// </summary>
public sealed class TestApp : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<FolioDb> _dbOptions;
    public FakeFinnhub Finnhub { get; } = new();
    public FakeTime Time { get; } = new();
    public LivePrices Live { get; }
    public FolioOptions Settings { get; } = new();

    public TestApp()
    {
        Live = new LivePrices(Time);
        _conn.Open();
        _dbOptions = new DbContextOptionsBuilder<FolioDb>().UseSqlite(_conn).Options;
        using var db = new FolioDb(_dbOptions);
        db.Database.Migrate();
        DefaultPortfolioId = db.DefaultAccountIdAsync().GetAwaiter().GetResult();
        Finnhub.Prices["SPY"] = 500m;
    }

    /// <summary>The "My portfolio" account created at startup.</summary>
    public int DefaultPortfolioId { get; }

    /// <summary>A scope working on <paramref name="portfolioId"/> (the default portfolio when omitted).</summary>
    public Scope NewScope(int? portfolioId = null) => new(this, portfolioId ?? DefaultPortfolioId);

    public void Dispose() => _conn.Dispose();

    public sealed class Scope : IDisposable
    {
        public FolioDb Db { get; }
        public FinnhubClient Finnhub { get; }
        public TradingService Trading { get; }
        public PortfolioService Portfolio { get; }
        public SnapshotService Snapshots { get; }
        public OrderService Orders { get; }
        public WatchlistService Watchlist { get; }
        public PortfolioContext Context { get; } = new();
        public TradeHistoryService History { get; }
        public ExportService Export { get; }
        public PortfoliosService Portfolios { get; }
        public PerformanceService Performance { get; }
        public AlertService Alerts { get; }

        internal Scope(TestApp app, int portfolioId)
        {
            Context.Id = portfolioId;
            Db = new FolioDb(app._dbOptions);
            var http = new HttpClient(app.Finnhub) { BaseAddress = new Uri("https://finnhub.test/api/v1/") };
            Finnhub = new FinnhubClient(
                http,
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new FinnhubOptions { ApiKey = "test" }),
                app.Live,
                NullLogger<FinnhubClient>.Instance);
            var clock = new MarketClock(app.Time, Options.Create(app.Settings));
            Trading = new TradingService(Db, Finnhub, clock, Context, app.Time, NullLogger<TradingService>.Instance);
            Portfolio = new PortfolioService(Db, Finnhub, Context, app.Time);
            Snapshots = new SnapshotService(Db, Portfolio, Finnhub, Context, Options.Create(new FolioOptions()), app.Time);
            Orders = new OrderService(Db, Trading, Finnhub, clock, Context, app.Time, NullLogger<OrderService>.Instance);
            Watchlist = new WatchlistService(Db, Finnhub, Trading, Context, app.Time);
            History = new TradeHistoryService(Db, Context);
            Export = new ExportService(Db, Portfolio, Context, app.Time);
            Portfolios = new PortfoliosService(Db, Portfolio, app.Time);
            Performance = new PerformanceService(Db, Portfolio, Context, app.Time);
            Alerts = new AlertService(Db, Finnhub, clock, app.Time, NullLogger<AlertService>.Instance);
        }

        public Task<OrderView> Place(string symbol, TradeSide side, OrderType type, decimal qty, decimal price) =>
            Orders.PlaceAsync(new(symbol, side, type, qty, price));

        public Task<Trade> Buy(string symbol, decimal qty, string? clientOrderId = null) =>
            Trading.ExecuteAsync(new(symbol, TradeSide.Buy, qty, clientOrderId));
        public Task<Trade> Sell(string symbol, decimal qty) => Trading.ExecuteAsync(new(symbol, TradeSide.Sell, qty));

        public void Dispose() => Db.Dispose();
    }
}
