using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

namespace Folio.Tests;

public class OrderServiceTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    private async Task<int> CheckAt(string symbol, decimal price)
    {
        _app.Finnhub.Prices[symbol] = price;
        using var s = _app.NewScope();
        return await s.Orders.CheckOpenOrdersAsync();
    }

    private async Task<Order> Reload(int id)
    {
        using var s = _app.NewScope();
        return await s.Db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    [Theory]
    // side, type, trigger, price, expected
    [InlineData(TradeSide.Buy, OrderType.Limit, 100, 99, true)]
    [InlineData(TradeSide.Buy, OrderType.Limit, 100, 100, true)]
    [InlineData(TradeSide.Buy, OrderType.Limit, 100, 101, false)]
    [InlineData(TradeSide.Sell, OrderType.Limit, 100, 101, true)]
    [InlineData(TradeSide.Sell, OrderType.Limit, 100, 99, false)]
    [InlineData(TradeSide.Buy, OrderType.Stop, 100, 101, true)]
    [InlineData(TradeSide.Buy, OrderType.Stop, 100, 99, false)]
    [InlineData(TradeSide.Sell, OrderType.Stop, 100, 99, true)]
    [InlineData(TradeSide.Sell, OrderType.Stop, 100, 101, false)]
    public void Trigger_rules(TradeSide side, OrderType type, decimal trigger, decimal price, bool expected) =>
        Assert.Equal(expected, OrderService.IsTriggered(side, type, trigger, price));

    [Fact]
    public async Task Buy_limit_rests_until_price_drops_then_fills_at_market()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        OrderView placed;
        using (var s = _app.NewScope()) placed = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 10, 190m);
        Assert.Equal(OrderStatus.Open, placed.Status);

        Assert.Equal(0, await CheckAt("AAPL", 195m));
        Assert.Equal(OrderStatus.Open, (await Reload(placed.Id)).Status);

        Assert.Equal(1, await CheckAt("AAPL", 188m)); // better than the limit
        var order = await Reload(placed.Id);
        Assert.Equal((OrderStatus.Filled, 188m), (order.Status, order.FillPrice));

        using var check = _app.NewScope();
        var trade = Assert.Single(await check.Db.Trades.ToListAsync());
        Assert.Equal((placed.Id, 188m, 10m), (trade.OrderId, trade.Price, trade.Quantity));
        Assert.Equal(100_000m - 1_880m, (await check.Db.GetAccountAsync()).Cash);
    }

    [Fact]
    public async Task Marketable_limit_fills_immediately()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using var s = _app.NewScope();
        var placed = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 210m);
        Assert.Equal((OrderStatus.Filled, 200m), (placed.Status, placed.FillPrice));
    }

    [Fact]
    public async Task Sell_stop_triggers_on_drop_and_books_loss()
    {
        _app.Finnhub.Prices["TSLA"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("TSLA", 10);
        OrderView stop;
        using (var s = _app.NewScope()) stop = await s.Place("TSLA", TradeSide.Sell, OrderType.Stop, 10, 90m);

        Assert.Equal(0, await CheckAt("TSLA", 95m));
        Assert.Equal(1, await CheckAt("TSLA", 87m)); // gapped through the stop: fills at market

        using var check = _app.NewScope();
        Assert.Empty(await check.Db.Positions.ToListAsync());
        Assert.Equal(-130m, (await check.Db.GetAccountAsync()).RealizedPnl);
        Assert.Equal(87m, (await Reload(stop.Id)).FillPrice);
    }

    [Fact]
    public async Task Buy_stop_triggers_on_breakout()
    {
        _app.Finnhub.Prices["NVDA"] = 100m;
        OrderView stop;
        using (var s = _app.NewScope()) stop = await s.Place("NVDA", TradeSide.Buy, OrderType.Stop, 5, 110m);

        Assert.Equal(0, await CheckAt("NVDA", 105m));
        Assert.Equal(1, await CheckAt("NVDA", 111m));
        Assert.Equal(OrderStatus.Filled, (await Reload(stop.Id)).Status);
    }

    [Theory]
    [InlineData(TradeSide.Buy, 95)]   // buy stop below market would trigger instantly
    [InlineData(TradeSide.Sell, 105)] // sell stop above market likewise
    public async Task Stop_on_the_wrong_side_of_market_is_rejected(TradeSide side, decimal stop)
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("AAPL", 10);
        using var s2 = _app.NewScope();
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s2.Place("AAPL", side, OrderType.Stop, 1, stop));
        Assert.Contains("Use a market order", ex.Message);
    }

    [Fact]
    public async Task Sell_orders_cannot_exceed_shares_not_already_committed()
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("AAPL", 10);
        using (var s = _app.NewScope()) await s.Place("AAPL", TradeSide.Sell, OrderType.Limit, 6, 150m);

        using var s2 = _app.NewScope();
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s2.Place("AAPL", TradeSide.Sell, OrderType.Stop, 5, 90m));
        Assert.Contains("already in open sell orders", ex.Message);
        await s2.Place("AAPL", TradeSide.Sell, OrderType.Stop, 4, 90m); // exactly the remainder is fine
    }

    [Fact]
    public async Task Buy_orders_cannot_exceed_uncommitted_cash()
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = _app.NewScope()) await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 900, 90m); // $81,000

        using var s2 = _app.NewScope();
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s2.Place("AAPL", TradeSide.Buy, OrderType.Limit, 250, 80m)); // $20,000
        Assert.Contains("already in open buy orders", ex.Message);
    }

    [Fact]
    public async Task Order_is_rejected_at_fill_time_if_cash_was_spent_meanwhile()
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        _app.Finnhub.Prices["MSFT"] = 1_000m;
        OrderView limit;
        using (var s = _app.NewScope()) limit = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 900, 95m);
        using (var s = _app.NewScope()) await s.Buy("MSFT", 50); // market orders don't respect commitments

        Assert.Equal(0, await CheckAt("AAPL", 94m));
        var order = await Reload(limit.Id);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Contains("Insufficient cash", order.Reason);

        using var check = _app.NewScope();
        Assert.DoesNotContain(await check.Db.Positions.ToListAsync(), p => p.Symbol == "AAPL");
    }

    [Fact]
    public async Task Cancelled_order_never_fills()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        OrderView placed;
        using (var s = _app.NewScope()) placed = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 150m);
        using (var s = _app.NewScope())
            Assert.Equal(OrderStatus.Cancelled, (await s.Orders.CancelAsync(placed.Id)).Status);

        Assert.Equal(0, await CheckAt("AAPL", 140m));
        Assert.Equal(OrderStatus.Cancelled, (await Reload(placed.Id)).Status);

        using var s2 = _app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s2.Orders.CancelAsync(placed.Id)); // already cancelled
    }

    [Fact]
    public async Task Stale_price_never_fills_an_order()
    {
        _app.Finnhub.Prices["STALEORD"] = 100m;
        OrderView placed;
        using (var s = _app.NewScope()) placed = await s.Place("STALEORD", TradeSide.Buy, OrderType.Limit, 1, 90m);

        _app.Finnhub.Prices["STALEORD"] = 50m;       // would trigger...
        _app.Finnhub.Failing.Add("STALEORD");        // ...but Finnhub is down, so only the stale $100 is known
        using (var s = _app.NewScope()) Assert.Equal(0, await s.Orders.CheckOpenOrdersAsync());
        Assert.Equal(OrderStatus.Open, (await Reload(placed.Id)).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(10.00001)]
    public async Task Invalid_price_is_rejected(decimal price)
    {
        _app.Finnhub.Prices["AAPL"] = 100m;
        using var s = _app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, price));
    }

    [Fact]
    public async Task List_filters_by_status_and_includes_current_price()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope())
        {
            await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 150m);
            var c = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 140m);
            await s.Orders.CancelAsync(c.Id);
        }

        using var check = _app.NewScope();
        var open = Assert.Single(await check.Orders.ListAsync("open", 100));
        Assert.Equal(200m, open.CurrentPrice);
        Assert.Equal(OrderStatus.Cancelled, Assert.Single(await check.Orders.ListAsync("closed", 100)).Status);
        Assert.Equal(2, (await check.Orders.ListAsync("all", 100)).Count);
    }

    [Fact]
    public async Task Reset_deletes_orders()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope()) await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 150m);
        using (var s = _app.NewScope()) await s.Trading.ResetAsync(100_000m);

        using var check = _app.NewScope();
        Assert.Empty(await check.Db.Orders.ToListAsync());
    }
}
