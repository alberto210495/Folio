using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

public class MarketHoursTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    /// <summary>A moment given in New York local time.</summary>
    private static DateTimeOffset Et(string local)
    {
        var dt = DateTime.Parse(local);
        return new DateTimeOffset(dt, MarketHours.Eastern.GetUtcOffset(dt));
    }

    // NYSE's published 2026 and 2027 holiday calendars.
    [Theory]
    [InlineData("2026-01-01", "New Year's Day")]
    [InlineData("2026-01-19", "Martin Luther King Jr. Day")]
    [InlineData("2026-02-16", "Washington's Birthday")]
    [InlineData("2026-04-03", "Good Friday")]
    [InlineData("2026-05-25", "Memorial Day")]
    [InlineData("2026-06-19", "Juneteenth")]
    [InlineData("2026-07-03", "Independence Day")]   // July 4 is a Saturday
    [InlineData("2026-09-07", "Labor Day")]
    [InlineData("2026-11-26", "Thanksgiving Day")]
    [InlineData("2026-12-25", "Christmas Day")]
    [InlineData("2027-01-01", "New Year's Day")]
    [InlineData("2027-03-26", "Good Friday")]
    [InlineData("2027-06-18", "Juneteenth")]         // June 19 is a Saturday
    [InlineData("2027-07-05", "Independence Day")]   // July 4 is a Sunday
    [InlineData("2027-12-24", "Christmas Day")]      // Dec 25 is a Saturday
    public void Holidays_match_the_exchange_calendar(string date, string name) =>
        Assert.Equal(name, MarketHours.HolidayName(D(date)));

    [Theory]
    [InlineData("2026-07-02")] // Thursday before the observed July 3 holiday: full day
    [InlineData("2026-11-25")]
    [InlineData("2027-12-31")] // New Year's Day 2028 is a Saturday and isn't observed on Friday
    [InlineData("2021-06-18")] // Juneteenth wasn't an exchange holiday until 2022
    public void Ordinary_days_are_not_holidays(string date) =>
        Assert.Null(MarketHours.HolidayName(D(date)));

    [Theory]
    [InlineData("2026-11-27", true)]  // day after Thanksgiving
    [InlineData("2026-12-24", true)]  // Thursday Christmas Eve
    [InlineData("2025-07-03", true)]  // Thursday before Independence Day
    [InlineData("2026-07-02", false)]
    [InlineData("2027-12-23", false)]
    public void Early_closes(string date, bool early) =>
        Assert.Equal(early, MarketHours.IsEarlyClose(D(date)));

    [Fact]
    public void Open_during_the_session_with_the_close_time()
    {
        var s = MarketHours.GetStatus(Et("2026-11-18 10:00"));
        Assert.True(s.IsOpen);
        Assert.Null(s.ClosedReason);
        Assert.Equal(Et("2026-11-18 16:00"), s.ClosesAt);
        Assert.Equal(Et("2026-11-19 09:30"), s.NextOpen);
    }

    [Theory]
    [InlineData("2026-11-18 09:29:59", false)]
    [InlineData("2026-11-18 09:30", true)]
    [InlineData("2026-11-18 15:59:59", true)]
    [InlineData("2026-11-18 16:00", false)]
    public void Session_edges(string local, bool open) =>
        Assert.Equal(open, MarketHours.GetStatus(Et(local)).IsOpen);

    [Fact]
    public void Before_the_open_reports_todays_open()
    {
        var s = MarketHours.GetStatus(Et("2026-11-18 07:00"));
        Assert.False(s.IsOpen);
        Assert.Equal("Overnight", s.ClosedReason);
        Assert.Equal(Et("2026-11-18 09:30"), s.NextOpen);
    }

    [Fact]
    public void Weekend_opens_monday()
    {
        var s = MarketHours.GetStatus(Et("2026-11-14 20:00")); // Saturday
        Assert.Equal("Weekend", s.ClosedReason);
        Assert.Equal(Et("2026-11-16 09:30"), s.NextOpen);
    }

    [Fact]
    public void Holiday_skips_to_the_next_trading_day()
    {
        var s = MarketHours.GetStatus(Et("2026-11-26 11:00")); // Thanksgiving
        Assert.False(s.IsOpen);
        Assert.Equal("Thanksgiving Day", s.ClosedReason);
        Assert.Equal(Et("2026-11-27 09:30"), s.NextOpen);
    }

    [Fact]
    public void Long_weekend_skips_holiday_monday()
    {
        // Friday evening before Martin Luther King Jr. Day 2026.
        Assert.Equal(Et("2026-01-20 09:30"), MarketHours.GetStatus(Et("2026-01-16 17:00")).NextOpen);
    }

    [Fact]
    public void Early_close_day_closes_at_one()
    {
        Assert.True(MarketHours.GetStatus(Et("2026-11-27 12:59")).IsOpen);
        var s = MarketHours.GetStatus(Et("2026-11-27 12:00"));
        Assert.True(s.EarlyClose);
        Assert.Equal(Et("2026-11-27 13:00"), s.ClosesAt);
        Assert.False(MarketHours.GetStatus(Et("2026-11-27 13:00")).IsOpen);
    }

    [Fact]
    public void Open_is_930_eastern_on_both_sides_of_daylight_saving()
    {
        // 9:30 ET is 13:30 UTC in summer (EDT) and 14:30 UTC in winter (EST).
        Assert.True(MarketHours.GetStatus(new DateTimeOffset(2026, 7, 15, 13, 30, 0, TimeSpan.Zero)).IsOpen);
        Assert.False(MarketHours.GetStatus(new DateTimeOffset(2026, 12, 16, 13, 30, 0, TimeSpan.Zero)).IsOpen);
        Assert.True(MarketHours.GetStatus(new DateTimeOffset(2026, 12, 16, 14, 30, 0, TimeSpan.Zero)).IsOpen);
    }
}

public class MarketHoursTradingTests
{
    private static readonly DateTimeOffset Saturday = new(2026, 11, 14, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Market_orders_are_rejected_while_closed_with_the_next_open()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Time.Now = Saturday;
        using var s = app.NewScope();

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Buy("AAPL", 1));
        Assert.Contains("market is closed", ex.Message);
        Assert.Contains("Monday, Nov 16 at 9:30 AM ET", ex.Message);
        Assert.Empty(s.Db.Trades);
    }

    [Fact]
    public async Task Hours_can_be_turned_off()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Time.Now = Saturday;
        app.Settings.EnforceMarketHours = false;
        using var s = app.NewScope();

        await s.Buy("AAPL", 1);
        Assert.Single(s.Db.Trades);
    }

    [Fact]
    public async Task Marketable_limit_waits_for_the_open_instead_of_filling()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Time.Now = Saturday;
        using (var s = app.NewScope())
        {
            var o = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 105m);
            Assert.Equal(OrderStatus.Open, o.Status);
            Assert.Equal(0, await s.Orders.CheckOpenOrdersAsync()); // still closed
        }

        app.Time.Now = new DateTimeOffset(2026, 11, 16, 14, 31, 0, TimeSpan.Zero); // Monday 9:31 AM ET
        using (var s = app.NewScope())
            Assert.Equal(1, await s.Orders.CheckOpenOrdersAsync());
    }

    [Fact]
    public async Task Stop_rejection_does_not_suggest_a_market_order_while_closed()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Time.Now = Saturday;
        using var s = app.NewScope();

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Place("AAPL", TradeSide.Sell, OrderType.Stop, 1, 105m));
        Assert.DoesNotContain("market order", ex.Message);
    }

    [Fact]
    public void Next_open_wording()
    {
        var time = new FakeTime { Now = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero) }; // Wed 5 PM ET
        var clock = new MarketClock(time, Microsoft.Extensions.Options.Options.Create(new FolioOptions()));
        Assert.Equal("tomorrow at 9:30 AM ET", clock.DescribeNextOpen());

        time.Now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero); // Wed 7 AM ET
        Assert.Equal("today at 9:30 AM ET", clock.DescribeNextOpen());
    }
}
