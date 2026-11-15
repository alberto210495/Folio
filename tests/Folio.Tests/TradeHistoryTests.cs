using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

public class TradeHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>Adds <paramref name="count"/> trades straight to the ledger; pairs share a timestamp to test tie-breaking.</summary>
    private static void Seed(TestApp app, int count)
    {
        using var s = app.NewScope();
        for (var i = 0; i < count; i++)
            s.Db.Trades.Add(new Trade { Symbol = "AAPL", Side = TradeSide.Buy, Quantity = i + 1, Price = 100m, ExecutedAt = T0.AddMinutes(i / 2) });
        s.Db.SaveChanges();
    }

    private static TradePage Page(TestApp app, int? page, int? size)
    {
        using var s = app.NewScope();
        return new TradeHistoryService(s.Db).PageAsync(page, size).GetAwaiter().GetResult();
    }

    [Fact]
    public void Pages_cover_every_trade_once_newest_first()
    {
        using var app = new TestApp();
        Seed(app, 60);

        var seen = new List<int>();
        for (var p = 1; p <= 3; p++)
        {
            var page = Page(app, p, 25);
            Assert.Equal(60, page.Total);
            Assert.Equal(3, page.Pages);
            Assert.Equal(p, page.Page);
            seen.AddRange(page.Items.Select(t => t.Id));
        }

        Assert.Equal(60, seen.Count);
        Assert.Equal(60, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(id => id), seen); // newest first; equal timestamps by id
        Assert.Equal(10, Page(app, 3, 25).Items.Count);
    }

    [Fact]
    public void Defaults_to_the_first_page_of_25()
    {
        using var app = new TestApp();
        Seed(app, 30);
        var page = Page(app, null, null);
        Assert.Equal(1, page.Page);
        Assert.Equal(TradeHistoryService.DefaultPageSize, page.PageSize);
        Assert.Equal(25, page.Items.Count);
    }

    [Theory]
    [InlineData(99, 3)]  // past the end: last page
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public void Out_of_range_pages_are_clamped(int requested, int expected)
    {
        using var app = new TestApp();
        Seed(app, 60);
        Assert.Equal(expected, Page(app, requested, 25).Page);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5000, TradeHistoryService.MaxPageSize)]
    public void Page_size_is_clamped(int requested, int expected)
    {
        using var app = new TestApp();
        Seed(app, 3);
        Assert.Equal(expected, Page(app, 1, requested).PageSize);
    }

    [Fact]
    public void Empty_history_is_one_empty_page()
    {
        using var app = new TestApp();
        var page = Page(app, 5, 25);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.Pages);
    }

    [Fact]
    public async Task Fills_from_orders_show_their_order_type()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using var s = app.NewScope();
        await s.Buy("AAPL", 5);
        await s.Place("AAPL", TradeSide.Sell, OrderType.Limit, 2, 95m); // marketable: fills now

        var items = (await new TradeHistoryService(s.Db).PageAsync(1, 25)).Items;
        Assert.Equal(["Limit", "Market"], items.Select(t => t.OrderType));
        Assert.NotNull(items[0].OrderId);
        Assert.Null(items[1].OrderId);
    }
}
