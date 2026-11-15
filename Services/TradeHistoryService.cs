using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

/// <param name="OrderType">Market, Limit or Stop.</param>
public record TradeView(
    int Id,
    string Symbol,
    TradeSide Side,
    decimal Quantity,
    decimal Price,
    decimal? RealizedPnl,
    DateTime ExecutedAt,
    int? OrderId,
    string OrderType);

/// <param name="Page">1-based. Past-the-end requests are clamped to the last page.</param>
/// <param name="Pages">At least 1, even with no trades.</param>
public record TradePage(IReadOnlyList<TradeView> Items, int Total, int Page, int PageSize, int Pages);

/// <summary>Trade history, newest first, one page at a time.</summary>
public class TradeHistoryService(FolioDb db)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    public async Task<TradePage> PageAsync(int? page, int? pageSize, CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await db.Trades.CountAsync(ct);
        var pages = Math.Max(1, (total + size - 1) / size);
        // A page that no longer exists (e.g. after a reset) shows the last one instead of an empty table.
        var current = Math.Clamp(page ?? 1, 1, pages);

        var trades = await db.Trades.AsNoTracking()
            .OrderByDescending(t => t.ExecutedAt).ThenByDescending(t => t.Id) // Id breaks ties so pages never overlap
            .Skip((current - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var orderIds = trades.Where(t => t.OrderId != null).Select(t => t.OrderId!.Value).Distinct().ToList();
        var types = await db.Orders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Type, ct);

        var items = trades.Select(t => new TradeView(
            t.Id, t.Symbol, t.Side, t.Quantity, t.Price, t.RealizedPnl, t.ExecutedAt, t.OrderId,
            t.OrderId is int id && types.TryGetValue(id, out var type) ? type.ToString() : "Market")).ToList();

        return new TradePage(items, total, current, size, pages);
    }
}
