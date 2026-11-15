using System.Text.RegularExpressions;
using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

public record TradeRequest(string Symbol, TradeSide Side, decimal Quantity);

public class TradeRejectedException(string message) : Exception(message);

/// <summary>Executes market orders against the paper account at the live Finnhub price.</summary>
public partial class TradingService(FolioDb db, FinnhubClient finnhub, ILogger<TradingService> log)
{
    // Serialises trades so two concurrent orders can't both spend the same cash.
    private static readonly SemaphoreSlim TradeLock = new(1, 1);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9.\-]{0,19}$")]
    private static partial Regex SymbolPattern();

    public static string NormalizeSymbol(string? symbol)
    {
        var s = (symbol ?? "").Trim().ToUpperInvariant();
        if (!SymbolPattern().IsMatch(s)) throw new TradeRejectedException("Enter a valid ticker symbol.");
        return s;
    }

    public async Task<Trade> ExecuteAsync(TradeRequest request, CancellationToken ct = default)
    {
        var symbol = NormalizeSymbol(request.Symbol);
        var qty = ValidateQuantity(request.Quantity);
        var quote = await LiveQuoteAsync(symbol, ct);
        return await FillAsync(symbol, request.Side, qty, quote.Price, ct: ct);
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

    /// <summary>
    /// Books a fill at <paramref name="price"/>. <paramref name="beforeSave"/> runs inside the same
    /// transaction (used to mark a resting order filled atomically with its trade).
    /// </summary>
    public async Task<Trade> FillAsync(
        string symbol, TradeSide side, decimal qty, decimal price,
        int? orderId = null, Action<Trade>? beforeSave = null, CancellationToken ct = default)
    {
        await TradeLock.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var account = await db.GetAccountAsync(ct);
            var position = await db.Positions.SingleOrDefaultAsync(p => p.Symbol == symbol, ct);
            var notional = Money(qty * price);

            var trade = new Trade { Symbol = symbol, Side = side, Quantity = qty, Price = price, OrderId = orderId };

            if (side == TradeSide.Buy)
            {
                if (notional > account.Cash)
                    throw new TradeRejectedException($"Insufficient cash: order costs {Usd(notional)}, you have {Usd(account.Cash)}.");

                account.Cash -= notional;
                if (position is null)
                {
                    db.Positions.Add(new Position { Symbol = symbol, Quantity = qty, AvgCost = price });
                }
                else
                {
                    var newQty = position.Quantity + qty;
                    position.AvgCost = (position.Quantity * position.AvgCost + qty * price) / newQty;
                    position.Quantity = newQty;
                }
            }
            else
            {
                if (position is null || position.Quantity < qty)
                    throw new TradeRejectedException($"You only hold {position?.Quantity ?? 0:0.####} shares of {symbol}.");

                var realized = Money((price - position.AvgCost) * qty);
                trade.RealizedPnl = realized;
                account.RealizedPnl += realized;
                account.Cash += notional;
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
            await db.Trades.ExecuteDeleteAsync(ct);
            await db.Positions.ExecuteDeleteAsync(ct);
            await db.Snapshots.ExecuteDeleteAsync(ct);
            await db.Orders.ExecuteDeleteAsync(ct);
            var account = await db.GetAccountAsync(ct);
            account.StartingCash = account.Cash = Money(startingCash);
            account.RealizedPnl = 0;
            account.BenchmarkStartPrice = null; // re-captured by the next snapshot
            account.CreatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            TradeLock.Release();
        }
    }

    /// <summary>Best-effort: cache name/industry for the sector chart. Failure never blocks a trade.</summary>
    public async Task EnsureCompanyAsync(string symbol, CancellationToken ct)
    {
        try
        {
            if (await db.Companies.AnyAsync(c => c.Symbol == symbol, ct)) return;
            var profile = await finnhub.GetProfileAsync(symbol, ct);
            db.Companies.Add(new Company
            {
                Symbol = symbol,
                Name = profile?.Name,
                Industry = profile?.Industry,
                Logo = profile?.Logo,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is FinnhubException or DbUpdateException)
        {
            log.LogInformation("Skipped profile for {Symbol}: {Message}", symbol, ex.Message);
        }
    }

    private static decimal Money(decimal v) => decimal.Round(v, 2, MidpointRounding.ToEven);

    private static string Usd(decimal v) => v.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
