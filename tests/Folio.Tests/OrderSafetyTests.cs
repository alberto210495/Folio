using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

namespace Folio.Tests;

/// <summary>
/// A trade that went through is never reported as failed, Finnhub trouble of any kind falls back cleanly,
/// and an order sent twice with the same ID is placed once.
/// </summary>
public class OrderSafetyTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    private async Task<(int Trades, decimal Cash, decimal Shares)> Account(string symbol)
    {
        using var s = _app.NewScope();
        var trades = await s.Db.Trades.CountAsync();
        var cash = (await s.Db.GetAccountAsync()).Cash;
        var shares = await s.Db.Positions.Where(p => p.Symbol == symbol).Select(p => p.Quantity).SingleOrDefaultAsync();
        return (trades, cash, shares);
    }

    // ---------- Finnhub failures ----------

    [Theory]
    [InlineData(FakeFinnhub.Fault.Timeout)]
    [InlineData(FakeFinnhub.Fault.BadJson)]
    public async Task Quote_failure_becomes_a_finnhub_error(FakeFinnhub.Fault fault)
    {
        // The last-known-price cache is shared by every test, so these use symbols no other test quotes.
        var symbol = $"NEVER{(int)fault}";
        _app.Finnhub.QuoteFaults[symbol] = fault;
        using var s = _app.NewScope();
        await Assert.ThrowsAsync<FinnhubException>(() => s.Finnhub.GetQuoteAsync(symbol));
    }

    [Theory]
    [InlineData(FakeFinnhub.Fault.Timeout)]
    [InlineData(FakeFinnhub.Fault.BadJson)]
    public async Task Quote_failure_serves_the_last_known_price_as_stale(FakeFinnhub.Fault fault)
    {
        var symbol = $"LASTKNOWN{(int)fault}";
        _app.Finnhub.Prices[symbol] = 123.45m;
        using (var s = _app.NewScope()) Assert.False((await s.Finnhub.GetQuoteAsync(symbol))!.Stale);

        _app.Finnhub.QuoteFaults[symbol] = fault;
        using var later = _app.NewScope();
        var quote = await later.Finnhub.GetQuoteAsync(symbol);
        Assert.Equal((123.45m, true), (quote!.Price, quote.Stale));
    }

    [Fact]
    public async Task Callers_own_cancellation_is_not_dressed_up_as_a_finnhub_error()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using var s = _app.NewScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Finnhub.GetQuoteAsync("AAPL", cts.Token));
    }

    [Theory]
    [InlineData(FakeFinnhub.Fault.Timeout)]
    [InlineData(FakeFinnhub.Fault.BadJson)]
    public async Task Buy_succeeds_when_the_company_lookup_afterwards_fails(FakeFinnhub.Fault fault)
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        _app.Finnhub.ProfileFaults["AAPL"] = fault;

        Trade trade;
        using (var s = _app.NewScope()) trade = await s.Buy("AAPL", 10);

        Assert.Equal((TradeSide.Buy, 10m, 200m), (trade.Side, trade.Quantity, trade.Price));
        Assert.Equal((1, 98_000m, 10m), await Account("AAPL"));
        using var check = _app.NewScope();
        Assert.Empty(await check.Db.Companies.ToListAsync()); // looked up again later
    }

    [Fact]
    public async Task Resting_order_fills_when_the_company_lookup_afterwards_fails()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        OrderView placed;
        using (var s = _app.NewScope()) placed = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 10, 190m);

        _app.Finnhub.ProfileFaults["AAPL"] = FakeFinnhub.Fault.Timeout;
        _app.Finnhub.Prices["AAPL"] = 189m;
        using (var s = _app.NewScope()) Assert.Equal(1, await s.Orders.CheckOpenOrdersAsync());

        using var check = _app.NewScope();
        var order = await check.Db.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.Id);
        Assert.Equal((OrderStatus.Filled, 189m), (order.Status, order.FillPrice));
    }

    [Fact]
    public async Task One_symbol_timing_out_doesnt_stop_other_orders_filling()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        _app.Finnhub.Prices["MSFT"] = 400m;
        OrderView aapl, msft;
        using (var s = _app.NewScope())
        {
            aapl = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 190m);
            msft = await s.Place("MSFT", TradeSide.Buy, OrderType.Limit, 1, 390m);
        }

        _app.Finnhub.QuoteFaults["AAPL"] = FakeFinnhub.Fault.Timeout;
        _app.Finnhub.Prices["MSFT"] = 385m;
        using (var s = _app.NewScope()) Assert.Equal(1, await s.Orders.CheckOpenOrdersAsync());

        using var check = _app.NewScope();
        var statuses = await check.Db.Orders.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Status);
        Assert.Equal(OrderStatus.Open, statuses[aapl.Id]);
        Assert.Equal(OrderStatus.Filled, statuses[msft.Id]);
    }

    // ---------- Order IDs ----------

    [Fact]
    public async Task Same_order_id_twice_buys_once()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        Trade first, again;
        using (var s = _app.NewScope()) first = await s.Buy("AAPL", 10, "order-1");
        _app.Finnhub.Prices["AAPL"] = 210m; // a retry later on doesn't trade at the new price either
        using (var s = _app.NewScope()) again = await s.Buy("AAPL", 10, "order-1");

        Assert.Equal((first.Id, 200m), (again.Id, again.Price));
        Assert.Equal((1, 98_000m, 10m), await Account("AAPL"));
    }

    [Fact]
    public async Task Retry_gets_its_trade_back_even_after_the_market_closes()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        Trade first;
        using (var s = _app.NewScope()) first = await s.Buy("AAPL", 10, "order-1");

        _app.Time.Now = _app.Time.Now.AddHours(8); // 6 PM ET
        using var later = _app.NewScope();
        Assert.Equal(first.Id, (await later.Buy("AAPL", 10, "order-1")).Id);
        await Assert.ThrowsAsync<TradeRejectedException>(() => later.Buy("AAPL", 10, "order-2"));
    }

    [Fact]
    public async Task Different_order_ids_are_separate_trades()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope())
        {
            await s.Buy("AAPL", 10, "order-1");
            await s.Buy("AAPL", 10, "order-2");
            await s.Buy("AAPL", 10); // no ID: always a new trade
            await s.Buy("AAPL", 10);
        }
        Assert.Equal((4, 92_000m, 40m), await Account("AAPL"));
    }

    [Fact]
    public async Task Reusing_an_order_id_for_a_different_order_is_refused()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        _app.Finnhub.Prices["MSFT"] = 400m;
        using var s = _app.NewScope();
        await s.Buy("AAPL", 10, "order-1");

        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("AAPL", 5, "order-1"));
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("MSFT", 10, "order-1"));
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Trading.ExecuteAsync(new("AAPL", TradeSide.Sell, 10, "order-1")));
        Assert.Equal((1, 98_000m, 10m), await Account("AAPL"));
    }

    [Fact]
    public async Task A_refused_order_can_be_retried_with_the_same_id()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope())
            await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("AAPL", 1_000, "order-1")); // $200k, too much
        using (var s = _app.NewScope()) await s.Trading.ResetAsync(500_000m);
        using (var s = _app.NewScope()) await s.Buy("AAPL", 1_000, "order-1");
        Assert.Equal((1, 300_000m, 1_000m), await Account("AAPL"));
    }

    [Fact]
    public async Task Concurrent_copies_of_one_order_buy_once()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        var scopes = Enumerable.Range(0, 5).Select(_ => _app.NewScope()).ToList();
        try
        {
            var trades = await Task.WhenAll(scopes.Select(s => s.Buy("AAPL", 10, "order-1")));
            Assert.Single(trades.Select(t => t.Id).Distinct());
        }
        finally
        {
            scopes.ForEach(s => s.Dispose());
        }
        Assert.Equal((1, 98_000m, 10m), await Account("AAPL"));
    }

    [Fact]
    public async Task Order_ids_are_per_portfolio()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        int other;
        using (var s = _app.NewScope()) other = (await s.Portfolios.CreateAsync(new("Second", 50_000m))).Id;

        using (var s = _app.NewScope()) await s.Buy("AAPL", 10, "order-1");
        using (var s = _app.NewScope(other)) await s.Buy("AAPL", 10, "order-1");

        using var check = _app.NewScope();
        Assert.Equal(2, await check.Db.Trades.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")] // 65 characters
    public async Task Invalid_order_ids_are_refused(string id)
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using var s = _app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("AAPL", 1, id));
        await Assert.ThrowsAsync<TradeRejectedException>(() =>
            s.Orders.PlaceAsync(new("AAPL", TradeSide.Buy, OrderType.Limit, 1, 190m, id)));
    }

    [Fact]
    public async Task Same_order_id_twice_places_one_resting_order()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        OrderView first, again;
        using (var s = _app.NewScope()) first = await s.Orders.PlaceAsync(new("AAPL", TradeSide.Buy, OrderType.Limit, 10, 190m, "order-1"));
        _app.Finnhub.Failing.Add("AAPL"); // even with Finnhub down, the retry gets its order back
        using (var s = _app.NewScope()) again = await s.Orders.PlaceAsync(new("AAPL", TradeSide.Buy, OrderType.Limit, 10, 190m, "order-1"));

        Assert.Equal((first.Id, OrderStatus.Open), (again.Id, again.Status));
        using var check = _app.NewScope();
        Assert.Equal(1, await check.Db.Orders.CountAsync());

        await Assert.ThrowsAsync<TradeRejectedException>(() =>
            check.Orders.PlaceAsync(new("AAPL", TradeSide.Buy, OrderType.Limit, 10, 180m, "order-1")));
    }

    [Fact]
    public async Task Retry_of_a_limit_that_filled_immediately_reports_the_fill_once()
    {
        _app.Finnhub.Prices["AAPL"] = 180m;
        OrderView first, again;
        using (var s = _app.NewScope()) first = await s.Orders.PlaceAsync(new("AAPL", TradeSide.Buy, OrderType.Limit, 10, 190m, "order-1"));
        using (var s = _app.NewScope()) again = await s.Orders.PlaceAsync(new("AAPL", TradeSide.Buy, OrderType.Limit, 10, 190m, "order-1"));

        Assert.Equal(OrderStatus.Filled, first.Status);
        Assert.Equal((first.Id, OrderStatus.Filled, 180m), (again.Id, again.Status, again.FillPrice));
        Assert.Equal((1, 98_200m, 10m), await Account("AAPL"));
    }
}
