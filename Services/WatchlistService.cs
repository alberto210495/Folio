using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

public record WatchlistAddRequest(string Symbol);

/// <summary>A watched symbol with its live quote. Price fields are null when no quote is available.</summary>
public record WatchItemView(
    string Symbol,
    string? Name,
    string? Logo,
    decimal? Price,
    decimal? Change,
    decimal? ChangePercent,
    bool Stale,
    decimal HeldQuantity,
    DateTime AddedAt);

/// <summary>Symbols you follow without holding them. Quotes come from the same 15s cache as the portfolio.</summary>
public class WatchlistService(FolioDb db, FinnhubClient finnhub, TradingService trading)
{
    /// <summary>
    /// Each watched symbol costs one Finnhub call per page refresh (60s), on top of holdings,
    /// open orders and SPY. 25 keeps a full page well inside the free 60 calls/min.
    /// </summary>
    public const int MaxItems = 25;

    public async Task<IReadOnlyList<WatchItemView>> ListAsync(CancellationToken ct = default)
    {
        var items = await db.Watchlist.AsNoTracking().OrderBy(w => w.AddedAt).ToListAsync(ct);
        return await ViewsAsync(items, ct);
    }

    public async Task<WatchItemView> AddAsync(string? rawSymbol, CancellationToken ct = default)
    {
        var symbol = TradingService.NormalizeSymbol(rawSymbol);

        var existing = await db.Watchlist.AsNoTracking().FirstOrDefaultAsync(w => w.Symbol == symbol, ct);
        if (existing is not null) return (await ViewsAsync([existing], ct))[0];

        if (await db.Watchlist.CountAsync(ct) >= MaxItems)
            throw new TradeRejectedException($"Your watchlist is full ({MaxItems} symbols). Remove one to add {symbol}.");

        // Same check as trading: Finnhub returns zeros for symbols it doesn't know.
        if (await finnhub.GetQuoteAsync(symbol, ct) is null)
            throw new TradeRejectedException($"No quote found for {symbol}. Check the ticker.");

        var item = new WatchlistItem { Symbol = symbol };
        db.Watchlist.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Added by a concurrent request; adding is idempotent.
            db.ChangeTracker.Clear();
        }

        await trading.EnsureCompanyAsync(symbol, ct); // name for the list; best-effort
        return (await ViewsAsync([item], ct))[0];
    }

    /// <summary>Removes the symbol. Returns false if it wasn't on the list.</summary>
    public async Task<bool> RemoveAsync(string? rawSymbol, CancellationToken ct = default)
    {
        var symbol = TradingService.NormalizeSymbol(rawSymbol);
        return await db.Watchlist.Where(w => w.Symbol == symbol).ExecuteDeleteAsync(ct) > 0;
    }

    private async Task<IReadOnlyList<WatchItemView>> ViewsAsync(IReadOnlyList<WatchlistItem> items, CancellationToken ct)
    {
        var symbols = items.Select(i => i.Symbol).ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => symbols.Contains(c.Symbol))
            .ToDictionaryAsync(c => c.Symbol, ct);
        var held = await db.Positions.AsNoTracking()
            .Where(p => symbols.Contains(p.Symbol))
            .ToDictionaryAsync(p => p.Symbol, p => p.Quantity, ct);

        var quotes = await Task.WhenAll(items.Select(async i =>
        {
            try { return await finnhub.GetQuoteAsync(i.Symbol, ct); }
            catch (FinnhubException) { return null; }
        }));

        return items.Select((i, n) =>
        {
            var q = quotes[n];
            var co = companies.GetValueOrDefault(i.Symbol);
            return new WatchItemView(
                i.Symbol,
                co?.Name,
                FinnhubClient.SafeLogo(co?.Logo),
                q?.Price,
                q?.Change,
                q?.ChangePercent,
                Stale: q is null || q.Stale,
                held.GetValueOrDefault(i.Symbol),
                i.AddedAt);
        }).ToList();
    }
}
