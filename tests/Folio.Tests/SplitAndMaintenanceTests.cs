using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

namespace Folio.Tests;

public class SideValidationTests
{
    [Fact]
    public async Task Market_order_with_an_unknown_side_is_refused()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using var s = app.NewScope();
        await s.Buy("AAPL", 5);

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Trading.ExecuteAsync(new("AAPL", (TradeSide)2, 1)));
        Assert.Equal("Side must be Buy or Sell.", ex.Message);
        Assert.Equal(5m, s.Db.Positions.Single().Quantity); // not treated as a sell
    }

    [Fact]
    public async Task Resting_order_with_an_unknown_side_is_refused_and_not_saved()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using var s = app.NewScope();

        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Place("AAPL", (TradeSide)7, OrderType.Limit, 1, 90m));
        Assert.Empty(s.Db.Orders);
    }
}

public class StockSplitTests
{
    [Fact]
    public async Task Forward_split_multiplies_shares_and_divides_cost_in_every_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 200m;
        int second;
        using (var s = app.NewScope())
        {
            await s.Buy("AAPL", 3);
            second = (await s.Portfolios.CreateAsync(new("Second", 10_000m))).Id;
        }
        using (var s = app.NewScope(second)) await s.Buy("AAPL", 1.5m);

        SplitResult result;
        using (var s = app.NewScope()) result = await s.Orders.ApplySplitAsync(new("aapl", 1, 4));

        Assert.Equal(2, result.Positions);
        Assert.Equal(0m, result.CashInLieu);
        using var check = app.NewScope();
        var first = check.Db.Positions.Single(p => p.AccountId == app.DefaultPortfolioId);
        Assert.Equal(12m, first.Quantity);
        Assert.Equal(50m, first.AvgCost);
        var other = check.Db.Positions.Single(p => p.AccountId == second);
        Assert.Equal(6m, other.Quantity);
        Assert.Equal(50m, other.AvgCost);

        // Cost basis is unchanged, and with the post-split price there's no fake loss.
        app.Finnhub.Prices["AAPL"] = 50m;
        using var after = app.NewScope();
        var h = (await after.Portfolio.GetAsync()).Holdings.Single();
        Assert.Equal(600m, h.CostBasis);
        Assert.Equal(0m, h.UnrealizedPnl);
    }

    [Fact]
    public async Task Reverse_split_pays_cash_for_fractions_too_small_to_keep()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["XYZ"] = 1m;
        using (var s = app.NewScope()) await s.Buy("XYZ", 10.0007m);
        decimal cashBefore;
        using (var s = app.NewScope()) cashBefore = (await s.Db.GetAccountAsync(app.DefaultPortfolioId)).Cash;

        SplitResult result;
        using (var s = app.NewScope()) result = await s.Orders.ApplySplitAsync(new("XYZ", 10, 1));

        using var check = app.NewScope();
        var p = check.Db.Positions.Single();
        Assert.Equal(1.0000m, p.Quantity); // 1.00007 kept to 4 decimals
        Assert.Equal(10m, decimal.Round(p.AvgCost, 6));
        // 0.00007 shares × $10 = $0.0007, which rounds to $0.00.
        Assert.Equal(0m, result.CashInLieu);
        Assert.Equal(cashBefore, (await check.Db.GetAccountAsync(app.DefaultPortfolioId)).Cash);
        Assert.Equal(0m, (await check.Db.GetAccountAsync(app.DefaultPortfolioId)).RealizedPnl);
    }

    [Fact]
    public async Task Open_orders_are_adjusted_and_ones_left_with_nothing_are_cancelled()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = app.NewScope())
        {
            await s.Buy("AAPL", 10);
            await s.Place("AAPL", TradeSide.Sell, OrderType.Limit, 2, 250m);
            await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 0.0001m, 150m);
        }

        SplitResult result;
        using (var s = app.NewScope()) result = await s.Orders.ApplySplitAsync(new("AAPL", 3, 1)); // 1-for-3 reverse split

        Assert.Equal(1, result.Orders);
        Assert.Equal(1, result.OrdersCancelled);
        using var check = app.NewScope();
        var orders = check.Db.Orders.OrderBy(o => o.Id).ToList();
        Assert.Equal(0.6666m, orders[0].Quantity);
        Assert.Equal(750m, orders[0].Price);
        Assert.Equal(OrderStatus.Open, orders[0].Status);
        Assert.Equal(OrderStatus.Cancelled, orders[1].Status);
        Assert.Contains("split", orders[1].Reason);
    }

    [Fact]
    public async Task Split_is_recorded_and_listed()
    {
        using var app = new TestApp();
        using (var s = app.NewScope())
        {
            await s.Orders.ApplySplitAsync(new("NVDA", 1, 10));
            await s.Orders.ApplySplitAsync(new("AAPL", 1, 4));
        }

        using var check = app.NewScope();
        var nvda = Assert.Single(await check.Trading.ListSplitsAsync("nvda"));
        Assert.Equal((1, 10), (nvda.From, nvda.To));
        Assert.Equal(app.Time.Now.UtcDateTime, nvda.AppliedAt);
        Assert.Equal(2, (await check.Trading.ListSplitsAsync(null)).Count);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(1, 1001)]
    public async Task Invalid_ratios_are_refused(int from, int to)
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Orders.ApplySplitAsync(new("AAPL", from, to)));
        Assert.Empty(s.Db.Splits);
    }
}

public class ClockAndCompanyRefreshTests
{
    [Fact]
    public async Task Trades_orders_and_snapshots_are_stamped_with_the_app_clock()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using var s = app.NewScope();
        await s.Buy("AAPL", 1);
        await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 50m);
        await s.Snapshots.RecordAsync();

        var now = app.Time.Now.UtcDateTime;
        Assert.Equal(now, s.Db.Trades.Single().ExecutedAt);
        Assert.Equal(now, s.Db.Positions.Single().OpenedAt);
        Assert.Equal(now, s.Db.Orders.Single().CreatedAt);
        Assert.Equal(now, s.Db.Snapshots.Single().TakenAt);
    }

    [Fact]
    public async Task History_range_is_measured_from_the_app_clock()
    {
        using var app = new TestApp();
        using (var s = app.NewScope()) await s.Snapshots.RecordAsync();

        app.Time.Now = app.Time.Now.AddDays(2);
        using var later = app.NewScope();
        // The snapshot is two days old by the app's clock, so it's outside 1D but inside 1W.
        Assert.DoesNotContain((await later.Snapshots.GetHistoryAsync("1D")).Points, p => p.V == 100_000m && p.T == app.Time.Now.UtcDateTime.AddDays(-2));
        Assert.Contains((await later.Snapshots.GetHistoryAsync("1W")).Points, p => p.T == app.Time.Now.UtcDateTime.AddDays(-2));
    }

    [Fact]
    public async Task Company_profile_is_refetched_once_it_is_old()
    {
        using var app = new TestApp();
        app.Finnhub.Logos["AAPL"] = "https://cdn.test/old.png";
        using (var s = app.NewScope()) await s.Trading.EnsureCompanyAsync("AAPL", default);

        app.Finnhub.Logos["AAPL"] = "https://cdn.test/new.png";
        app.Time.Now = app.Time.Now.AddDays(29);
        using (var s = app.NewScope())
            Assert.Equal("https://cdn.test/old.png", (await s.Trading.EnsureCompanyAsync("AAPL", default))?.Logo);
        Assert.Equal(1, app.Finnhub.ProfileCalls);

        app.Time.Now = app.Time.Now.AddDays(2);
        using (var s = app.NewScope())
            Assert.Equal("https://cdn.test/new.png", (await s.Trading.EnsureCompanyAsync("AAPL", default))?.Logo);
        using (var s = app.NewScope())
            Assert.Equal(app.Time.Now.UtcDateTime, s.Db.Companies.Single().FetchedAt);
        Assert.Equal(2, app.Finnhub.ProfileCalls);
    }

    [Fact]
    public async Task Failed_refresh_keeps_the_cached_profile()
    {
        using var app = new TestApp();
        using (var s = app.NewScope()) await s.Trading.EnsureCompanyAsync("AAPL", default);

        app.Time.Now = app.Time.Now.AddDays(40);
        app.Finnhub.Failing.Add("AAPL");
        using (var s = app.NewScope())
            Assert.Equal("AAPL Inc", (await s.Trading.EnsureCompanyAsync("AAPL", default))?.Name);
    }

    [Fact]
    public async Task Background_refresh_only_touches_held_or_watched_symbols()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Finnhub.Prices["MSFT"] = 100m;
        using (var s = app.NewScope())
        {
            await s.Buy("AAPL", 1);
            await s.Watchlist.AddAsync("MSFT");
            await s.Trading.EnsureCompanyAsync("OLD", default); // looked up once, never held
        }
        var calls = app.Finnhub.ProfileCalls;

        app.Time.Now = app.Time.Now.AddDays(31);
        using (var s = app.NewScope())
            Assert.Equal(2, await s.Trading.RefreshStaleCompaniesAsync(5));
        Assert.Equal(calls + 2, app.Finnhub.ProfileCalls);

        using var check = app.NewScope();
        var oldFetched = check.Db.Companies.AsNoTracking().Single(c => c.Symbol == "OLD").FetchedAt;
        Assert.True(app.Time.Now.UtcDateTime - oldFetched > TradingService.CompanyRefreshAfter);
    }
}
