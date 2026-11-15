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
    public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.TooManyRequests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var symbol = HttpUtility.ParseQueryString(request.RequestUri.Query)["symbol"] ?? "";

        if (Failing.Contains(symbol))
            return Task.FromResult(new HttpResponseMessage(FailureStatus));

        object body = path switch
        {
            _ when path.EndsWith("/quote") => Prices.TryGetValue(symbol, out var p)
                ? new { c = p, d = 1m, dp = 0.5m, pc = p - 1m, t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                : new { c = 0m, d = (decimal?)null, dp = (decimal?)null, pc = 0m, t = 0L },
            _ when path.EndsWith("/stock/profile2") => new { name = $"{symbol} Inc", finnhubIndustry = "Technology", logo = "" },
            _ => new { result = Array.Empty<object>() },
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
    }
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

    public TestApp()
    {
        _conn.Open();
        _dbOptions = new DbContextOptionsBuilder<FolioDb>().UseSqlite(_conn).Options;
        using var db = new FolioDb(_dbOptions);
        db.Database.Migrate();
        Finnhub.Prices["SPY"] = 500m;
    }

    public Scope NewScope() => new(this);

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

        internal Scope(TestApp app)
        {
            Db = new FolioDb(app._dbOptions);
            var http = new HttpClient(app.Finnhub) { BaseAddress = new Uri("https://finnhub.test/api/v1/") };
            Finnhub = new FinnhubClient(
                http,
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new FinnhubOptions { ApiKey = "test" }),
                NullLogger<FinnhubClient>.Instance);
            Trading = new TradingService(Db, Finnhub, NullLogger<TradingService>.Instance);
            Portfolio = new PortfolioService(Db, Finnhub);
            Snapshots = new SnapshotService(Db, Portfolio, Finnhub, Options.Create(new FolioOptions()));
            Orders = new OrderService(Db, Trading, Finnhub, NullLogger<OrderService>.Instance);
            Watchlist = new WatchlistService(Db, Finnhub, Trading);
        }

        public Task<OrderView> Place(string symbol, TradeSide side, OrderType type, decimal qty, decimal price) =>
            Orders.PlaceAsync(new(symbol, side, type, qty, price));

        public Task<Trade> Buy(string symbol, decimal qty) => Trading.ExecuteAsync(new(symbol, TradeSide.Buy, qty));
        public Task<Trade> Sell(string symbol, decimal qty) => Trading.ExecuteAsync(new(symbol, TradeSide.Sell, qty));

        public void Dispose() => Db.Dispose();
    }
}
