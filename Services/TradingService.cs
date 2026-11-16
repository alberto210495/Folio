using System.Text.RegularExpressions;
using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

public record TradeRequest(string Symbol, TradeSide Side, decimal Quantity);

public record SplitRequest(string Symbol, int From, int To);

/// <summary>What a split changed: positions and open orders adjusted (across every portfolio), and cash paid for fractions.</summary>
public record SplitResult(int Id, string Symbol, int From, int To, DateTime AppliedAt, int Positions, int Orders, int OrdersCancelled, decimal CashInLieu);

public record SplitView(int Id, string Symbol, int From, int To, DateTime AppliedAt);

public class TradeRejectedException(string message) : Exception(message);

/// <summary>Executes market orders against the paper account at the live Finnhub price.</summary>
public partial class TradingService(FolioDb db, FinnhubClient finnhub, MarketClock clock, PortfolioContext portfolio, TimeProvider time, ILogger<TradingService> log)
{
    /// <summary>Company name, industry and logo are re-fetched from Finnhub once they're this old.</summary>
    public static readonly TimeSpan CompanyRefreshAfter = TimeSpan.FromDays(30);

    public const int MaxSplitRatio = 1000;

    // Serialises trades so two concurrent orders can't both spend the same cash.
    private static readonly SemaphoreSlim TradeLock = new(1, 1);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9.\-]{0,19}$")]
    private static partial Regex SymbolPattern();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static string NormalizeSymbol(string? symbol)
    {
        var s = (symbol ?? "").Trim().ToUpperInvariant();
        if (!SymbolPattern().IsMatch(s)) throw new TradeRejectedException("Enter a valid ticker symbol.");
        return s;
    }

    /// <summary>The JSON enum converter also accepts numbers, so "side": 2 has to be refused here.</summary>
    public static TradeSide ValidateSide(TradeSide side) =>
        Enum.IsDefined(side) ? side : throw new TradeRejectedException("Side must be Buy or Sell.");

    public async Task<Trade> ExecuteAsync(TradeRequest request, CancellationToken ct = default)
    {
        var symbol = NormalizeSymbol(request.Symbol);
        var side = ValidateSide(request.Side);
        var qty = ValidateQuantity(request.Quantity);
        if (!clock.CanTrade())
            throw new TradeRejectedException(
                $"The market is closed. It opens {clock.DescribeNextOpen()}. You can place a limit order now; it's checked once the market opens.");
        var quote = await LiveQuoteAsync(symbol, ct);
        return await FillAsync(portfolio.Id, symbol, side, qty, quote.Price, ct: ct);
    }

    public static decimal ValidateQuantity(decimal qty)
    {
        if (qty <= 0) throw new TradeRejectedException("Quantity must be greater than zero.");
        if (decimal.Round(qty, 4) != qty) throw new TradeRejectedException("Quantity supports at most 4 decimal places.");
        return qty;
    }

    /// <summary>A fresh, tradable quote, or a <see cref="TradeRejectedException"/> explaining why not.</summary>
    public async Task<Quote> LiveQuoteAsync(string symbol, CancellationToken ct = default)
    {
        var quote = await finnhub.GetQuoteAsync(symbol, ct)
            ?? throw new TradeRejectedException($"No quote found for {symbol}.");
        if (quote.Stale)
            throw new TradeRejectedException("Live price is unavailable right now (Finnhub rate limit or outage). Try again shortly.");
        if (quote.Price <= 0)
            throw new TradeRejectedException($"{symbol} has no tradable price.");
        return quote;
    }

    public const decimal MaxCommission = 1_000m;
    public const decimal MaxSlippageBps = 500m; // 5%

    /// <summary>
    /// The fill price for a trade at <paramref name="market"/>: buys pay <paramref name="slippageBps"/> more,
    /// sells get that much less, to 4 decimals. A limit order never fills past its <paramref name="limit"/>.
    /// </summary>
    public static decimal FillPrice(TradeSide side, decimal market, decimal slippageBps, decimal? limit = null)
    {
        var f = slippageBps / 10_000m;
        var price = decimal.Round(side == TradeSide.Buy ? market * (1 + f) : market * (1 - f), 4, MidpointRounding.ToEven);
        if (limit is { } l)
            price = side == TradeSide.Buy ? Math.Max(market, Math.Min(price, l)) : Math.Min(market, Math.Max(price, l));
        return price;
    }

    /// <summary>Cash a buy of <paramref name="qty"/> at about <paramref name="price"/> needs, with the account's commission and slippage.</summary>
    public static decimal EstimatedBuyCost(decimal qty, decimal price, Account account, bool capped = false) =>
        Money(qty * (capped ? price : FillPrice(TradeSide.Buy, price, account.SlippageBps))) + account.Commission;

    /// <summary>
    /// Books a fill in portfolio <paramref name="accountId"/> at the <paramref name="marketPrice"/> adjusted for the
    /// portfolio's slippage, and charges its commission. Pass <paramref name="limit"/> for a limit order so slippage
    /// never takes the fill past it. <paramref name="beforeSave"/> runs inside the same transaction (used to mark a
    /// resting order filled atomically with its trade).
    /// </summary>
    public async Task<Trade> FillAsync(
        int accountId, string symbol, TradeSide side, decimal qty, decimal marketPrice,
        int? orderId = null, Action<Trade>? beforeSave = null, decimal? limit = null, CancellationToken ct = default)
    {
        ValidateSide(side);
        await TradeLock.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var account = await db.GetAccountAsync(accountId, ct);
            var position = await db.Positions.SingleOrDefaultAsync(p => p.AccountId == accountId && p.Symbol == symbol, ct);
            var price = FillPrice(side, marketPrice, account.SlippageBps, limit);
            var notional = Money(qty * price);
            var fee = account.Commission;
            var now = Now;

            var trade = new Trade
            {
                AccountId = accountId, Symbol = symbol, Side = side, Quantity = qty, Price = price, OrderId = orderId, ExecutedAt = now,
                Commission = fee,
                Slippage = Money(Math.Abs(price - marketPrice) * qty),
            };

            if (side == TradeSide.Buy)
            {
                var total = notional + fee;
                if (total > account.Cash)
                    throw new TradeRejectedException(fee > 0
                        ? $"Insufficient cash: order costs {Usd(total)} with the {Usd(fee)} commission, you have {Usd(account.Cash)}."
                        : $"Insufficient cash: order costs {Usd(notional)}, you have {Usd(account.Cash)}.");

                account.Cash -= total;
                // The commission is part of what the shares cost, so it's in the average cost.
                if (position is null)
                {
                    db.Positions.Add(new Position { AccountId = accountId, Symbol = symbol, Quantity = qty, AvgCost = (qty * price + fee) / qty, OpenedAt = now });
                }
                else
                {
                    var newQty = position.Quantity + qty;
                    position.AvgCost = (position.Quantity * position.AvgCost + qty * price + fee) / newQty;
                    position.Quantity = newQty;
                }
            }
            else
            {
                if (position is null || position.Quantity < qty)
                    throw new TradeRejectedException($"You only hold {position?.Quantity ?? 0:0.####} shares of {symbol}.");
                if (account.Cash + notional - fee < 0)
                    throw new TradeRejectedException($"Not enough cash to pay the {Usd(fee)} commission on this sale.");

                var realized = Money((price - position.AvgCost) * qty) - fee;
                trade.RealizedPnl = realized;
                account.RealizedPnl += realized;
                account.Cash += notional - fee;
                position.Quantity -= qty;
                if (position.Quantity == 0) db.Positions.Remove(position);
            }

            db.Trades.Add(trade);
            beforeSave?.Invoke(trade);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (side == TradeSide.Buy) await EnsureCompanyAsync(symbol, ct);
            return trade;
        }
        finally
        {
            TradeLock.Release();
        }
    }

    public async Task ResetAsync(decimal startingCash, CancellationToken ct = default)
    {
        if (startingCash is <= 0 or > 1_000_000_000m)
            throw new TradeRejectedException("Starting cash must be between $1 and $1,000,000,000.");

        await TradeLock.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = portfolio.Id;
            await db.Trades.Where(t => t.AccountId == id).ExecuteDeleteAsync(ct);
            await db.Positions.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
            await db.Snapshots.Where(s => s.AccountId == id).ExecuteDeleteAsync(ct);
            await db.Orders.Where(o => o.AccountId == id).ExecuteDeleteAsync(ct);
            var account = await db.GetAccountAsync(id, ct);
            account.StartingCash = account.Cash = Money(startingCash);
            account.RealizedPnl = 0;
            account.BenchmarkStartPrice = null; // re-captured by the next snapshot
            account.CreatedAt = Now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            TradeLock.Release();
        }
    }

    /// <summary>
    /// Applies a stock split to every portfolio: each holding's shares are multiplied by To/From and its average
    /// cost divided by the same, so cost basis and market value don't change. Shares are kept to 4 decimals;
    /// any smaller fraction is paid out as cash at the adjusted average cost, so no profit or loss is booked.
    /// Open orders get the same treatment (quantity × To/From, price × From/To); one left with no shares is cancelled.
    /// Call through <see cref="OrderService.ApplySplitAsync"/> so no order check runs at the same time.
    /// </summary>
    public async Task<SplitResult> ApplySplitAsync(SplitRequest req, CancellationToken ct = default)
    {
        var symbol = NormalizeSymbol(req.Symbol);
        if (req.From is < 1 or > MaxSplitRatio || req.To is < 1 or > MaxSplitRatio)
            throw new TradeRejectedException($"Split ratios must be whole numbers from 1 to {MaxSplitRatio}.");
        if (req.From == req.To)
            throw new TradeRejectedException("A split must change the number of shares.");

        await TradeLock.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = Now;
            decimal factor(decimal q) => q * req.To / req.From;

            var positions = await db.Positions.Where(p => p.Symbol == symbol).ToListAsync(ct);
            var accountIds = positions.Select(p => p.AccountId).Distinct().ToList();
            var accounts = await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            var cashInLieu = 0m;
            foreach (var p in positions)
            {
                var exact = factor(p.Quantity);
                var kept = decimal.Round(exact, 4, MidpointRounding.ToZero);
                var newAvg = p.Quantity * p.AvgCost / exact;
                var cash = Money((exact - kept) * newAvg);
                accounts[p.AccountId].Cash += cash;
                cashInLieu += cash;
                if (kept == 0)
                {
                    db.Positions.Remove(p);
                }
                else
                {
                    p.Quantity = kept;
                    p.AvgCost = newAvg;
                }
            }

            var orders = await db.Orders.Where(o => o.Symbol == symbol && o.Status == OrderStatus.Open).ToListAsync(ct);
            var cancelled = 0;
            foreach (var o in orders)
            {
                var qty = decimal.Round(factor(o.Quantity), 4, MidpointRounding.ToZero);
                var price = decimal.Round(o.Price * req.From / req.To, 4, MidpointRounding.ToEven);
                if (qty == 0 || price <= 0)
                {
                    o.Status = OrderStatus.Cancelled;
                    o.Reason = $"Cancelled by the {req.To}-for-{req.From} split: too small to adjust.";
                    o.ClosedAt = now;
                    cancelled++;
                }
                else
                {
                    o.Quantity = qty;
                    o.Price = price;
                }
            }

            // Waiting price alerts move with the price, or a 10-for-1 split would set off every "falls to" alert.
            foreach (var a in await db.Alerts.Where(a => a.Symbol == symbol && a.TriggeredAt == null).ToListAsync(ct))
                a.Price = Math.Max(0.0001m, decimal.Round(a.Price * req.From / req.To, 4, MidpointRounding.ToEven));

            var split = new StockSplit { Symbol = symbol, From = req.From, To = req.To, AppliedAt = now };
            db.Splits.Add(split);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation("Applied {To}-for-{From} split to {Symbol}: {Positions} positions, {Orders} orders",
                req.To, req.From, symbol, positions.Count, orders.Count);
            return new(split.Id, symbol, req.From, req.To, now, positions.Count, orders.Count - cancelled, cancelled, cashInLieu);
        }
        finally
        {
            TradeLock.Release();
        }
    }

    /// <summary>Splits recorded so far, newest first; one symbol's when <paramref name="symbol"/> is given.</summary>
    public async Task<IReadOnlyList<SplitView>> ListSplitsAsync(string? symbol, CancellationToken ct = default)
    {
        var query = db.Splits.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var s = NormalizeSymbol(symbol);
            query = query.Where(x => x.Symbol == s);
        }
        var rows = await query.OrderByDescending(x => x.AppliedAt).ThenByDescending(x => x.Id).Take(100).ToListAsync(ct);
        return rows.Select(x => new SplitView(x.Id, x.Symbol, x.From, x.To, x.AppliedAt)).ToList();
    }

    /// <summary>
    /// Best-effort: cache name, industry and logo (one Finnhub call per symbol, refreshed every
    /// <see cref="CompanyRefreshAfter"/>). Returns null if Finnhub couldn't be reached and nothing is cached yet;
    /// it's retried next time. A failed refresh keeps the cached data. Failure never blocks a trade.
    /// </summary>
    public async Task<Company?> EnsureCompanyAsync(string symbol, CancellationToken ct)
    {
        Company? existing = null, added = null;
        try
        {
            existing = await db.Companies.FirstOrDefaultAsync(c => c.Symbol == symbol, ct);
            if (existing is not null && Now - existing.FetchedAt < CompanyRefreshAfter) return existing;

            var profile = await finnhub.GetProfileAsync(symbol, ct);
            if (existing is null)
            {
                added = new Company { Symbol = symbol, Name = profile?.Name, Industry = profile?.Industry, Logo = profile?.Logo, FetchedAt = Now };
                db.Companies.Add(added);
            }
            else
            {
                // An empty profile (ETFs, or a passing Finnhub glitch) never wipes data we already have.
                if (profile is not null)
                {
                    existing.Name = profile.Name;
                    existing.Industry = profile.Industry;
                    existing.Logo = profile.Logo;
                }
                existing.FetchedAt = Now;
            }
            await db.SaveChangesAsync(ct);
            return added ?? existing;
        }
        catch (Exception ex) when (ex is FinnhubException or DbUpdateException)
        {
            log.LogInformation("Skipped profile for {Symbol}: {Message}", symbol, ex.Message);
            // Only undo our own row: callers (the order worker) may still have tracked orders to save.
            if (added is not null) db.Entry(added).State = EntityState.Detached;
            else if (existing is not null) await db.Entry(existing).ReloadAsync(ct);
            return existing;
        }
    }

    /// <summary>
    /// Re-fetches up to <paramref name="max"/> out-of-date company profiles for symbols you hold or watch,
    /// so renamed companies and new logos show up without anyone buying the stock again. Returns how many it tried.
    /// </summary>
    public async Task<int> RefreshStaleCompaniesAsync(int max, CancellationToken ct = default)
    {
        var cutoff = Now - CompanyRefreshAfter;
        var inUse = db.Positions.Select(p => p.Symbol).Union(db.Watchlist.Select(w => w.Symbol));
        var stale = await db.Companies.AsNoTracking()
            .Where(c => c.FetchedAt < cutoff && inUse.Contains(c.Symbol))
            .OrderBy(c => c.FetchedAt)
            .Select(c => c.Symbol)
            .Take(max)
            .ToListAsync(ct);
        foreach (var symbol in stale) await EnsureCompanyAsync(symbol, ct);
        return stale.Count;
    }

    public static decimal Money(decimal v) => decimal.Round(v, 2, MidpointRounding.ToEven);

    private static string Usd(decimal v) => v.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
