using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

public class TradingCostTests
{
    private static async Task SetCosts(TestApp app, decimal commission, decimal slippageBps, int? portfolioId = null)
    {
        using var s = app.NewScope();
        await s.Portfolios.UpdateAsync(portfolioId ?? app.DefaultPortfolioId, new(Commission: commission, SlippageBps: slippageBps));
    }

    [Theory]
    [InlineData(TradeSide.Buy, 100, 10, null, 100.10)]
    [InlineData(TradeSide.Sell, 100, 10, null, 99.90)]
    [InlineData(TradeSide.Buy, 100, 0, null, 100)]
    [InlineData(TradeSide.Buy, 99.8, 50, 100.0, 100)]       // a buy limit never fills above its limit
    [InlineData(TradeSide.Sell, 100.2, 50, 100.0, 100)]     // a sell limit never fills below its limit
    [InlineData(TradeSide.Buy, 99, 50, 100.0, 99.495)]      // well inside the limit: slips normally
    [InlineData(TradeSide.Buy, 123.4567, 3, null, 123.4937)] // rounded to 4 decimals
    public void Fill_price_adds_slippage_against_you(TradeSide side, decimal market, decimal bps, double? limit, decimal expected) =>
        Assert.Equal(expected, TradingService.FillPrice(side, market, bps, limit is null ? null : (decimal)limit));

    [Fact]
    public async Task Commission_and_slippage_go_into_cost_basis_and_realized_pnl()
    {
        using var app = new TestApp();
        await SetCosts(app, commission: 5m, slippageBps: 10m);
        app.Finnhub.Prices["AAPL"] = 100m;

        Trade buy;
        using (var s = app.NewScope()) buy = await s.Buy("AAPL", 10);
        Assert.Equal((100.10m, 5m, 1.00m), (buy.Price, buy.Commission, buy.Slippage));

        using (var s = app.NewScope())
        {
            var p = await s.Portfolio.GetAsync();
            Assert.Equal(100_000m - 1_001m - 5m, p.Cash);
            Assert.Equal(100.6m, p.Holdings.Single().AvgCost);   // (1,001 + 5) ÷ 10
        }

        app.Finnhub.Prices["AAPL"] = 110m;
        Trade sell;
        using (var s = app.NewScope()) sell = await s.Sell("AAPL", 10);
        Assert.Equal(109.89m, sell.Price);
        Assert.Equal(1.10m, sell.Slippage);
        Assert.Equal(87.90m, sell.RealizedPnl);                    // (109.89 − 100.60) × 10 − 5

        using var check = app.NewScope();
        var after = await check.Portfolio.GetAsync();
        Assert.Equal(100_087.90m, after.Cash);
        Assert.Equal(87.90m, after.RealizedPnl);
        Assert.Equal(after.TotalReturn, after.RealizedPnl);        // nothing lost between the two
    }

    [Fact]
    public async Task Buy_needs_cash_for_the_commission_too()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        int id;
        using (var s = app.NewScope()) id = (await s.Portfolios.CreateAsync(new("Small", 1_000m, Commission: 10m))).Id;

        using var p = app.NewScope(id);
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => p.Buy("AAPL", 10));
        Assert.Contains("$10.00 commission", ex.Message);
        await p.Buy("AAPL", 9.9m); // $990 + $10 fits exactly
    }

    [Fact]
    public async Task Limit_fill_is_capped_at_the_limit_and_the_order_records_the_real_fill()
    {
        using var app = new TestApp();
        await SetCosts(app, 0m, slippageBps: 50m);
        app.Finnhub.Prices["AAPL"] = 99.8m;

        using (var s = app.NewScope()) await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 10, 100m); // marketable: fills now

        using var check = app.NewScope();
        var trade = check.Db.Trades.Single();
        Assert.Equal(100m, trade.Price);
        Assert.Equal(2.00m, trade.Slippage);
        Assert.Equal(100m, check.Db.Orders.Single().FillPrice);
    }

    [Fact]
    public async Task Stop_fill_slips_past_the_stop()
    {
        using var app = new TestApp();
        await SetCosts(app, 0m, slippageBps: 50m);
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope())
        {
            await s.Buy("AAPL", 10);
            await s.Place("AAPL", TradeSide.Sell, OrderType.Stop, 10, 90m);
        }

        app.Finnhub.Prices["AAPL"] = 89m;
        using (var s = app.NewScope()) Assert.Equal(1, await s.Orders.CheckOpenOrdersAsync());

        using var check = app.NewScope();
        var order = check.Db.Orders.Single();
        Assert.Equal(88.555m, order.FillPrice);   // 89 − 0.5%
    }

    [Fact]
    public async Task Open_buy_orders_count_their_costs_against_cash()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 120m;
        int id;
        using (var s = app.NewScope()) id = (await s.Portfolios.CreateAsync(new("Small", 1_000m, Commission: 10m))).Id;

        using var p = app.NewScope(id);
        await p.Place("AAPL", TradeSide.Buy, OrderType.Limit, 9.9m, 100m);   // $990 + $10
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => p.Place("AAPL", TradeSide.Buy, OrderType.Limit, 0.01m, 100m));
        Assert.Contains("already in open buy orders", ex.Message);
    }

    [Fact]
    public async Task Costs_are_set_per_portfolio_and_listed()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        int costly;
        using (var s = app.NewScope())
        {
            var created = await s.Portfolios.CreateAsync(new("Costly", 10_000m, Commission: 4.95m, SlippageBps: 5m));
            Assert.Equal((4.95m, 5m), (created.Commission, created.SlippageBps));
            costly = created.Id;
        }

        using (var s = app.NewScope()) Assert.Equal(0m, (await s.Buy("AAPL", 1)).Commission); // the default stays free
        using (var s = app.NewScope(costly)) Assert.Equal(4.95m, (await s.Buy("AAPL", 1)).Commission);

        using var check = app.NewScope();
        var list = await check.Portfolios.ListAsync();
        Assert.Equal((0m, 0m), (list[0].Commission, list[0].SlippageBps));
        Assert.Equal((4.95m, 5m), (list[1].Commission, list[1].SlippageBps));
    }

    [Fact]
    public async Task Update_changes_only_the_fields_given()
    {
        using var app = new TestApp();
        using (var s = app.NewScope()) await s.Portfolios.UpdateAsync(app.DefaultPortfolioId, new(Commission: 1m, SlippageBps: 2m));
        using (var s = app.NewScope()) await s.Portfolios.UpdateAsync(app.DefaultPortfolioId, new(Name: "Renamed"));
        using (var s = app.NewScope()) await s.Portfolios.UpdateAsync(app.DefaultPortfolioId, new(SlippageBps: 0m));

        using var check = app.NewScope();
        var a = await check.Db.GetAccountAsync(app.DefaultPortfolioId);
        Assert.Equal(("Renamed", 1m, 0m), (a.Name, a.Commission, a.SlippageBps));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1000.01, 0)]
    [InlineData(1.001, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 500.01)]
    [InlineData(0, 1.001)]
    public async Task Invalid_costs_are_refused(double commission, double bps)
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() =>
            s.Portfolios.UpdateAsync(app.DefaultPortfolioId, new(Commission: (decimal)commission, SlippageBps: (decimal)bps)));
        await Assert.ThrowsAsync<TradeRejectedException>(() =>
            s.Portfolios.CreateAsync(new("X", 1_000m, (decimal)commission, (decimal)bps)));
    }

    [Fact]
    public async Task Reset_keeps_the_cost_settings()
    {
        using var app = new TestApp();
        await SetCosts(app, 2m, 3m);
        using (var s = app.NewScope()) await s.Trading.ResetAsync(5_000m);
        using var check = app.NewScope();
        var a = await check.Db.GetAccountAsync(app.DefaultPortfolioId);
        Assert.Equal((2m, 3m), (a.Commission, a.SlippageBps));
    }

    [Fact]
    public async Task Costs_show_in_trade_history_and_performance()
    {
        using var app = new TestApp();
        await SetCosts(app, 1m, 10m);
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope()) await s.Buy("AAPL", 10);   // slippage $1.00
        using (var s = app.NewScope()) await s.Sell("AAPL", 5);   // slippage $0.50

        using var check = app.NewScope();
        var page = await check.History.PageAsync(1, 25);
        Assert.Equal([(1m, 0.50m), (1m, 1.00m)], page.Items.Select(t => (t.Commission, t.Slippage)));

        var costs = (await check.Performance.GetAsync()).Costs;
        Assert.Equal((2m, 1.50m, 3.50m, 2), (costs.Commission, costs.Slippage, costs.Total, costs.Fills));
    }
}
