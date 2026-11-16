using Folio.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Folio.Services;

/// <param name="Direction">Optional: when left out, it's worked out from the current price (a target above it is Above).</param>
public record CreateAlertRequest(string Symbol, decimal Price, AlertDirection? Direction = null);

public record AlertView(
    int Id,
    string Symbol,
    AlertDirection Direction,
    decimal Price,
    DateTime CreatedAt,
    DateTime? TriggeredAt,
    decimal? TriggerPrice,
    bool Seen);

/// <param name="Active">Alerts still waiting, by symbol then price.</param>
/// <param name="Triggered">Alerts that have fired, newest first (the last <see cref="AlertService.HistoryShown"/>).</param>
public record AlertsView(IReadOnlyList<AlertView> Active, IReadOnlyList<AlertView> Triggered);

/// <summary>Price alerts on watchlist symbols: create, remove, list, and fire them when a live price reaches the target.</summary>
public class AlertService(FolioDb db, FinnhubClient finnhub, MarketClock clock, TimeProvider time, ILogger<AlertService> log)
{
    /// <summary>Every alert symbol is on the watchlist, so this doesn't add to the 50 streamed symbols.</summary>
    public const int MaxActive = 50;
    public const int HistoryShown = 20;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Above fires at or over the target; Below at or under it.</summary>
    public static bool IsTriggered(AlertDirection direction, decimal target, decimal price) =>
        direction == AlertDirection.Above ? price >= target : price <= target;

    public async Task<AlertsView> ListAsync(CancellationToken ct = default)
    {
        var active = await db.Alerts.AsNoTracking().Where(a => a.TriggeredAt == null).ToListAsync(ct);
        var triggered = await db.Alerts.AsNoTracking().Where(a => a.TriggeredAt != null)
            .OrderByDescending(a => a.TriggeredAt).ThenByDescending(a => a.Id).Take(HistoryShown).ToListAsync(ct);
        return new(
            active.OrderBy(a => a.Symbol).ThenBy(a => a.Price).Select(ToView).ToList(),
            triggered.Select(ToView).ToList());
    }

    public async Task<AlertView> CreateAsync(CreateAlertRequest req, CancellationToken ct = default)
    {
        var symbol = TradingService.NormalizeSymbol(req.Symbol);
        if (req.Price <= 0) throw new TradeRejectedException("Alert price must be greater than zero.");
        if (decimal.Round(req.Price, 4) != req.Price) throw new TradeRejectedException("Alert price supports at most 4 decimal places.");
        if (req.Direction is { } d && !Enum.IsDefined(d)) throw new TradeRejectedException("Direction must be Above or Below.");
        if (!await db.Watchlist.AnyAsync(w => w.Symbol == symbol, ct))
            throw new TradeRejectedException($"Add {symbol} to your watchlist first.");

        var quote = await finnhub.GetQuoteAsync(symbol, ct)
            ?? throw new TradeRejectedException($"No quote found for {symbol}, so there's nothing to compare the alert with.");
        var now = quote.Price;
        var direction = req.Direction ?? (req.Price > now ? AlertDirection.Above : req.Price < now ? AlertDirection.Below
            : throw new TradeRejectedException($"{symbol} is already at {Usd(now)}. Pick a price above or below it."));
        if (IsTriggered(direction, req.Price, now))
            throw new TradeRejectedException(
                $"{symbol} is already {(direction == AlertDirection.Above ? "at or above" : "at or below")} {Usd(req.Price)} (it's {Usd(now)} now).");

        // Compared here, not in SQL: SQLite keeps decimals as text, where 130 and 130.0 differ.
        var existing = (await db.Alerts.AsNoTracking().Where(a => a.TriggeredAt == null && a.Symbol == symbol).ToListAsync(ct))
            .FirstOrDefault(a => a.Direction == direction && a.Price == req.Price);
        if (existing is not null) return ToView(existing); // setting the same alert twice is a no-op

        if (await db.Alerts.CountAsync(a => a.TriggeredAt == null, ct) >= MaxActive)
            throw new TradeRejectedException($"You can have up to {MaxActive} alerts waiting. Remove one to add another.");

        var alert = new PriceAlert { Symbol = symbol, Direction = direction, Price = req.Price, CreatedAt = Now };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);
        return ToView(alert);
    }

    /// <summary>Removes an alert, waiting or fired. Returns false if there was none with that id.</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default) =>
        await db.Alerts.Where(a => a.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Clears the history of fired alerts. Returns how many were removed.</summary>
    public Task<int> ClearTriggeredAsync(CancellationToken ct = default) =>
        db.Alerts.Where(a => a.TriggeredAt != null).ExecuteDeleteAsync(ct);

    /// <summary>Marks fired alerts as shown, so the page announces each one only once.</summary>
    public Task<int> MarkSeenAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default) =>
        db.Alerts.Where(a => ids.Contains(a.Id) && a.TriggeredAt != null && !a.Seen)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Seen, true), ct);

    /// <summary>Fires every waiting alert for <paramref name="symbol"/> that <paramref name="price"/> reaches. Returns how many fired.</summary>
    public async Task<int> TriggerAsync(string symbol, decimal price, CancellationToken ct = default)
    {
        if (price <= 0) return 0;
        var waiting = await db.Alerts.Where(a => a.TriggeredAt == null && a.Symbol == symbol).ToListAsync(ct);
        var fired = waiting.Where(a => IsTriggered(a.Direction, a.Price, price)).ToList();
        if (fired.Count == 0) return 0;

        var now = Now;
        foreach (var a in fired)
        {
            a.TriggeredAt = now;
            a.TriggerPrice = price;
            log.LogInformation("Alert {Id}: {Symbol} {Direction} {Target} fired at {Price}", a.Id, a.Symbol, a.Direction, a.Price, price);
        }
        await db.SaveChangesAsync(ct);
        return fired.Count;
    }

    /// <summary>
    /// Checks every waiting alert against a fresh quote (the fallback for when no streamed trade arrives).
    /// Skipped while the market is closed, since prices don't move. Returns how many fired.
    /// </summary>
    public async Task<int> CheckAllAsync(CancellationToken ct = default)
    {
        if (!clock.CanTrade()) return 0;
        var symbols = await db.Alerts.AsNoTracking().Where(a => a.TriggeredAt == null).Select(a => a.Symbol).Distinct().ToListAsync(ct);
        var fired = 0;
        foreach (var symbol in symbols)
        {
            Quote? quote;
            try { quote = await finnhub.GetQuoteAsync(symbol, ct); }
            catch (FinnhubException ex)
            {
                log.LogInformation("Skipping alerts for {Symbol}: {Message}", symbol, ex.Message);
                continue;
            }
            if (quote is { Stale: false, Price: > 0 }) fired += await TriggerAsync(symbol, quote.Price, ct);
        }
        return fired;
    }

    private static AlertView ToView(PriceAlert a) =>
        new(a.Id, a.Symbol, a.Direction, a.Price, a.CreatedAt, a.TriggeredAt, a.TriggerPrice, a.Seen);

    private static string Usd(decimal v) => v.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>
/// Fires price alerts. Checks a symbol as soon as a streamed trade reaches one of its alerts, and every
/// <see cref="FolioOptions.OrderCheckSeconds"/> against quotes as a fallback. Tells open pages through
/// <see cref="LivePrices.SignalAlerts"/>.
/// </summary>
public class AlertWorker(IServiceScopeFactory scopes, LivePrices live, IOptions<FolioOptions> options, ILogger<AlertWorker> log) : BackgroundService
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private readonly System.Threading.Channels.Channel<LiveTick> _crossed =
        System.Threading.Channels.Channel.CreateUnbounded<LiveTick>(new() { SingleReader = true });
    private volatile PriceAlert[] _waiting = [];

    /// <summary>Runs on the stream's thread: compares the tick with the cached alerts, no I/O.</summary>
    private void OnTick(LiveTick tick)
    {
        if (!live.Counts(tick)) return;
        foreach (var a in _waiting)
            if (string.Equals(a.Symbol, tick.Symbol, StringComparison.OrdinalIgnoreCase) && AlertService.IsTriggered(a.Direction, a.Price, tick.Price))
            {
                _crossed.Writer.TryWrite(tick);
                return;
            }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fullEvery = TimeSpan.FromSeconds(Math.Max(15, options.Value.OrderCheckSeconds));
        var nextFull = DateTimeOffset.UtcNow + fullEvery;
        var nextRefresh = DateTimeOffset.UtcNow;
        live.Ticked += OnTick;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextRefresh)
                {
                    await RefreshAsync(stoppingToken);
                    nextRefresh = now + RefreshEvery;
                }
                if (now >= nextFull)
                {
                    await RunAsync(s => s.CheckAllAsync(stoppingToken));
                    nextFull = now + fullEvery;
                }

                var wake = (nextRefresh < nextFull ? nextRefresh : nextFull) - DateTimeOffset.UtcNow;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(wake > TimeSpan.Zero ? wake : TimeSpan.Zero);
                LiveTick tick;
                try { tick = await _crossed.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { continue; }

                // Drop alerts fired by this tick from the cache right away, so a burst of ticks fires each once.
                _waiting = _waiting.Where(a => !(string.Equals(a.Symbol, tick.Symbol, StringComparison.OrdinalIgnoreCase)
                    && AlertService.IsTriggered(a.Direction, a.Price, tick.Price))).ToArray();
                await RunAsync(s => s.TriggerAsync(tick.Symbol, tick.Price, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            live.Ticked -= OnTick;
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolioDb>();
            _waiting = await db.Alerts.AsNoTracking().Where(a => a.TriggeredAt == null).ToArrayAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't load price alerts");
        }
    }

    private async Task RunAsync(Func<AlertService, Task<int>> work)
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (!scope.ServiceProvider.GetRequiredService<FinnhubClient>().IsConfigured) return;
            if (await work(scope.ServiceProvider.GetRequiredService<AlertService>()) > 0)
            {
                live.SignalAlerts();
                await RefreshAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Alert check failed");
        }
    }
}
