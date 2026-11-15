using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

namespace Folio.Tests;

public class TradingServiceTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    private async Task<(Account Account, List<Position> Positions, List<Trade> Trades)> State()
    {
        using var s = _app.NewScope();
        return (await s.Db.GetAccountAsync(),
                await s.Db.Positions.ToListAsync(),
                await s.Db.Trades.ToListAsync());
    }

    [Fact]
    public async Task Buy_deducts_cash_and_opens_position()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope())
        {
            var trade = await s.Buy("AAPL", 10);
            Assert.Equal(200m, trade.Price);
            Assert.Null(trade.RealizedPnl);
        }

        var (account, positions, trades) = await State();
        Assert.Equal(98_000m, account.Cash);
        var pos = Assert.Single(positions);
        Assert.Equal(("AAPL", 10m, 200m), (pos.Symbol, pos.Quantity, pos.AvgCost));
        Assert.Single(trades);
    }

    [Fact]
    public async Task Symbol_is_trimmed_and_uppercased()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using var s = _app.NewScope();
        var trade = await s.Buy("  aapl ", 1);
        Assert.Equal("AAPL", trade.Symbol);
    }

    [Fact]
    public async Task Second_buy_uses_weighted_average_cost()
    {
        _app.Finnhub.Prices["MSFT"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("MSFT", 10);
        _app.Finnhub.Prices["MSFT"] = 200m;
        using (var s = _app.NewScope()) await s.Buy("MSFT", 30);

        var (account, positions, _) = await State();
        var pos = Assert.Single(positions);
        Assert.Equal(40m, pos.Quantity);
        Assert.Equal(175m, pos.AvgCost); // (10×100 + 30×200) / 40
        Assert.Equal(100_000m - 1_000m - 6_000m, account.Cash);
    }

    [Fact]
    public async Task Partial_sell_books_realized_gain_and_keeps_avg_cost()
    {
        _app.Finnhub.Prices["NVDA"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("NVDA", 10);
        _app.Finnhub.Prices["NVDA"] = 130m;

        Trade sell;
        using (var s = _app.NewScope()) sell = await s.Sell("NVDA", 4);

        Assert.Equal(120m, sell.RealizedPnl); // (130 − 100) × 4
        var (account, positions, _) = await State();
        Assert.Equal(100_000m - 1_000m + 520m, account.Cash);
        Assert.Equal(120m, account.RealizedPnl);
        var pos = Assert.Single(positions);
        Assert.Equal((6m, 100m), (pos.Quantity, pos.AvgCost));
    }

    [Fact]
    public async Task Selling_everything_closes_position_and_books_loss()
    {
        _app.Finnhub.Prices["TSLA"] = 100m;
        using (var s = _app.NewScope()) await s.Buy("TSLA", 10);
        _app.Finnhub.Prices["TSLA"] = 80m;
        using (var s = _app.NewScope()) await s.Sell("TSLA", 10);

        var (account, positions, trades) = await State();
        Assert.Empty(positions);
        Assert.Equal(-200m, account.RealizedPnl);
        Assert.Equal(99_800m, account.Cash);
        Assert.Equal(2, trades.Count);
    }

    [Fact]
    public async Task Fractional_shares_are_supported()
    {
        _app.Finnhub.Prices["AMZN"] = 150m;
        using (var s = _app.NewScope()) await s.Buy("AMZN", 0.5m);

        var (account, positions, _) = await State();
        Assert.Equal(0.5m, Assert.Single(positions).Quantity);
        Assert.Equal(99_925m, account.Cash);
    }

    [Fact]
    public async Task Order_cost_is_rounded_to_cents()
    {
        _app.Finnhub.Prices["ODD"] = 33.333m;
        using (var s = _app.NewScope()) await s.Buy("ODD", 3); // 99.999

        var (account, _, _) = await State();
        Assert.Equal(99_900m, account.Cash);
    }

    [Fact]
    public async Task Buying_more_than_cash_is_rejected_and_changes_nothing()
    {
        _app.Finnhub.Prices["BRK.A"] = 700_000m;
        using (var s = _app.NewScope())
        {
            var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("BRK.A", 1));
            Assert.Contains("Insufficient cash", ex.Message);
            Assert.Contains("$700,000.00", ex.Message);
        }

        var (account, positions, trades) = await State();
        Assert.Equal(100_000m, account.Cash);
        Assert.Empty(positions);
        Assert.Empty(trades);
    }

    [Fact]
    public async Task Selling_more_than_held_is_rejected()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope()) await s.Buy("AAPL", 5);
        using (var s = _app.NewScope())
        {
            var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Sell("AAPL", 6));
            Assert.Contains("only hold 5", ex.Message);
        }

        var (_, positions, _) = await State();
        Assert.Equal(5m, Assert.Single(positions).Quantity);
    }

    [Fact]
    public async Task Selling_a_symbol_not_held_is_rejected()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using var s = _app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Sell("AAPL", 1));
    }

    [Fact]
    public async Task Unknown_symbol_is_rejected()
    {
        using var s = _app.NewScope();
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("ZZZZ", 1));
        Assert.Contains("No quote found", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.00001)]
    public async Task Invalid_quantity_is_rejected(decimal qty)
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using var s = _app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("AAPL", qty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AA PL")]
    [InlineData("<script>")]
    [InlineData("WAYTOOLONGSYMBOLNAME123")]
    public void Invalid_symbol_is_rejected(string symbol) =>
        Assert.Throws<TradeRejectedException>(() => TradingService.NormalizeSymbol(symbol));

    [Fact]
    public async Task Stale_price_blocks_trading()
    {
        _app.Finnhub.Prices["STALETRADE"] = 50m;
        using (var s = _app.NewScope()) await s.Buy("STALETRADE", 1); // remembers a last-known price

        _app.Finnhub.Failing.Add("STALETRADE");
        using (var s = _app.NewScope())
        {
            var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("STALETRADE", 1));
            Assert.Contains("Live price is unavailable", ex.Message);
        }
    }

    [Fact]
    public async Task Reset_clears_everything_and_sets_new_cash()
    {
        _app.Finnhub.Prices["AAPL"] = 200m;
        using (var s = _app.NewScope())
        {
            await s.Buy("AAPL", 10);
            await s.Snapshots.RecordAsync();
        }
        using (var s = _app.NewScope()) await s.Trading.ResetAsync(25_000m);

        using var check = _app.NewScope();
        var account = await check.Db.GetAccountAsync();
        Assert.Equal((25_000m, 25_000m, 0m), (account.StartingCash, account.Cash, account.RealizedPnl));
        Assert.Null(account.BenchmarkStartPrice);
        Assert.Empty(await check.Db.Positions.ToListAsync());
        Assert.Empty(await check.Db.Trades.ToListAsync());
        Assert.Empty(await check.Db.Snapshots.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(2_000_000_000)]
    public async Task Reset_rejects_out_of_range_cash(decimal cash)
    {
        using var s = _app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Trading.ResetAsync(cash));
    }
}
