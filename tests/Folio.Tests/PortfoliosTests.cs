using Folio.Data;
using Folio.Services;
using Microsoft.EntityFrameworkCore;

namespace Folio.Tests;

public class PortfoliosTests
{
    private static async Task<int> Create(TestApp app, string name, decimal cash = 50_000m)
    {
        using var s = app.NewScope();
        return (await s.Portfolios.CreateAsync(new(name, cash))).Id;
    }

    [Fact]
    public async Task Starts_with_one_default_portfolio()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        var p = Assert.Single(await s.Portfolios.ListAsync());
        Assert.Equal("My portfolio", p.Name);
        Assert.Equal(100_000m, p.TotalValue);
    }

    [Fact]
    public async Task Portfolios_keep_separate_cash_positions_trades_and_orders()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Finnhub.Prices["MSFT"] = 400m;
        var growth = await Create(app, "Growth", 10_000m);

        using (var main = app.NewScope()) await main.Buy("AAPL", 10);
        using (var g = app.NewScope(growth))
        {
            await g.Buy("MSFT", 5);
            await g.Place("MSFT", TradeSide.Sell, OrderType.Limit, 1, 500m);
        }

        using var m = app.NewScope();
        var mainView = await m.Portfolio.GetAsync();
        Assert.Equal(99_000m, mainView.Cash);
        Assert.Equal(["AAPL"], mainView.Holdings.Select(h => h.Symbol));
        Assert.Single((await m.History.PageAsync(1, 25)).Items);
        Assert.Empty(await m.Orders.ListAsync("open", 100));

        using var gs = app.NewScope(growth);
        var growthView = await gs.Portfolio.GetAsync();
        Assert.Equal(8_000m, growthView.Cash);
        Assert.Equal(["MSFT"], growthView.Holdings.Select(h => h.Symbol));
        Assert.Single(await gs.Orders.ListAsync("open", 100));
    }

    [Fact]
    public async Task Same_symbol_can_be_held_in_two_portfolios()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other");
        using (var s = app.NewScope()) await s.Buy("AAPL", 1);
        using (var s = app.NewScope(other)) await s.Buy("AAPL", 2);

        using var check = app.NewScope();
        Assert.Equal(2, await check.Db.Positions.CountAsync(p => p.Symbol == "AAPL"));
    }

    [Fact]
    public async Task Cannot_sell_shares_held_in_another_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other");
        using (var s = app.NewScope()) await s.Buy("AAPL", 5);

        using var o = app.NewScope(other);
        await Assert.ThrowsAsync<TradeRejectedException>(() => o.Sell("AAPL", 1));
        await Assert.ThrowsAsync<TradeRejectedException>(() => o.Place("AAPL", TradeSide.Sell, OrderType.Limit, 1, 150m));
    }

    [Fact]
    public async Task Open_orders_commit_cash_only_within_their_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other", 1_000m);
        using (var s = app.NewScope()) await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 900, 90m); // commits $81,000 of main

        using var o = app.NewScope(other);
        var placed = await o.Place("AAPL", TradeSide.Buy, OrderType.Limit, 10, 90m); // $900 of Other's $1,000
        Assert.Equal(OrderStatus.Open, placed.Status);
    }

    [Fact]
    public async Task Order_worker_fills_each_order_into_its_own_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other", 5_000m);
        using (var s = app.NewScope()) await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 2, 95m);
        using (var s = app.NewScope(other)) await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 3, 95m);
        app.Finnhub.Prices["AAPL"] = 94m;

        using (var worker = app.NewScope()) Assert.Equal(2, await worker.Orders.CheckOpenOrdersAsync());

        using var m = app.NewScope();
        Assert.Equal(2m, (await m.Portfolio.GetAsync()).Holdings.Single().Quantity);
        using var o = app.NewScope(other);
        Assert.Equal(3m, (await o.Portfolio.GetAsync()).Holdings.Single().Quantity);
        Assert.Equal(5_000m - 282m, (await o.Portfolio.GetAsync()).Cash);
    }

    [Fact]
    public async Task Cancel_only_finds_orders_in_the_current_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other");
        int id;
        using (var s = app.NewScope()) id = (await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 90m)).Id;

        using var o = app.NewScope(other);
        await Assert.ThrowsAsync<TradeRejectedException>(() => o.Orders.CancelAsync(id));
    }

    [Fact]
    public async Task Reset_only_clears_the_current_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other");
        using (var s = app.NewScope()) await s.Buy("AAPL", 1);
        using (var s = app.NewScope(other)) await s.Buy("AAPL", 1);

        using (var s = app.NewScope(other)) await s.Trading.ResetAsync(20_000m);

        using var m = app.NewScope();
        Assert.Single((await m.Portfolio.GetAsync()).Holdings);
        using var o = app.NewScope(other);
        var view = await o.Portfolio.GetAsync();
        Assert.Empty(view.Holdings);
        Assert.Equal(20_000m, view.Cash);
    }

    [Fact]
    public async Task Snapshots_and_history_are_per_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other", 5_000m);
        using (var s = app.NewScope()) Assert.Equal(2, await s.Snapshots.RecordAllAsync());

        using var o = app.NewScope(other);
        var history = await o.Snapshots.GetHistoryAsync("ALL");
        Assert.Equal(5_000m, history.StartingCash);
        Assert.All(history.Points, p => Assert.Equal(5_000m, p.V));
        Assert.Equal(1, await o.Db.Snapshots.CountAsync(x => x.AccountId == other));
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("This name is far too long to be a portfolio name, really", "40")]
    [InlineData("my PORTFOLIO", "already")]
    public async Task Names_are_validated(string name, string message)
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Portfolios.CreateAsync(new(name, 1_000m)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task Names_are_trimmed_and_inner_spaces_collapsed()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        Assert.Equal("Long term", (await s.Portfolios.CreateAsync(new("  Long   term ", 1_000m))).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(2_000_000_000)]
    public async Task Starting_cash_is_validated(decimal cash)
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Portfolios.CreateAsync(new("X", cash)));
    }

    [Fact]
    public async Task Portfolio_count_is_capped()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        for (var i = 1; i < PortfoliosService.MaxPortfolios; i++) await s.Portfolios.CreateAsync(new($"P{i}", 1_000m));
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Portfolios.CreateAsync(new("One more", 1_000m)));
        Assert.Contains("up to", ex.Message);
    }

    [Fact]
    public async Task Rename_checks_for_duplicates_but_allows_changing_case()
    {
        using var app = new TestApp();
        var other = await Create(app, "Other");
        using var s = app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Portfolios.RenameAsync(other, "My Portfolio"));
        await s.Portfolios.RenameAsync(other, "OTHER");
        Assert.Contains(await s.Portfolios.ListAsync(), p => p.Name == "OTHER");
    }

    [Fact]
    public async Task Delete_removes_the_portfolio_and_everything_in_it()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other");
        using (var s = app.NewScope(other))
        {
            await s.Buy("AAPL", 2);
            await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 90m);
            await s.Snapshots.RecordAsync();
        }
        using (var s = app.NewScope()) await s.Buy("AAPL", 1);

        using (var s = app.NewScope()) await s.Portfolios.DeleteAsync(other);

        using var check = app.NewScope();
        Assert.Single(await check.Portfolios.ListAsync());
        Assert.Equal(0, await check.Db.Positions.CountAsync(p => p.AccountId == other));
        Assert.Equal(0, await check.Db.Trades.CountAsync(t => t.AccountId == other));
        Assert.Equal(0, await check.Db.Orders.CountAsync(o => o.AccountId == other));
        Assert.Equal(0, await check.Db.Snapshots.CountAsync(x => x.AccountId == other));
        Assert.Single((await check.Portfolio.GetAsync()).Holdings); // the other portfolio is untouched
    }

    [Fact]
    public async Task The_only_portfolio_cannot_be_deleted()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Portfolios.DeleteAsync(app.DefaultPortfolioId));
    }

    [Fact]
    public async Task Unknown_portfolio_is_not_found()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        await Assert.ThrowsAsync<PortfolioNotFoundException>(() => s.Portfolios.RenameAsync(999, "X"));
        await Assert.ThrowsAsync<PortfolioNotFoundException>(() => s.Portfolios.DeleteAsync(999));
        using var ghost = app.NewScope(999);
        await Assert.ThrowsAsync<PortfolioNotFoundException>(() => ghost.Portfolio.GetAsync());
    }

    [Fact]
    public async Task Watchlist_is_shared_but_shows_shares_held_in_the_current_portfolio()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        var other = await Create(app, "Other");
        using (var s = app.NewScope()) { await s.Watchlist.AddAsync("AAPL"); await s.Buy("AAPL", 4); }

        using var o = app.NewScope(other);
        var item = Assert.Single(await o.Watchlist.ListAsync());
        Assert.Equal(0m, item.HeldQuantity);
        using var m = app.NewScope();
        Assert.Equal(4m, Assert.Single(await m.Watchlist.ListAsync()).HeldQuantity);
    }

    [Theory]
    [InlineData("Growth", "growth")]
    [InlineData("Long-term  (IRA) 2026!", "long-term-ira-2026")]
    [InlineData("Ünïcödé", "n-c-d")]
    [InlineData("!!!", "")]
    public void Export_file_names_use_a_slug_of_the_portfolio_name(string name, string slug) =>
        Assert.Equal(slug, ExportService.Slug(name));
}
