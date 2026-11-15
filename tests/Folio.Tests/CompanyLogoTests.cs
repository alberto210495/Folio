using Folio.Services;

namespace Folio.Tests;

public class CompanyLogoTests
{
    [Theory]
    [InlineData("https://static2.finnhub.io/logo/AAPL.png", "https://static2.finnhub.io/logo/AAPL.png")]
    [InlineData("http://example.com/a.png", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:image/png;base64,AAAA", null)]
    [InlineData("/relative.png", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_absolute_https_logos_are_kept(string? input, string? expected) =>
        Assert.Equal(expected, FinnhubClient.SafeLogo(input));

    [Fact]
    public async Task Company_profile_is_fetched_once_then_served_from_the_database()
    {
        using var app = new TestApp();
        app.Finnhub.Logos["AAPL"] = "https://cdn.test/AAPL.png";

        using (var s = app.NewScope())
        {
            var c = await s.Trading.EnsureCompanyAsync("AAPL", default);
            Assert.Equal("AAPL Inc", c?.Name);
            Assert.Equal("https://cdn.test/AAPL.png", c?.Logo);
        }
        using (var s = app.NewScope())
            Assert.Equal("https://cdn.test/AAPL.png", (await s.Trading.EnsureCompanyAsync("AAPL", default))?.Logo);

        Assert.Equal(1, app.Finnhub.ProfileCalls);
    }

    [Fact]
    public async Task Failed_profile_lookup_returns_null_saves_nothing_and_is_retried()
    {
        using var app = new TestApp();
        app.Finnhub.Failing.Add("AAPL");
        using (var s = app.NewScope())
        {
            Assert.Null(await s.Trading.EnsureCompanyAsync("AAPL", default));
            Assert.Empty(s.Db.Companies);
        }

        app.Finnhub.Failing.Remove("AAPL");
        using (var s = app.NewScope())
            Assert.Equal("AAPL Inc", (await s.Trading.EnsureCompanyAsync("AAPL", default))?.Name);
    }

    [Fact]
    public async Task Holdings_and_watchlist_carry_the_logo()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Finnhub.Prices["MSFT"] = 400m;
        app.Finnhub.Logos["AAPL"] = "https://cdn.test/AAPL.png";
        app.Finnhub.Logos["MSFT"] = "https://cdn.test/MSFT.png";
        using var s = app.NewScope();
        await s.Buy("AAPL", 1);
        await s.Watchlist.AddAsync("MSFT");

        Assert.Equal("https://cdn.test/AAPL.png", (await s.Portfolio.GetAsync()).Holdings.Single().Logo);
        Assert.Equal("https://cdn.test/MSFT.png", (await s.Watchlist.ListAsync()).Single().Logo);
    }

    [Fact]
    public async Task Unsafe_logo_from_finnhub_is_dropped()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["EVIL"] = 10m;
        app.Finnhub.Logos["EVIL"] = "javascript:alert(1)";
        using var s = app.NewScope();
        await s.Watchlist.AddAsync("EVIL");

        Assert.Null((await s.Watchlist.ListAsync()).Single().Logo);
        Assert.Null(s.Db.Companies.Single().Logo);
    }

    [Fact]
    public async Task Order_worker_still_saves_a_rejection_after_a_fill_whose_profile_lookup_failed()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope())
        {
            await s.Place("AAPL", Folio.Data.TradeSide.Buy, Folio.Data.OrderType.Limit, 900, 95m);  // fills first
            await s.Place("AAPL", Folio.Data.TradeSide.Buy, Folio.Data.OrderType.Limit, 150, 95m);
        }
        // Cash isn't reserved: spend some elsewhere so the second order can't be covered when it triggers.
        app.Finnhub.Prices["MSFT"] = 100m;
        using (var s = app.NewScope()) await s.Buy("MSFT", 100);
        app.Finnhub.Prices["AAPL"] = 94m;
        app.Finnhub.FailureStatus = System.Net.HttpStatusCode.InternalServerError;

        using (var s = app.NewScope())
        {
            // Make only the profile call fail: quote succeeds first, then the symbol starts failing.
            await s.Finnhub.GetQuoteAsync("AAPL");
            app.Finnhub.Failing.Add("AAPL");
            await s.Orders.CheckOpenOrdersAsync();
        }

        using var check = app.NewScope();
        var statuses = check.Db.Orders.OrderBy(o => o.Id).Select(o => o.Status).ToList();
        Assert.Equal([Folio.Data.OrderStatus.Filled, Folio.Data.OrderStatus.Rejected], statuses);
    }
}
