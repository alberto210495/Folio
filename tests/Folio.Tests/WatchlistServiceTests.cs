using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

public class WatchlistServiceTests
{
    [Fact]
    public async Task Add_normalizes_symbol_and_returns_live_quote_and_name()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["NVDA"] = 120m;
        using var s = app.NewScope();

        var item = await s.Watchlist.AddAsync("  nvda ");

        Assert.Equal("NVDA", item.Symbol);
        Assert.Equal(120m, item.Price);
        Assert.Equal(1m, item.Change);
        Assert.Equal("NVDA Inc", item.Name);
        Assert.False(item.Stale);
        Assert.Equal(0m, item.HeldQuantity);
    }

    [Fact]
    public async Task Adding_twice_is_idempotent()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["NVDA"] = 120m;
        using (var s = app.NewScope()) await s.Watchlist.AddAsync("NVDA");
        using (var s = app.NewScope()) await s.Watchlist.AddAsync("nvda");

        using var check = app.NewScope();
        Assert.Single(await check.Watchlist.ListAsync());
    }

    [Fact]
    public async Task Unknown_symbol_is_rejected_and_not_saved()
    {
        using var app = new TestApp();
        using var s = app.NewScope();

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Watchlist.AddAsync("ZZZZ"));
        Assert.Contains("ZZZZ", ex.Message);
        Assert.Empty(await s.Watchlist.ListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("A B")]
    [InlineData("<script>")]
    public async Task Invalid_ticker_is_rejected(string symbol)
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Watchlist.AddAsync(symbol));
    }

    [Fact]
    public async Task List_is_capped()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        for (var i = 0; i < WatchlistService.MaxItems; i++)
        {
            app.Finnhub.Prices[$"T{i}"] = 10m + i;
            await s.Watchlist.AddAsync($"T{i}");
        }
        app.Finnhub.Prices["ONEMORE"] = 5m;

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Watchlist.AddAsync("ONEMORE"));
        Assert.Contains("full", ex.Message);
        // Re-adding something already there still works at the cap.
        Assert.Equal("T0", (await s.Watchlist.AddAsync("T0")).Symbol);
    }

    [Fact]
    public async Task List_keeps_insertion_order_and_shows_held_shares()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["MSFT"] = 400m;
        app.Finnhub.Prices["AAPL"] = 200m;
        using var s = app.NewScope();
        await s.Watchlist.AddAsync("MSFT");
        await Task.Delay(5);
        await s.Watchlist.AddAsync("AAPL");
        await s.Buy("AAPL", 3);

        using var check = app.NewScope();
        var list = await check.Watchlist.ListAsync();

        Assert.Equal(["MSFT", "AAPL"], list.Select(w => w.Symbol));
        Assert.Equal(3m, list[1].HeldQuantity);
        Assert.Equal(0m, list[0].HeldQuantity);
    }

    [Fact]
    public async Task Failed_quote_without_history_lists_with_null_price()
    {
        using var app = new TestApp();
        // Saved in an earlier run, but Finnhub is failing and this process has never seen a price for it.
        using (var s = app.NewScope())
        {
            s.Db.Watchlist.Add(new WatchlistItem { Symbol = "NOQUOTE" });
            await s.Db.SaveChangesAsync();
        }
        app.Finnhub.Failing.Add("NOQUOTE");

        using var check = app.NewScope();
        var item = (await check.Watchlist.ListAsync()).Single(w => w.Symbol == "NOQUOTE");
        Assert.Null(item.Price);
        Assert.Null(item.Change);
        Assert.True(item.Stale);
    }

    [Fact]
    public async Task Remove_deletes_and_reports_missing()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["NVDA"] = 120m;
        using var s = app.NewScope();
        await s.Watchlist.AddAsync("NVDA");

        Assert.True(await s.Watchlist.RemoveAsync("nvda"));
        Assert.False(await s.Watchlist.RemoveAsync("NVDA"));
        Assert.Empty(await s.Watchlist.ListAsync());
    }

    [Fact]
    public async Task Account_reset_keeps_the_watchlist()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["NVDA"] = 120m;
        using var s = app.NewScope();
        await s.Watchlist.AddAsync("NVDA");
        await s.Buy("NVDA", 1);

        await s.Trading.ResetAsync(50_000m);

        using var check = app.NewScope();
        var item = Assert.Single(await check.Watchlist.ListAsync());
        Assert.Equal(0m, item.HeldQuantity);
    }
}
