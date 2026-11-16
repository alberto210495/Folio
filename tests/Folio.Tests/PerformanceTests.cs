using Folio.Services;
using static Folio.Services.PerformanceService;

namespace Folio.Tests;

public class PerformanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);
    private static ValuePoint P(int day, decimal v) => new(T0.AddDays(day), v);

    [Fact]
    public void Trade_stats_count_wins_losses_and_breakeven()
    {
        // Each sell: quantity, price, realized P&L. Cost = qty × price − P&L.
        var stats = PerformanceService.Trades([
            new(10, 110m, 100m),   // cost 1000, +10%
            new(5, 230m, 150m),    // cost 1000, +15%
            new(10, 95m, -50m),    // cost 1000, −5%
            new(1, 100m, 0m),      // breakeven
        ]);

        Assert.Equal(4, stats.Closed);
        Assert.Equal((2, 1, 1), (stats.Wins, stats.Losses, stats.Breakeven));
        Assert.Equal(66.67m, stats.WinRate);      // breakeven isn't counted
        Assert.Equal(125m, stats.AvgWin);
        Assert.Equal(-50m, stats.AvgLoss);
        Assert.Equal(12.5m, stats.AvgWinPercent);
        Assert.Equal(-5m, stats.AvgLossPercent);
        Assert.Equal(150m, stats.LargestWin);
        Assert.Equal(-50m, stats.LargestLoss);
        Assert.Equal(5m, stats.ProfitFactor);     // 250 / 50
        Assert.Equal(50m, stats.Expectancy);      // 200 / 4
        Assert.Equal((250m, 50m), (stats.GrossProfit, stats.GrossLoss));
    }

    [Fact]
    public void Trade_stats_with_no_sells_or_no_losses_leave_ratios_empty()
    {
        var none = PerformanceService.Trades([]);
        Assert.Equal(0, none.Closed);
        Assert.Null(none.WinRate);
        Assert.Null(none.AvgWin);
        Assert.Null(none.Expectancy);
        Assert.Null(none.ProfitFactor);

        var allWins = PerformanceService.Trades([new(1, 110m, 10m)]);
        Assert.Equal(100m, allWins.WinRate);
        Assert.Null(allWins.ProfitFactor); // no losses to divide by
        Assert.Null(allWins.AvgLoss);
    }

    [Fact]
    public void Drawdown_finds_the_deepest_fall_and_its_recovery()
    {
        var d = PerformanceService.Drawdown([
            P(0, 100_000m),
            P(1, 110_000m),  // peak
            P(2, 99_000m),   // −10%
            P(3, 88_000m),   // −20%: trough
            P(4, 105_000m),
            P(5, 111_000m),  // recovered, new high
            P(6, 105_450m),  // −5% now
        ], liveIncluded: true);

        Assert.Equal(20m, d.MaxPercent);
        Assert.Equal(22_000m, d.MaxAmount);
        Assert.Equal(T0.AddDays(1), d.PeakAt);
        Assert.Equal(T0.AddDays(3), d.TroughAt);
        Assert.Equal(T0.AddDays(5), d.RecoveredAt);
        Assert.Equal(111_000m, d.HighWater);
        Assert.Equal(T0.AddDays(5), d.HighWaterAt);
        Assert.Equal(5m, d.CurrentPercent);
        Assert.Equal(5_550m, d.CurrentAmount);
        Assert.Equal(7, d.Points);
        Assert.Equal(T0, d.Since);
    }

    [Fact]
    public void Drawdown_not_yet_recovered_has_no_recovery_date()
    {
        var d = PerformanceService.Drawdown([P(0, 100m), P(1, 80m), P(2, 95m)], liveIncluded: false);
        Assert.Equal(20m, d.MaxPercent);
        Assert.Null(d.RecoveredAt);
        Assert.Equal(5m, d.CurrentPercent);
        Assert.False(d.LiveIncluded);
    }

    [Fact]
    public void A_later_deeper_fall_replaces_an_earlier_recovered_one()
    {
        var d = PerformanceService.Drawdown([P(0, 100m), P(1, 90m), P(2, 120m), P(3, 84m)], true);
        Assert.Equal(30m, d.MaxPercent);   // 120 → 84
        Assert.Equal(T0.AddDays(2), d.PeakAt);
        Assert.Null(d.RecoveredAt);
    }

    [Fact]
    public void Only_rising_value_has_no_drawdown()
    {
        var d = PerformanceService.Drawdown([P(0, 100m), P(1, 101m), P(2, 105m)], true);
        Assert.Equal(0m, d.MaxPercent);
        Assert.Equal(0m, d.CurrentPercent);
        Assert.Null(d.PeakAt);
        Assert.Null(d.TroughAt);
    }

    [Fact]
    public async Task Portfolio_stats_come_from_its_own_sells_and_value_history()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        // A new scope after each price change: quotes are cached per scope.
        using (var s = app.NewScope())
        {
            await s.Buy("AAPL", 100);             // $10,000
            await s.Snapshots.RecordAsync();      // $100,000
        }

        app.Finnhub.Prices["AAPL"] = 120m;
        app.Time.Now = app.Time.Now.AddHours(1);
        using (var s = app.NewScope())
        {
            await s.Snapshots.RecordAsync();      // $102,000, the high
            await s.Sell("AAPL", 50);             // +$1,000
        }

        app.Finnhub.Prices["AAPL"] = 90m;
        app.Time.Now = app.Time.Now.AddHours(1);
        int other;
        using (var s = app.NewScope())
        {
            await s.Snapshots.RecordAsync();      // $100,500
            await s.Sell("AAPL", 10);             // −$100
            // A second portfolio's trades don't count.
            other = (await s.Portfolios.CreateAsync(new("Other", 10_000m))).Id;
        }
        using (var o = app.NewScope(other)) { await o.Buy("AAPL", 10); await o.Sell("AAPL", 10); }

        using var check = app.NewScope();
        var perf = await check.Performance.GetAsync();
        Assert.Equal(2, perf.Trades.Closed);
        Assert.Equal(50m, perf.Trades.WinRate);
        Assert.Equal(1_000m, perf.Trades.AvgWin);
        Assert.Equal(20m, perf.Trades.AvgWinPercent);
        Assert.Equal(-100m, perf.Trades.AvgLoss);
        Assert.Equal(10m, perf.Trades.ProfitFactor);

        // Start $100,000 → high $102,000 → $100,500 (snapshot) and $100,500 live (the sell doesn't change value).
        Assert.True(perf.Drawdown.LiveIncluded);
        Assert.Equal(102_000m, perf.Drawdown.HighWater);
        Assert.Equal(1_500m, perf.Drawdown.MaxAmount);
        Assert.Equal(1.47m, perf.Drawdown.MaxPercent);
        Assert.Equal(1.47m, perf.Drawdown.CurrentPercent);
        Assert.Null(perf.Drawdown.RecoveredAt);
    }

    [Fact]
    public async Task Stale_prices_leave_the_live_value_out_of_the_drawdown()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope()) await s.Buy("AAPL", 10);
        app.Finnhub.Failing.Add("AAPL");

        using var later = app.NewScope();
        var perf = await later.Performance.GetAsync();
        Assert.False(perf.Drawdown.LiveIncluded);
        Assert.Equal(1, perf.Drawdown.Points); // just the opening balance
    }

    [Fact]
    public async Task New_portfolio_has_empty_stats()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        var perf = await s.Performance.GetAsync();
        Assert.Equal(0, perf.Trades.Closed);
        Assert.Null(perf.Trades.WinRate);
        Assert.Equal(0m, perf.Drawdown.MaxPercent);
        Assert.Equal(100_000m, perf.Drawdown.HighWater);
    }
}
