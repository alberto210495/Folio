using Folio.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Folio.Services;

public record PlaceOrderRequest(string Symbol, TradeSide Side, OrderType Type, decimal Quantity, decimal Price);

public record OrderView(
    int Id,
    string Symbol,
    TradeSide Side,
    OrderType Type,
    decimal Quantity,
    decimal Price,
    OrderStatus Status,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    decimal? FillPrice,
    string? Reason,
    decimal? CurrentPrice);

/// <summary>Places, cancels and fills resting limit and stop orders.</summary>
public class OrderService(FolioDb db, TradingService trading, FinnhubClient finnhub, MarketClock clock, ILogger<OrderService> log)
{
    // Serialises order checks and cancels so an order can't be cancelled mid-fill.
    private static readonly SemaphoreSlim OrderLock = new(1, 1);

    /// <summary>
    /// Buy limit: at or below the limit. Sell limit: at or above.
    /// Buy stop: at or above the stop (breakout). Sell stop: at or below (stop-loss).
    /// </summary>
    public static bool IsTriggered(TradeSide side, OrderType type, decimal trigger, decimal price) => (side, type) switch
    {
        (TradeSide.Buy, OrderType.Limit) => price <= trigger,
        (TradeSide.Sell, OrderType.Limit) => price >= trigger,
        (TradeSide.Buy, OrderType.Stop) => price >= trigger,
        (TradeSide.Sell, OrderType.Stop) => price <= trigger,
        _ => false,
    };

    public async Task<OrderView> PlaceAsync(PlaceOrderRequest req, CancellationToken ct = default)
    {
        var symbol = TradingService.NormalizeSymbol(req.Symbol);
        var qty = TradingService.ValidateQuantity(req.Quantity);
        if (req.Price <= 0) throw new TradeRejectedException("Price must be greater than zero.");
        if (decimal.Round(req.Price, 4) != req.Price) throw new TradeRejectedException("Price supports at most 4 decimal places.");
        if (!Enum.IsDefined(req.Type)) throw new TradeRejectedException("Order type must be Limit or Stop.");

        var quote = await trading.LiveQuoteAsync(symbol, ct);
        var triggeredNow = IsTriggered(req.Side, req.Type, req.Price, quote.Price);

        if (req.Type == OrderType.Stop && triggeredNow)
        {
            var where = req.Side == TradeSide.Buy ? "above" : "below";
            throw new TradeRejectedException(
                $"A {req.Side.ToString().ToLowerInvariant()} stop must be {where} the current price ({Usd(quote.Price)})." +
                (clock.CanTrade() ? " Use a market order to trade now." : ""));
        }

        // Held for validation, save and any immediate fill, so the background check can't
        // fill this order concurrently and the open-order totals stay accurate.
        await OrderLock.WaitAsync(ct);
        try
        {
            var open = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Open).ToListAsync(ct);
            if (req.Side == TradeSide.Sell)
            {
                var held = (await db.Positions.AsNoTracking().SingleOrDefaultAsync(p => p.Symbol == symbol, ct))?.Quantity ?? 0;
                var committed = open.Where(o => o.Side == TradeSide.Sell && o.Symbol == symbol).Sum(o => o.Quantity);
                if (qty > held - committed)
                    throw new TradeRejectedException(committed > 0
                        ? $"You hold {held:0.####} {symbol}, and {committed:0.####} are already in open sell orders."
                        : $"You only hold {held:0.####} shares of {symbol}.");
            }
            else
            {
                var account = await db.GetAccountAsync(ct);
                var committed = open.Where(o => o.Side == TradeSide.Buy).Sum(o => o.Quantity * o.Price);
                var cost = qty * req.Price;
                if (cost > account.Cash - committed)
                    throw new TradeRejectedException(committed > 0
                        ? $"Order needs about {Usd(cost)}; you have {Usd(account.Cash)} cash with {Usd(committed)} already in open buy orders."
                        : $"Insufficient cash: order needs about {Usd(cost)}, you have {Usd(account.Cash)}.");
            }

            var order = new Order { Symbol = symbol, Side = req.Side, Type = req.Type, Quantity = qty, Price = req.Price };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            // A limit that's already marketable fills right away, like at a real broker; while the
            // market is closed it waits for the open instead of filling on the last close.
            if (triggeredNow && clock.CanTrade()) await TryFillAsync(order, quote.Price, ct);

            return ToView(order, quote.Price);
        }
        finally
        {
            OrderLock.Release();
        }
    }

    public async Task<OrderView> CancelAsync(int id, CancellationToken ct = default)
    {
        await OrderLock.WaitAsync(ct);
        try
        {
            var order = await db.Orders.FindAsync([id], ct)
                ?? throw new TradeRejectedException($"Order {id} not found.");
            if (order.Status != OrderStatus.Open)
                throw new TradeRejectedException($"Order {id} is already {order.Status.ToString().ToLowerInvariant()}.");

            order.Status = OrderStatus.Cancelled;
            order.ClosedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ToView(order, null);
        }
        finally
        {
            OrderLock.Release();
        }
    }

    /// <summary>Checks open orders (all, or one symbol's) against a live quote. Returns how many filled.</summary>
    public async Task<int> CheckOpenOrdersAsync(CancellationToken ct = default) => await CheckOpenOrdersAsync(null, ct);

    public async Task<int> CheckOpenOrdersAsync(string? symbol, CancellationToken ct = default)
    {
        // Outside market hours prices don't move (and fills would use the last close), so skip the quotes.
        if (!clock.CanTrade()) return 0;
        await OrderLock.WaitAsync(ct);
        try
        {
            var open = await db.Orders.Where(o => o.Status == OrderStatus.Open && (symbol == null || o.Symbol == symbol)).ToListAsync(ct);
            var filled = 0;

            foreach (var group in open.GroupBy(o => o.Symbol))
            {
                Quote? quote;
                try { quote = await finnhub.GetQuoteAsync(group.Key, ct); }
                catch (FinnhubException ex)
                {
                    log.LogInformation("Skipping orders for {Symbol}: {Message}", group.Key, ex.Message);
                    continue;
                }
                // Never fill on a stale price.
                if (quote is not { Stale: false, Price: > 0 }) continue;

                foreach (var order in group.OrderBy(o => o.CreatedAt))
                    if (IsTriggered(order.Side, order.Type, order.Price, quote.Price) && await TryFillAsync(order, quote.Price, ct))
                        filled++;
            }

            return filled;
        }
        finally
        {
            OrderLock.Release();
        }
    }

    public async Task<IReadOnlyList<OrderView>> ListAsync(string? status, int limit, CancellationToken ct = default)
    {
        var query = db.Orders.AsNoTracking();
        query = (status ?? "open").ToLowerInvariant() switch
        {
            "open" => query.Where(o => o.Status == OrderStatus.Open),
            "closed" => query.Where(o => o.Status != OrderStatus.Open),
            "all" => query,
            _ => throw new TradeRejectedException("Status must be open, closed or all."),
        };
        var orders = await query.OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);

        // Current prices for open orders (cached quotes, so this is cheap).
        var prices = new Dictionary<string, decimal>();
        foreach (var sym in orders.Where(o => o.Status == OrderStatus.Open).Select(o => o.Symbol).Distinct())
        {
            try
            {
                if (await finnhub.GetQuoteAsync(sym, ct) is { Price: > 0 } q) prices[sym] = q.Price;
            }
            catch (FinnhubException) { /* show without a current price */ }
        }

        return orders.Select(o => ToView(o, prices.TryGetValue(o.Symbol, out var p) ? p : null)).ToList();
    }

    /// <summary>Fills the order at <paramref name="price"/>, or marks it rejected if the account can't cover it any more.</summary>
    private async Task<bool> TryFillAsync(Order order, decimal price, CancellationToken ct)
    {
        try
        {
            await trading.FillAsync(order.Symbol, order.Side, order.Quantity, price, order.Id, _ =>
            {
                order.Status = OrderStatus.Filled;
                order.FillPrice = price;
                order.ClosedAt = DateTime.UtcNow;
            }, ct);
            log.LogInformation("Filled {Type} {Side} {Qty} {Symbol} at {Price}", order.Type, order.Side, order.Quantity, order.Symbol, price);
            return true;
        }
        catch (TradeRejectedException ex)
        {
            order.Status = OrderStatus.Rejected;
            order.Reason = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            order.ClosedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Rejected order {Id}: {Reason}", order.Id, ex.Message);
            return false;
        }
    }

    private static OrderView ToView(Order o, decimal? currentPrice) => new(
        o.Id, o.Symbol, o.Side, o.Type, o.Quantity, o.Price, o.Status,
        o.CreatedAt, o.ClosedAt, o.FillPrice, o.Reason, currentPrice);

    private static string Usd(decimal v) => v.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>
/// Fills resting orders. Checks all of them every <see cref="FolioOptions.OrderCheckSeconds"/>, and checks a
/// symbol straight away when a streamed trade crosses one of its orders' prices.
/// </summary>
public class OrderWorker(IServiceScopeFactory scopes, LivePrices live, IOptions<FolioOptions> options, ILogger<OrderWorker> log) : BackgroundService
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SymbolCooldown = TimeSpan.FromSeconds(2);

    private readonly System.Threading.Channels.Channel<string> _triggered =
        System.Threading.Channels.Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private volatile Order[] _open = [];
    private readonly Dictionary<string, DateTimeOffset> _lastChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs on the stream's thread: compares the tick with the cached open orders, no I/O.</summary>
    private void OnTick(LiveTick tick)
    {
        if (!live.Counts(tick)) return;
        foreach (var o in _open)
            if (string.Equals(o.Symbol, tick.Symbol, StringComparison.OrdinalIgnoreCase) && OrderService.IsTriggered(o.Side, o.Type, o.Price, tick.Price))
            {
                _triggered.Writer.TryWrite(o.Symbol);
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
                    await RefreshOpenAsync(stoppingToken);
                    nextRefresh = now + RefreshEvery;
                }
                if (now >= nextFull)
                {
                    await CheckAsync(null, stoppingToken);
                    nextFull = now + fullEvery;
                }

                var wake = (nextRefresh < nextFull ? nextRefresh : nextFull) - DateTimeOffset.UtcNow;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(wake > TimeSpan.Zero ? wake : TimeSpan.Zero);
                string symbol;
                try { symbol = await _triggered.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { continue; }

                // Coalesce a burst of ticks for the same symbol into one check.
                if (_lastChecked.TryGetValue(symbol, out var last) && DateTimeOffset.UtcNow - last < SymbolCooldown) continue;
                _lastChecked[symbol] = DateTimeOffset.UtcNow;
                await CheckAsync(symbol, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            live.Ticked -= OnTick;
        }
    }

    private async Task RefreshOpenAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolioDb>();
            _open = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Open).ToArrayAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't load open orders");
        }
    }

    private async Task CheckAsync(string? symbol, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (!scope.ServiceProvider.GetRequiredService<FinnhubClient>().IsConfigured) return;

            var filled = await scope.ServiceProvider.GetRequiredService<OrderService>().CheckOpenOrdersAsync(symbol, ct);
            if (filled > 0)
            {
                await scope.ServiceProvider.GetRequiredService<SnapshotService>().RecordAsync(ct);
                await RefreshOpenAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Order check failed");
        }
    }
}
