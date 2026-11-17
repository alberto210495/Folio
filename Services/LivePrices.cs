using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Folio.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Folio.Services;

/// <summary>The latest trade print for a symbol from Finnhub's websocket.</summary>
public record LiveTick(string Symbol, decimal Price, DateTimeOffset At);

/// <summary>
/// Latest streamed trade per symbol, shared by the whole app. <see cref="FinnhubClient"/> layers these
/// over its REST quotes, so valuations, fills and order checks all see the newest price.
/// </summary>
/// <param name="regularHoursOnly">
/// Ignore prints outside the regular session (the default). Off when <c>Folio:EnforceMarketHours</c> is
/// off, so round-the-clock trading also gets extended-hours prices.
/// </param>
public class LivePrices(TimeProvider time, bool regularHoursOnly = true, ILogger<LivePrices>? log = null)
{
    /// <summary>
    /// A tick this far (20%) from the last good price is held back until a second tick confirms it.
    /// Real moves that big (earnings gaps) get through a moment later; one-off bad prints never do.
    /// </summary>
    public const decimal MaxJump = 0.20m;
    /// <summary>A confirming tick must be within 1% of the held one, and arrive within this window.</summary>
    public const decimal ConfirmWithin = 0.01m;
    public static readonly TimeSpan ConfirmFor = TimeSpan.FromMinutes(2);

    /// <summary>A stale REST quote counts as fresh again if a tick arrived within this window.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    /// <summary>How long a symbol shown in the order form stays subscribed.</summary>
    public static readonly TimeSpan InterestFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (LiveTick Tick, long Seq)> _ticks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _interest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _resync = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, decimal> _reference = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LiveTick> _held = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private long _alertsVersion;
    private long _rejected;

    /// <summary>How many ticks were held back as implausible and never confirmed (for diagnostics).</summary>
    public long RejectedTicks => Interlocked.Read(ref _rejected);

    /// <summary>True while the websocket to Finnhub is open.</summary>
    public bool Connected { get; set; }

    /// <summary>Raised for every accepted tick (on the websocket's thread; keep handlers quick).</summary>
    public event Action<LiveTick>? Ticked;

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>Whether a tick should move prices: always, or only when printed during regular hours.</summary>
    public bool Counts(LiveTick tick) => !regularHoursOnly || MarketHours.GetStatus(tick.At).IsOpen;

    /// <summary>
    /// Remembers a fresh REST price as the yardstick for a symbol's first streamed tick, so even that one
    /// can't be wildly off.
    /// </summary>
    public void NoteQuote(Quote quote)
    {
        if (!quote.Stale && quote.Price > 0) _reference[quote.Symbol] = quote.Price;
    }

    public static bool IsJump(decimal from, decimal to) => from > 0 && Math.Abs(to / from - 1) > MaxJump;

    /// <summary>
    /// Accepts a tick unless it's older than the current one, a duplicate, non-positive, or an unconfirmed
    /// jump of more than <see cref="MaxJump"/> from the last good price. Returns whether it was accepted;
    /// only accepted ticks raise <see cref="Ticked"/> (which drives order fills and price alerts).
    /// </summary>
    public bool Record(LiveTick tick)
    {
        if (tick.Price <= 0) return false;
        if (!Plausible(tick)) return false;

        var accepted = false;
        _ticks.AddOrUpdate(tick.Symbol,
            _ => { accepted = true; return (tick, Interlocked.Increment(ref _seq)); },
            (_, old) =>
            {
                if (tick.At < old.Tick.At || (tick.At == old.Tick.At && tick.Price == old.Tick.Price)) return old;
                accepted = true;
                return (tick, Interlocked.Increment(ref _seq));
            });
        if (accepted) Ticked?.Invoke(tick);
        return accepted;
    }

    private bool Plausible(LiveTick tick)
    {
        var current = Get(tick.Symbol);
        if (current is not null && tick.At < current.At) return true; // stale; Record ignores it anyway
        var baseline = current?.Price ?? (_reference.TryGetValue(tick.Symbol, out var r) ? r : 0);
        if (!IsJump(baseline, tick.Price))
        {
            if (_held.TryRemove(tick.Symbol, out _)) Interlocked.Increment(ref _rejected); // prices went back to normal: it was a bad print
            return true;
        }

        // A second print near the held one, soon after, confirms a genuine move.
        if (_held.TryGetValue(tick.Symbol, out var held)
            && tick.At - held.At <= ConfirmFor
            && Math.Abs(tick.Price / held.Price - 1) <= ConfirmWithin)
        {
            _held.TryRemove(tick.Symbol, out _);
            log?.LogInformation("Accepted a {Change:P0} move in {Symbol} to {Price} after confirmation", tick.Price / baseline - 1, tick.Symbol, tick.Price);
            return true;
        }

        if (_held.TryGetValue(tick.Symbol, out var previous)) Interlocked.Increment(ref _rejected); // the earlier one never got confirmed
        _held[tick.Symbol] = tick;
        log?.LogWarning("Holding back an implausible {Symbol} price {Price} ({Change:P0} from {Baseline}) until confirmed", tick.Symbol, tick.Price, tick.Price / baseline - 1, baseline);
        return false;
    }

    public LiveTick? Get(string symbol) => _ticks.TryGetValue(symbol, out var e) ? e.Tick : null;

    /// <summary>Ticks recorded after <paramref name="seq"/>, and the sequence to pass next time.</summary>
    public (IReadOnlyList<LiveTick> Ticks, long Seq) ChangedSince(long seq)
    {
        var current = Interlocked.Read(ref _seq);
        var changed = _ticks.Values.Where(e => e.Seq > seq && e.Seq <= current).Select(e => e.Tick).ToList();
        return (changed, current);
    }

    /// <summary>Keeps a symbol subscribed for a few minutes, e.g. while it's in the order form.</summary>
    public void Touch(string symbol)
    {
        var isNew = !_interest.ContainsKey(symbol);
        _interest[symbol] = Now + InterestFor;
        if (isNew) RequestResync();
    }

    public IReadOnlyList<string> ActiveInterest()
    {
        var now = Now;
        foreach (var (sym, until) in _interest)
            if (until < now) _interest.TryRemove(sym, out _);
        return _interest.Keys.ToList();
    }

    /// <summary>Tells open pages (through an "alerts" event) that a price alert fired.</summary>
    public void SignalAlerts() => Interlocked.Increment(ref _alertsVersion);

    public long AlertsVersion => Interlocked.Read(ref _alertsVersion);

    /// <summary>Asks the stream to update its subscriptions now instead of at the next interval.</summary>
    public void RequestResync() => _resync.Writer.TryWrite(true);

    internal ChannelReader<bool> ResyncRequests => _resync.Reader;

    /// <summary>
    /// The quote with the streamed price on top, when the tick is newer than the quote and was printed
    /// during regular hours (extended-hours prints are ignored, matching the REST quote). Change is
    /// recomputed from the previous close.
    /// </summary>
    public static Quote Merge(Quote quote, LiveTick? tick, DateTimeOffset now, bool regularHoursOnly = true)
    {
        if (tick is null || tick.At <= quote.AsOf || tick.Price <= 0) return quote;
        if (regularHoursOnly && !MarketHours.GetStatus(tick.At).IsOpen) return quote;
        var change = tick.Price - quote.PreviousClose;
        return quote with
        {
            Price = tick.Price,
            Change = change,
            ChangePercent = quote.PreviousClose == 0 ? 0 : decimal.Round(change / quote.PreviousClose * 100, 4),
            AsOf = tick.At,
            Stale = quote.Stale && now - tick.At > FreshFor,
        };
    }

    public Quote Merge(Quote quote) => Merge(quote, Get(quote.Symbol), Now, regularHoursOnly);

    /// <summary>
    /// Server-sent events for the page: a "status" event first and every 15s, a "prices" event
    /// (symbol to price and time) at most once a second when regular-hours prices change, and an
    /// "alerts" event when a price alert fires (the page then fetches <c>/api/alerts</c>).
    /// </summary>
    public async IAsyncEnumerable<SseItem<object>> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var (_, seq) = ChangedSince(long.MaxValue); // start from now; the page already has current prices
        var alerts = AlertsVersion;
        bool? lastConnected = null;
        var lastStatus = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (Connected != lastConnected || Now - lastStatus > TimeSpan.FromSeconds(15))
            {
                lastConnected = Connected;
                lastStatus = Now;
                yield return new SseItem<object>(new { connected = Connected }, "status");
            }

            (var ticks, seq) = ChangedSince(seq);
            var prices = ticks
                .Where(Counts)
                .ToDictionary(t => t.Symbol, t => new { p = t.Price, t = t.At });
            if (prices.Count > 0) yield return new SseItem<object>(prices, "prices");

            if (AlertsVersion != alerts)
            {
                alerts = AlertsVersion;
                yield return new SseItem<object>(new { version = alerts }, "alerts");
            }
        }
        while (await WaitTick(timer, ct));
    }

    private static async Task<bool> WaitTick(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; } // the browser went away
    }
}

/// <summary>Which symbols the stream should follow, most important first (Finnhub's free tier allows 50).</summary>
public interface IStreamSymbols
{
    Task<IReadOnlyList<string>> GetAsync(CancellationToken ct);
}

/// <summary>Holdings, open orders, the benchmark, then the watchlist.</summary>
public class DbStreamSymbols(IServiceScopeFactory scopes, IOptions<FolioOptions> options) : IStreamSymbols
{
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FolioDb>();
        var list = new List<string>();
        list.AddRange(await db.Positions.AsNoTracking().OrderByDescending(p => p.Quantity * p.AvgCost).Select(p => p.Symbol).ToListAsync(ct));
        list.AddRange(await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Open).Select(o => o.Symbol).Distinct().ToListAsync(ct));
        list.Add(options.Value.BenchmarkSymbol);
        list.AddRange(await db.Watchlist.AsNoTracking().OrderBy(w => w.AddedAt).Select(w => w.Symbol).ToListAsync(ct));
        return list;
    }
}

/// <summary>
/// Keeps one websocket open to Finnhub (the free tier allows one per key), subscribed to the symbols
/// the app cares about, and records every trade print in <see cref="LivePrices"/>. Reconnects with backoff.
/// </summary>
public class FinnhubStream(LivePrices live, IStreamSymbols symbols, IOptions<FinnhubOptions> options, ILogger<FinnhubStream> log) : BackgroundService
{
    public const int MaxSymbols = 50;
    private static readonly TimeSpan ResyncEvery = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.ApiKey) || !opt.Streaming) return;

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                await RunAsync(opt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning("Price stream disconnected: {Message}", ex.Message);
            }
            finally
            {
                live.Connected = false;
            }

            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1); // it had been working
            try { await Task.Delay(backoff, stoppingToken); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    private async Task RunAsync(FinnhubOptions opt, CancellationToken stoppingToken)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // The key goes in the query string because that's what Finnhub requires; never log this URI.
        await ws.ConnectAsync(new Uri($"{opt.StreamUrl}?token={Uri.EscapeDataString(opt.ApiKey)}"), stoppingToken);
        live.Connected = true;
        log.LogInformation("Price stream connected");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var receive = ReceiveAsync(ws, cts.Token);
        var sync = SyncAsync(ws, cts.Token);
        var first = await Task.WhenAny(receive, sync);
        await cts.CancelAsync();
        try { await Task.WhenAll(receive, sync); } catch (OperationCanceledException) { }
        await first; // surface the error that ended the session
        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    private async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException($"Finnhub closed the stream ({result.CloseStatus} {result.CloseStatusDescription}).");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            var parsed = ParseMessage(text);
            if (parsed.Error is not null) log.LogWarning("Finnhub stream error: {Message}", parsed.Error);
            foreach (var tick in parsed.Ticks) live.Record(tick);
        }
    }

    /// <summary>Keeps subscriptions in line with what the app needs. The only sender, so sends never overlap.</summary>
    private async Task SyncAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!ct.IsCancellationRequested)
        {
            var desired = Desired(await symbols.GetAsync(ct), live.ActiveInterest());
            foreach (var sym in subscribed.Except(desired).ToList())
            {
                await SendAsync(ws, new { type = "unsubscribe", symbol = sym }, ct);
                subscribed.Remove(sym);
            }
            foreach (var sym in desired.Except(subscribed).ToList())
            {
                await SendAsync(ws, new { type = "subscribe", symbol = sym }, ct);
                subscribed.Add(sym);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(ResyncEvery);
            try { await live.ResyncRequests.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* interval elapsed */ }
        }
    }

    /// <summary>App symbols in priority order, then order-form interest; de-duplicated, capped at 50.</summary>
    public static HashSet<string> Desired(IEnumerable<string> appSymbols, IEnumerable<string> interest) =>
        appSymbols.Concat(interest)
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .Take(MaxSymbols)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Task SendAsync(ClientWebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(new ReadOnlyMemory<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, ct).AsTask();

    /// <summary>
    /// Parses one Finnhub message: <c>{"type":"trade","data":[{"s":"AAPL","p":190.1,"t":1700000000000,"v":10}]}</c>.
    /// Pings and unknown types yield nothing; <c>{"type":"error","msg":"…"}</c> yields the message.
    /// Only the latest print per symbol in a batch is kept.
    /// </summary>
    public static (IReadOnlyList<LiveTick> Ticks, string? Error) ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "error") return ([], root.TryGetProperty("msg", out var m) ? m.GetString() : "unknown error");
            if (type != "trade" || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return ([], null);

            var latest = new Dictionary<string, LiveTick>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in data.EnumerateArray())
            {
                if (!d.TryGetProperty("s", out var s) || s.GetString() is not { Length: > 0 } sym) continue;
                if (!d.TryGetProperty("p", out var p) || !p.TryGetDecimal(out var price) || price <= 0) continue;
                if (!d.TryGetProperty("t", out var ts) || !ts.TryGetInt64(out var ms)) continue;
                var tick = new LiveTick(sym.ToUpperInvariant(), price, DateTimeOffset.FromUnixTimeMilliseconds(ms));
                if (!latest.TryGetValue(tick.Symbol, out var prev) || tick.At >= prev.At) latest[tick.Symbol] = tick;
            }
            return (latest.Values.ToList(), null);
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }
}
