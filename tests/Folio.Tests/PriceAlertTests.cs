using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

public class PriceAlertTests
{
    private static async Task<TestApp> AppWatching(string symbol, decimal price)
    {
        var app = new TestApp();
        app.Finnhub.Prices[symbol] = price;
        using var s = app.NewScope();
        await s.Watchlist.AddAsync(symbol);
        return app;
    }

    [Theory]
    [InlineData(AlertDirection.Above, 130, 130, true)]
    [InlineData(AlertDirection.Above, 130, 129.99, false)]
    [InlineData(AlertDirection.Below, 110, 110, true)]
    [InlineData(AlertDirection.Below, 110, 110.01, false)]
    public void Trigger_rules(AlertDirection direction, decimal target, decimal price, bool expected) =>
        Assert.Equal(expected, AlertService.IsTriggered(direction, target, price));

    [Fact]
    public async Task Direction_is_worked_out_from_the_current_price()
    {
        using var app = await AppWatching("NVDA", 120m);
        using var s = app.NewScope();

        var up = await s.Alerts.CreateAsync(new("nvda", 130m));
        var down = await s.Alerts.CreateAsync(new("NVDA", 110m));

        Assert.Equal(("NVDA", AlertDirection.Above), (up.Symbol, up.Direction));
        Assert.Equal(AlertDirection.Below, down.Direction);
        Assert.Equal(app.Time.Now.UtcDateTime, up.CreatedAt);
        Assert.Equal([110m, 130m], (await s.Alerts.ListAsync()).Active.Select(a => a.Price));
    }

    [Fact]
    public async Task Alert_that_would_fire_straight_away_is_refused()
    {
        using var app = await AppWatching("NVDA", 120m);
        using var s = app.NewScope();

        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("NVDA", 110m, AlertDirection.Above)));
        Assert.Contains("already at or above", ex.Message);
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("NVDA", 120m)));
        Assert.Empty(s.Db.Alerts);
    }

    [Fact]
    public async Task Symbol_must_be_on_the_watchlist_and_price_valid()
    {
        using var app = await AppWatching("NVDA", 120m);
        app.Finnhub.Prices["AAPL"] = 200m;
        using var s = app.NewScope();

        Assert.Contains("watchlist first", (await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("AAPL", 250m)))).Message);
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("NVDA", 0m)));
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("NVDA", 130.00001m)));
        await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("NVDA", 130m, (AlertDirection)9)));
    }

    [Fact]
    public async Task Same_alert_twice_is_a_no_op()
    {
        using var app = await AppWatching("NVDA", 120m);
        using var s = app.NewScope();
        var a = await s.Alerts.CreateAsync(new("NVDA", 130m));
        var b = await s.Alerts.CreateAsync(new("NVDA", 130.0m));
        Assert.Equal(a.Id, b.Id);
        Assert.Single(s.Db.Alerts);
    }

    [Fact]
    public async Task Waiting_alerts_are_capped()
    {
        using var app = await AppWatching("NVDA", 100m);
        using var s = app.NewScope();
        for (var i = 1; i <= AlertService.MaxActive; i++) await s.Alerts.CreateAsync(new("NVDA", 100m + i));
        var ex = await Assert.ThrowsAsync<TradeRejectedException>(() => s.Alerts.CreateAsync(new("NVDA", 500m)));
        Assert.Contains($"up to {AlertService.MaxActive}", ex.Message);
    }

    [Fact]
    public async Task Price_reaching_the_target_fires_the_alert_once()
    {
        using var app = await AppWatching("NVDA", 120m);
        using (var s = app.NewScope())
        {
            await s.Alerts.CreateAsync(new("NVDA", 130m));
            await s.Alerts.CreateAsync(new("NVDA", 140m));
            await s.Alerts.CreateAsync(new("NVDA", 110m));
        }

        using (var s = app.NewScope())
        {
            Assert.Equal(0, await s.Alerts.TriggerAsync("NVDA", 129.99m));
            Assert.Equal(1, await s.Alerts.TriggerAsync("NVDA", 131.5m));
            Assert.Equal(0, await s.Alerts.TriggerAsync("NVDA", 131.5m)); // already fired
        }

        using var check = app.NewScope();
        var list = await check.Alerts.ListAsync();
        Assert.Equal([110m, 140m], list.Active.Select(a => a.Price));
        var fired = Assert.Single(list.Triggered);
        Assert.Equal((130m, 131.5m, false), (fired.Price, fired.TriggerPrice!.Value, fired.Seen));
        Assert.Equal(app.Time.Now.UtcDateTime, fired.TriggeredAt);
    }

    [Fact]
    public async Task Quote_check_fires_alerts_during_market_hours_only()
    {
        using var app = await AppWatching("NVDA", 120m);
        using (var s = app.NewScope()) await s.Alerts.CreateAsync(new("NVDA", 110m));
        app.Finnhub.Prices["NVDA"] = 109m;

        app.Time.Now = new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.Zero); // Saturday
        using (var s = app.NewScope()) Assert.Equal(0, await s.Alerts.CheckAllAsync());

        app.Time.Now = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero); // Monday 10:00 AM ET
        using (var s = app.NewScope()) Assert.Equal(1, await s.Alerts.CheckAllAsync());
    }

    [Fact]
    public async Task Seen_and_clear_manage_the_history()
    {
        using var app = await AppWatching("NVDA", 120m);
        using (var s = app.NewScope())
        {
            await s.Alerts.CreateAsync(new("NVDA", 130m));
            await s.Alerts.CreateAsync(new("NVDA", 110m));
            await s.Alerts.TriggerAsync("NVDA", 135m);
        }
        using (var s = app.NewScope())
        {
            var fired = (await s.Alerts.ListAsync()).Triggered.Single();
            Assert.Equal(1, await s.Alerts.MarkSeenAsync([fired.Id]));
            Assert.Equal(0, await s.Alerts.MarkSeenAsync([fired.Id]));
        }
        using (var s = app.NewScope())
        {
            Assert.True((await s.Alerts.ListAsync()).Triggered.Single().Seen);
            Assert.Equal(1, await s.Alerts.ClearTriggeredAsync());
            var list = await s.Alerts.ListAsync();
            Assert.Empty(list.Triggered);
            Assert.Single(list.Active); // waiting alerts aren't touched
        }
    }

    [Fact]
    public async Task Removing_from_the_watchlist_removes_its_waiting_alerts()
    {
        using var app = await AppWatching("NVDA", 120m);
        using (var s = app.NewScope())
        {
            await s.Alerts.CreateAsync(new("NVDA", 130m));
            await s.Alerts.CreateAsync(new("NVDA", 140m));
            await s.Alerts.TriggerAsync("NVDA", 131m);
            Assert.True(await s.Watchlist.RemoveAsync("NVDA"));
        }

        using var check = app.NewScope();
        var list = await check.Alerts.ListAsync();
        Assert.Empty(list.Active);
        Assert.Single(list.Triggered); // history is kept
    }

    [Fact]
    public async Task Split_moves_waiting_alert_prices()
    {
        using var app = await AppWatching("NVDA", 1200m);
        using (var s = app.NewScope())
        {
            await s.Alerts.CreateAsync(new("NVDA", 1000m));
            await s.Orders.ApplySplitAsync(new("NVDA", 1, 10));
        }
        app.Finnhub.Prices["NVDA"] = 120m;

        using var check = app.NewScope();
        Assert.Equal(100m, (await check.Alerts.ListAsync()).Active.Single().Price);
        Assert.Equal(0, await check.Alerts.TriggerAsync("NVDA", 120m)); // no false alarm after the split
    }

    [Fact]
    public async Task Event_stream_tells_the_page_when_an_alert_fires()
    {
        var open = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
        var live = new LivePrices(new FakeTime { Now = open });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = live.StreamAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("status", e.Current.EventType);

        live.SignalAlerts();
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("alerts", e.Current.EventType);
    }
}
