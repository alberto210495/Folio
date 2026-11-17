using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

/// <summary>Implausible streamed prices are held back until a second tick confirms them.</summary>
public class PriceSanityTests
{
    // Wednesday 2026-11-18, 10:00 AM ET: market open.
    private static readonly DateTimeOffset T = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);

    private static (LivePrices Live, List<LiveTick> Raised) Make()
    {
        var live = new LivePrices(new FakeTime { Now = T });
        var raised = new List<LiveTick>();
        live.Ticked += raised.Add;
        return (live, raised);
    }

    [Fact]
    public void Normal_moves_are_accepted()
    {
        var (live, raised) = Make();
        Assert.True(live.Record(new("AAPL", 100m, T)));
        Assert.True(live.Record(new("AAPL", 119m, T.AddSeconds(1)))); // +19%: within the limit
        Assert.True(live.Record(new("AAPL", 96m, T.AddSeconds(2))));  // −19%
        Assert.Equal(3, raised.Count);
    }

    [Theory]
    [InlineData(1000)]  // fat-finger print, 10x
    [InlineData(1)]     // a decimal slip
    [InlineData(125)]   // +25%
    [InlineData(75)]    // −25%
    public void A_single_wild_tick_is_held_back(decimal bad)
    {
        var (live, raised) = Make();
        live.Record(new("AAPL", 100m, T));

        Assert.False(live.Record(new("AAPL", bad, T.AddSeconds(1))));
        Assert.Equal(100m, live.Get("AAPL")!.Price);
        Assert.Single(raised); // order fills and alerts never saw it

        Assert.True(live.Record(new("AAPL", 100.5m, T.AddSeconds(2)))); // back to normal: held tick dropped
        Assert.Equal(1, live.RejectedTicks);
    }

    [Fact]
    public void A_confirmed_jump_is_accepted()
    {
        var (live, raised) = Make();
        live.Record(new("AAPL", 100m, T));
        Assert.False(live.Record(new("AAPL", 130m, T.AddSeconds(1))));     // earnings gap: held
        Assert.True(live.Record(new("AAPL", 130.6m, T.AddSeconds(3))));    // second print agrees: accepted
        Assert.Equal(130.6m, live.Get("AAPL")!.Price);
        Assert.True(live.Record(new("AAPL", 131m, T.AddSeconds(4))));      // and it's the new normal
        Assert.Equal(3, raised.Count);
    }

    [Fact]
    public void Confirmation_must_be_close_and_soon()
    {
        var (live, _) = Make();
        live.Record(new("AAPL", 100m, T));
        live.Record(new("AAPL", 130m, T.AddSeconds(1)));
        Assert.False(live.Record(new("AAPL", 140m, T.AddSeconds(2))));                         // 7.7% away from the held one
        Assert.False(live.Record(new("AAPL", 140.5m, T.AddSeconds(2) + LivePrices.ConfirmFor + TimeSpan.FromSeconds(1)))); // too late
        Assert.Equal(100m, live.Get("AAPL")!.Price);
        Assert.Equal(2, live.RejectedTicks); // 130 and 140 were never confirmed
    }

    [Fact]
    public void The_first_tick_is_checked_against_the_rest_quote()
    {
        var (live, raised) = Make();
        live.NoteQuote(new Quote("AAPL", 100m, 0, 0, 99m, T.AddMinutes(-1), Stale: false));
        Assert.False(live.Record(new("AAPL", 10m, T)));
        Assert.Null(live.Get("AAPL"));
        Assert.True(live.Record(new("AAPL", 101m, T.AddSeconds(1))));
        Assert.Single(raised);
    }

    [Fact]
    public void A_stale_rest_quote_is_not_used_as_the_yardstick()
    {
        var (live, _) = Make();
        live.NoteQuote(new Quote("AAPL", 100m, 0, 0, 99m, T.AddDays(-3), Stale: true));
        Assert.True(live.Record(new("AAPL", 10m, T))); // nothing trustworthy to compare with
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_prices_are_refused(decimal price)
    {
        var (live, raised) = Make();
        Assert.False(live.Record(new("AAPL", price, T)));
        Assert.Empty(raised);
    }

    [Fact]
    public async Task A_bad_tick_neither_moves_the_quote_nor_fills_an_order()
    {
        using var app = new TestApp(); // clock: Wednesday 10:00 AM ET
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope())
        {
            await s.Finnhub.GetQuoteAsync("AAPL"); // REST price becomes the yardstick
            await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 95m);
        }

        Assert.False(app.Live.Record(new("AAPL", 1m, app.Time.Now))); // would trigger the $95 buy limit

        using var check = app.NewScope();
        Assert.Equal(100m, (await check.Finnhub.GetQuoteAsync("AAPL"))!.Price);
        Assert.Equal(0, await check.Orders.CheckOpenOrdersAsync("AAPL"));
        Assert.Equal(OrderStatus.Open, check.Db.Orders.Single().Status);
    }
}
