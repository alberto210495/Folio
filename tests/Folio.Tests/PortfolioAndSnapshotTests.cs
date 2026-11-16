using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

namespace Folio.Tests;

public class PortfolioServiceTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Values_holdings_at_live_price()
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("AAPL", 10);
        _app.Finnhub.Prices["AAPL"] = 150m;

        using var check = _app.NewScope();
        var p = await check.Portfolio.GetAsync();

        var h = Assert.Single(p.Holdings);
        Assert.Equal((1_500m, 500m, 50m), (h.MarketValue, h.UnrealizedPnl, h.UnrealizedPnlPercent));
        Assert.Equal("Technology", h.Sector); // from the cached company profile
        Assert.Equal(99_000m, p.Cash);
        Assert.Equal(100_500m, p.TotalValue);
        Assert.Equal((500m, 0.5m), (p.TotalReturn, p.TotalReturnPercent));
        Assert.Equal(10m, p.DayChange); // fake quote: +$1/share today
        Assert.False(p.AnyStale);
    }

    [Fact]
    public async Task Holding_with_no_quote_is_valued_at_cost_and_flagged()
    {
        using (var s = _app.NewScope())
        {
            s.Db.Positions.Add(new Position { AccountId = _app.DefaultPortfolioId, Symbol = "NOQUOTE1", Quantity = 4, AvgCost = 25m });
            await s.Db.SaveChangesAsync();
        }
        _app.Finnhub.Failing.Add("NOQUOTE1");

        using var check = _app.NewScope();
        var p = await check.Portfolio.GetAsync();

        var h = Assert.Single(p.Holdings);
        Assert.True(h.PriceStale);
        Assert.Equal((25m, 100m, 0m), (h.Price, h.MarketValue, h.UnrealizedPnl));
        Assert.True(p.AnyStale);
    }
}

public class SnapshotServiceTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Record_saves_value_and_benchmark_and_sets_baseline()
    {
        using (var s = _app.NewScope()) Assert.True(await s.Snapshots.RecordAsync());

        using var check = _app.NewScope();
        var snap = Assert.Single(await check.Db.Snapshots.ToListAsync());
        Assert.Equal((100_000m, 500m), (snap.TotalValue, snap.BenchmarkPrice));
        Assert.Equal(500m, (await check.Db.GetAccountAsync()).BenchmarkStartPrice);
    }

    [Fact]
    public async Task Record_skips_when_nothing_changed()
    {
        using (var s = _app.NewScope()) await s.Snapshots.RecordAsync();
        using (var s = _app.NewScope()) Assert.False(await s.Snapshots.RecordAsync());

        using var check = _app.NewScope();
        Assert.Single(await check.Db.Snapshots.ToListAsync());
    }

    [Fact]
    public async Task Record_saves_when_only_the_benchmark_moved()
    {
        using (var s = _app.NewScope()) await s.Snapshots.RecordAsync();
        _app.Finnhub.Prices["SPY"] = 510m;
        using (var s = _app.NewScope()) Assert.True(await s.Snapshots.RecordAsync());
    }

    [Fact]
    public async Task Record_skips_when_a_holding_price_is_unavailable()
    {
        using (var s = _app.NewScope())
        {
            s.Db.Positions.Add(new Position { AccountId = _app.DefaultPortfolioId, Symbol = "NOQUOTE2", Quantity = 1, AvgCost = 10m });
            await s.Db.SaveChangesAsync();
        }
        _app.Finnhub.Failing.Add("NOQUOTE2");

        using var s2 = _app.NewScope();
        Assert.False(await s2.Snapshots.RecordAsync());
        Assert.Empty(await s2.Db.Snapshots.ToListAsync());
    }

    [Fact]
    public async Task Record_still_saves_when_benchmark_is_unavailable_without_a_baseline()
    {
        _app.Finnhub.Failing.Add("SPY");
        using (var s = _app.NewScope()) Assert.True(await s.Snapshots.RecordAsync());

        using var check = _app.NewScope();
        Assert.Null(Assert.Single(await check.Db.Snapshots.ToListAsync()).BenchmarkPrice);
        Assert.Null((await check.Db.GetAccountAsync()).BenchmarkStartPrice);
    }

    [Fact]
    public async Task History_scales_benchmark_to_starting_cash()
    {
        using (var s = _app.NewScope()) await s.Snapshots.RecordAsync();   // SPY 500 = baseline
        _app.Finnhub.Prices["SPY"] = 550m;                                   // +10%
        using (var s = _app.NewScope()) await s.Snapshots.RecordAsync();

        using var check = _app.NewScope();
        var h = await check.Snapshots.GetHistoryAsync("ALL");

        Assert.Equal("SPY", h.BenchmarkSymbol);
        Assert.Equal(110_000m, h.BenchmarkNow);
        Assert.Equal(100_000m, h.Points[0].B);  // account-start anchor
        Assert.Equal(110_000m, h.Points[^1].B);
        Assert.All(h.Points, p => Assert.Equal(100_000m, p.V));
    }

    [Fact]
    public void Benchmark_value_is_null_without_a_baseline_or_price()
    {
        Assert.Null(SnapshotService.BenchmarkValue(100_000m, null, 500m));
        Assert.Null(SnapshotService.BenchmarkValue(100_000m, 500m, null));
        Assert.Null(SnapshotService.BenchmarkValue(100_000m, 0m, 500m));
        Assert.Equal(90_000m, SnapshotService.BenchmarkValue(100_000m, 500m, 450m));
    }

    [Fact]
    public void Downsample_caps_points_and_keeps_both_ends()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 5_000)
            .Select(i => new HistoryPoint(start.AddMinutes(i * 15), i, null))
            .ToList();

        var result = SnapshotService.Downsample(points, 400);

        Assert.InRange(result.Count, 2, 400);
        Assert.Equal(points[0], result[0]);
        Assert.Equal(points[^1], result[^1]);
        Assert.Equal(result.OrderBy(p => p.T), result); // still in time order
    }

    [Fact]
    public void Unknown_range_is_rejected() =>
        Assert.Throws<TradeRejectedException>(() => SnapshotService.RangeStart("5Y", DateTime.UtcNow));
}
