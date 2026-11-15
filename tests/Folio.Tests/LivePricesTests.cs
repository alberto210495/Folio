using System.Net.ServerSentEvents;
using Folio.Data;
using Folio.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Folio.Tests;

public class LivePricesTests
{
    // Wednesday 2026-11-18, 10:00 AM ET (market open) and 6:00 PM ET (after hours).
    private static readonly DateTimeOffset Open = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterHours = new(2026, 11, 18, 23, 0, 0, TimeSpan.Zero);

    private static Quote Q(decimal price = 100m, decimal prevClose = 98m, DateTimeOffset? asOf = null, bool stale = false) =>
        new("AAPL", price, price - prevClose, 0, prevClose, asOf ?? Open.AddMinutes(-5), stale);

    [Fact]
    public void Newer_regular_hours_tick_replaces_the_price_and_recomputes_change()
    {
        var merged = LivePrices.Merge(Q(), new LiveTick("AAPL", 103m, Open), Open);
        Assert.Equal(103m, merged.Price);
        Assert.Equal(5m, merged.Change);
        Assert.Equal(5.102m, decimal.Round(merged.ChangePercent, 3));
        Assert.Equal(Open, merged.AsOf);
        Assert.Equal(98m, merged.PreviousClose);
    }

    [Fact]
    public void Older_tick_is_ignored()
    {
        var q = Q(asOf: Open);
        Assert.Same(q, LivePrices.Merge(q, new LiveTick("AAPL", 103m, Open.AddSeconds(-1)), Open));
    }

    [Fact]
    public void Extended_hours_tick_is_ignored()
    {
        var q = Q(asOf: Open);
        Assert.Same(q, LivePrices.Merge(q, new LiveTick("AAPL", 103m, AfterHours), AfterHours));
    }

    [Fact]
    public void Extended_hours_tick_counts_when_hours_are_not_enforced()
    {
        var merged = LivePrices.Merge(Q(asOf: Open), new LiveTick("AAPL", 103m, AfterHours), AfterHours, regularHoursOnly: false);
        Assert.Equal(103m, merged.Price);
        Assert.False(new LivePrices(new FakeTime()).Counts(new LiveTick("AAPL", 1m, AfterHours)));
        Assert.True(new LivePrices(new FakeTime(), regularHoursOnly: false).Counts(new LiveTick("AAPL", 1m, AfterHours)));
    }

    [Fact]
    public void Recent_tick_makes_a_stale_quote_fresh_but_an_old_one_does_not()
    {
        var stale = Q(stale: true);
        Assert.False(LivePrices.Merge(stale, new LiveTick("AAPL", 101m, Open), Open.AddSeconds(30)).Stale);
        Assert.True(LivePrices.Merge(stale, new LiveTick("AAPL", 101m, Open), Open.AddMinutes(5)).Stale);
    }

    [Fact]
    public void Record_keeps_the_newest_tick_and_reports_changes_once()
    {
        var live = new LivePrices(new FakeTime());
        var seen = new List<LiveTick>();
        live.Ticked += seen.Add;

        live.Record(new LiveTick("AAPL", 100m, Open));
        live.Record(new LiveTick("AAPL", 99m, Open.AddSeconds(-5))); // out of order: dropped
        live.Record(new LiveTick("AAPL", 100m, Open));               // duplicate: dropped
        live.Record(new LiveTick("MSFT", 400m, Open));

        Assert.Equal(100m, live.Get("aapl")?.Price);
        Assert.Equal(2, seen.Count);

        var (changed, seq) = live.ChangedSince(0);
        Assert.Equal(2, changed.Count);
        live.Record(new LiveTick("AAPL", 101m, Open.AddSeconds(1)));
        var (next, _) = live.ChangedSince(seq);
        Assert.Equal("AAPL", Assert.Single(next).Symbol);
    }

    [Fact]
    public void Interest_expires()
    {
        var time = new FakeTime { Now = Open };
        var live = new LivePrices(time);
        live.Touch("NVDA");
        Assert.Equal(["NVDA"], live.ActiveInterest());
        time.Now = Open + LivePrices.InterestFor + TimeSpan.FromSeconds(1);
        Assert.Empty(live.ActiveInterest());
    }

    [Fact]
    public void Parses_trade_batches_keeping_the_latest_print_per_symbol()
    {
        var (ticks, error) = FinnhubStream.ParseMessage("""
            {"type":"trade","data":[
              {"s":"AAPL","p":190.1,"t":1763478000000,"v":10},
              {"s":"AAPL","p":190.25,"t":1763478000500,"v":5},
              {"s":"MSFT","p":420,"t":1763478000100,"v":1},
              {"s":"BAD","p":0,"t":1763478000100,"v":1}
            ]}
            """);
        Assert.Null(error);
        Assert.Equal(2, ticks.Count);
        Assert.Equal(190.25m, ticks.Single(t => t.Symbol == "AAPL").Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1763478000500), ticks.Single(t => t.Symbol == "AAPL").At);
    }

    [Theory]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"trade","data":null}""")]
    [InlineData("not json")]
    public void Ignores_pings_and_junk(string json)
    {
        var (ticks, error) = FinnhubStream.ParseMessage(json);
        Assert.Empty(ticks);
        Assert.Null(error);
    }

    [Fact]
    public void Surfaces_stream_errors() =>
        Assert.Equal("Subscribing to too many symbols", FinnhubStream.ParseMessage("""{"type":"error","msg":"Subscribing to too many symbols"}""").Error);

    [Fact]
    public void Subscriptions_are_deduplicated_and_capped_in_priority_order()
    {
        var app = Enumerable.Range(0, 60).Select(i => $"S{i}").Prepend("aapl").Prepend("AAPL");
        var desired = FinnhubStream.Desired(app, ["NVDA"]);
        Assert.Equal(FinnhubStream.MaxSymbols, desired.Count);
        Assert.Contains("AAPL", desired);
        Assert.DoesNotContain("NVDA", desired); // order-form interest comes last
        Assert.DoesNotContain("S59", desired);
    }

    [Fact]
    public async Task Quotes_trades_and_portfolio_use_the_streamed_price()
    {
        using var app = new TestApp(); // clock: Wednesday 10:00 AM ET
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope()) await s.Buy("AAPL", 10);

        app.Live.Record(new LiveTick("AAPL", 104m, app.Time.Now));
        using var scope = app.NewScope();
        Assert.Equal(104m, (await scope.Finnhub.GetQuoteAsync("AAPL"))!.Price);
        Assert.Equal(1040m, (await scope.Portfolio.GetAsync()).HoldingsValue);
        Assert.Equal(104m, (await scope.Sell("AAPL", 1)).Price);
    }

    [Fact]
    public async Task Order_check_for_one_symbol_fills_at_the_streamed_price()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        app.Finnhub.Prices["MSFT"] = 400m;
        using (var s = app.NewScope())
        {
            await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 95m);
            await s.Place("MSFT", TradeSide.Buy, OrderType.Limit, 1, 395m);
        }
        app.Finnhub.Prices["MSFT"] = 390m; // MSFT would trigger too, but only AAPL is checked
        app.Live.Record(new LiveTick("AAPL", 94.5m, app.Time.Now));

        using (var s = app.NewScope())
            Assert.Equal(1, await s.Orders.CheckOpenOrdersAsync("AAPL"));

        using var check = app.NewScope();
        var orders = check.Db.Orders.OrderBy(o => o.Id).ToList();
        Assert.Equal(OrderStatus.Filled, orders[0].Status);
        Assert.Equal(94.5m, orders[0].FillPrice);
        Assert.Equal(OrderStatus.Open, orders[1].Status);
    }

    [Fact]
    public async Task Event_stream_starts_with_status_then_sends_new_regular_hours_prices()
    {
        var time = new FakeTime { Now = Open };
        var live = new LivePrices(time) { Connected = true };
        live.Record(new LiveTick("OLD", 1m, Open)); // before the page connected: not re-sent
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = live.StreamAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await e.MoveNextAsync());
        Assert.Equal("status", e.Current.EventType);

        live.Record(new LiveTick("AAPL", 101m, Open.AddSeconds(1)));
        live.Record(new LiveTick("LATE", 5m, AfterHours));
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("prices", e.Current.EventType);
        var json = System.Text.Json.JsonSerializer.Serialize(e.Current.Data);
        Assert.Contains("\"AAPL\"", json);
        Assert.DoesNotContain("OLD", json);
        Assert.DoesNotContain("LATE", json);
    }
}

/// <summary>
/// Opt-in check against the real Finnhub websocket: set FINNHUB_KEY to run it. Uses a crypto pair because
/// it trades around the clock, so it works when US markets are closed.
/// </summary>
public class FinnhubStreamLiveTests
{
    private sealed class Fixed(params string[] symbols) : IStreamSymbols
    {
        public Task<IReadOnlyList<string>> GetAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(symbols);
    }

    [Fact]
    public async Task Receives_ticks_from_finnhub()
    {
        var key = Environment.GetEnvironmentVariable("FINNHUB_KEY");
        if (string.IsNullOrWhiteSpace(key)) return; // not configured: nothing to check

        var live = new LivePrices(TimeProvider.System);
        var got = new TaskCompletionSource<LiveTick>();
        live.Ticked += t => got.TrySetResult(t);
        var stream = new FinnhubStream(live, new Fixed("BINANCE:BTCUSDT"),
            Options.Create(new FinnhubOptions { ApiKey = key }), NullLogger<FinnhubStream>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await stream.StartAsync(cts.Token);
        try
        {
            var tick = await got.Task.WaitAsync(cts.Token);
            Assert.Equal("BINANCE:BTCUSDT", tick.Symbol);
            Assert.True(tick.Price > 0);
            Assert.True(live.Connected);
        }
        finally
        {
            await stream.StopAsync(CancellationToken.None);
        }
    }
}
