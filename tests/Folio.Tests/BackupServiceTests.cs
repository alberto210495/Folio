using System.Text.Json;
using System.Text.Json.Serialization;
using Folio.Data;
using Folio.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Folio.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Folio";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"folio-backup-test-{Guid.NewGuid():N}");
    private readonly TestApp _app = new();

    public BackupServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _app.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private BackupService Backups(TestApp.Scope s, int keep = 14) => new(
        s.Db, Options.Create(new FolioOptions { BackupKeep = keep }), new Env(_root), _app.Time, NullLogger<BackupService>.Instance);

    /// <summary>Two portfolios with holdings, an order-linked fill, a cancelled order, history, a watchlist, an alert and a split.</summary>
    private async Task SeedAsync()
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        _app.Finnhub.Prices["MSFT"] = 400m;
        int growth;
        using (var s = _app.NewScope())
        {
            growth = (await s.Portfolios.CreateAsync(new("Growth", 20_000m, Commission: 1m))).Id;
            await s.Buy("AAPL", 10);
            await s.Place("AAPL", TradeSide.Sell, OrderType.Limit, 2, 95m); // marketable: fills, linked to its order
            var cancelled = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 50m);
            await s.Orders.CancelAsync(cancelled.Id);
            await s.Snapshots.RecordAllAsync();
            await s.Watchlist.AddAsync("MSFT");
            s.Db.Alerts.Add(new PriceAlert { Symbol = "MSFT", Direction = AlertDirection.Above, Price = 450m });
            s.Db.Splits.Add(new StockSplit { Symbol = "AAPL", From = 1, To = 4 });
            await s.Db.SaveChangesAsync();
        }
        using (var g = _app.NewScope(growth)) await g.Buy("MSFT", 5);
    }

    private static FolioArchive RoundTrip(FolioArchive a) =>
        JsonSerializer.Deserialize<FolioArchive>(JsonSerializer.Serialize(a, Json), Json)!;

    [Fact]
    public async Task Backup_is_a_complete_readable_copy_of_the_database()
    {
        await SeedAsync();
        using var s = _app.NewScope();
        var info = await Backups(s).CreateAsync();

        Assert.Matches(@"^folio-\d{8}-\d{6}\.db$", info.Name);
        using var copy = new SqliteConnection($"Data Source={Path.Combine(_root, "backups", info.Name)};Pooling=False");
        copy.Open();
        using var cmd = copy.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Trades";
        Assert.Equal((long)await s.Db.Trades.CountAsync(), (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Keeps_only_the_newest_backups()
    {
        using var s = _app.NewScope();
        var backups = Backups(s, keep: 3);
        for (var i = 0; i < 5; i++)
        {
            await backups.CreateAsync();
            _app.Time.Now = _app.Time.Now.AddMinutes(1);
        }

        var list = backups.List().Backups;
        Assert.Equal(3, list.Count);
        Assert.Equal(list.OrderByDescending(b => b.Name, StringComparer.Ordinal).Select(b => b.Name), list.Select(b => b.Name));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "backups")).Length);
    }

    [Fact]
    public async Task Listing_ignores_files_folio_did_not_write()
    {
        using var s = _app.NewScope();
        var backups = Backups(s);
        await backups.CreateAsync();
        File.WriteAllText(Path.Combine(_root, "backups", "folio-evil.db"), "x");
        File.WriteAllText(Path.Combine(_root, "backups", "notes.txt"), "x");
        Assert.Single(backups.List().Backups);
    }

    [Fact]
    public async Task Export_then_import_restores_everything()
    {
        await SeedAsync();
        FolioArchive exported;
        using (var s = _app.NewScope()) exported = RoundTrip(await Backups(s).ExportAsync());

        // Change everything, then restore.
        using (var s = _app.NewScope())
        {
            await s.Trading.ResetAsync(1m);
            await s.Portfolios.CreateAsync(new("Scratch", 5m));
        }
        using (var s = _app.NewScope())
        {
            var result = await Backups(s).ImportAsync(exported);
            Assert.Equal(2, result.Portfolios);
            Assert.Matches("before-import", result.BackupName);
        }

        using var check = _app.NewScope();
        var again = RoundTrip(await Backups(check).ExportAsync());
        Assert.Equal(JsonSerializer.Serialize(exported with { ExportedAt = default }, Json), JsonSerializer.Serialize(again with { ExportedAt = default }, Json));
        var linked = await check.Db.Trades.SingleAsync(t => t.OrderId != null);
        Assert.True(await check.Db.Orders.AnyAsync(o => o.Id == linked.OrderId && o.Status == OrderStatus.Filled));
        Assert.Equal(1m, (await check.Db.Accounts.SingleAsync(a => a.Name == "Growth")).Commission);
    }

    [Fact]
    public async Task Import_takes_a_backup_first_so_it_can_be_undone()
    {
        await SeedAsync();
        using var s = _app.NewScope();
        var backups = Backups(s);
        var archive = RoundTrip(await backups.ExportAsync());
        await backups.ImportAsync(archive with { Portfolios = [archive.Portfolios[0]], Positions = [], Trades = [], Orders = [], Snapshots = [] });

        var before = Assert.Single(backups.List().Backups, b => b.Name.EndsWith("-before-import.db"));
        using var copy = new SqliteConnection($"Data Source={Path.Combine(_root, "backups", before.Name)};Pooling=False");
        copy.Open();
        using var cmd = copy.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Accounts";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task History_stays_in_time_order_and_capped_when_imported_snapshots_predate_the_portfolio()
    {
        using var s = _app.NewScope();
        var backups = Backups(s);
        var archive = RoundTrip(await backups.ExportAsync());
        var pid = archive.Portfolios[0].Id;
        var t0 = archive.Portfolios[0].CreatedAt.AddDays(-30);
        var older = Enumerable.Range(1, 1000).Select(i => new PortfolioSnapshot
        {
            Id = i, AccountId = pid, TakenAt = t0.AddMinutes(30 * i), TotalValue = 100_000m + i, Cash = 1m, HoldingsValue = 1m,
        }).ToList();
        await backups.ImportAsync(archive with { Snapshots = older });

        using var check = _app.NewScope();
        var points = (await check.Snapshots.GetHistoryAsync("ALL")).Points;
        Assert.True(points.Count <= 400, $"{points.Count} points");
        Assert.Equal(points.OrderBy(p => p.T).Select(p => p.T), points.Select(p => p.T));
    }

    public static TheoryData<string, Func<FolioArchive, FolioArchive>> BadArchives => new()
    {
        { "version", a => a with { Version = 99 } },
        { "1 to 20 portfolios", a => a with { Portfolios = [] } },
        { "duplicate portfolio name", a => a with { Portfolios = [.. a.Portfolios, Clone(a.Portfolios[0], p => { p.Id = 99; p.Name = p.Name.ToUpperInvariant(); })] } },
        { "isn't in the file", a => a with { Trades = [.. a.Trades, Clone(a.Trades[0], t => { t.Id = 999; t.AccountId = 12345; t.OrderId = null; })] } },
        { "invalid symbol", a => a with { Watchlist = [new WatchlistItem { Symbol = "DROP TABLE" }] } },
        { "unknown side", a => a with { Trades = [Clone(a.Trades[0], t => t.Side = (TradeSide)2)] } },
        { "no shares", a => a with { Positions = [Clone(a.Positions[0], p => p.Quantity = -1)] } },
        { "order that isn't in its portfolio", a => a with { Trades = [Clone(a.Trades.First(t => t.OrderId != null), t => t.OrderId = 4242)] } },
        { "commission", a => a with { Portfolios = [Clone(a.Portfolios[0], p => p.Commission = 5_000m), .. a.Portfolios.Skip(1)] } },
        { "duplicate trade id", a => a with { Trades = [.. a.Trades, a.Trades[0]] } },
        { "future", a => a with { Snapshots = [.. a.Snapshots, Clone(a.Snapshots[0], x => { x.Id = 9999; x.TakenAt = DateTime.UtcNow.AddYears(1); })] } },
    };

    private static T Clone<T>(T item, Action<T> change)
    {
        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item, Json), Json)!;
        change(copy);
        return copy;
    }

    [Theory]
    [MemberData(nameof(BadArchives))]
    public async Task Invalid_imports_are_rejected_without_touching_data(string message, Func<FolioArchive, FolioArchive> corrupt)
    {
        await SeedAsync();
        using var s = _app.NewScope();
        var backups = Backups(s);
        var good = RoundTrip(await backups.ExportAsync());
        var tradesBefore = await s.Db.Trades.CountAsync();

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => backups.ImportAsync(corrupt(good)));
        Assert.Contains(message, ex.Message);
        Assert.Equal(tradesBefore, await s.Db.Trades.CountAsync());
        Assert.Empty(backups.List().Backups); // rejected before the safety backup, so nothing was written
    }
}
