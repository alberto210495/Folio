using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

public record HoldingView(
    string Symbol,
    string? Name,
    string Sector,
    string? Logo,
    decimal Quantity,
    decimal AvgCost,
    decimal CostBasis,
    decimal Price,
    decimal MarketValue,
    decimal UnrealizedPnl,
    decimal UnrealizedPnlPercent,
    decimal DayChange,
    decimal DayChangePercent,
    bool PriceStale);

public record PortfolioView(
    decimal StartingCash,
    decimal Cash,
    decimal HoldingsValue,
    decimal TotalValue,
    decimal TotalReturn,
    decimal TotalReturnPercent,
    decimal UnrealizedPnl,
    decimal RealizedPnl,
    decimal DayChange,
    IReadOnlyList<HoldingView> Holdings,
    bool AnyStale,
    DateTime AsOf);

public class PortfolioService(FolioDb db, FinnhubClient finnhub)
{
    public async Task<PortfolioView> GetAsync(CancellationToken ct = default)
    {
        var account = await db.GetAccountAsync(ct);
        var positions = await db.Positions.AsNoTracking().ToListAsync(ct);
        var symbols = positions.Select(p => p.Symbol).ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => symbols.Contains(c.Symbol))
            .ToDictionaryAsync(c => c.Symbol, ct);

        // Quotes are cached by FinnhubClient, so refreshing the page every few seconds stays cheap.
        var quotes = await Task.WhenAll(positions.Select(async p =>
        {
            try { return await finnhub.GetQuoteAsync(p.Symbol, ct); }
            catch (FinnhubException) { return null; }
        }));

        var holdings = positions.Select((p, i) =>
        {
            var q = quotes[i];
            companies.TryGetValue(p.Symbol, out var co);
            // Without any quote, value at cost so totals stay meaningful.
            var price = q?.Price ?? p.AvgCost;
            var cost = p.Quantity * p.AvgCost;
            var value = p.Quantity * price;
            var dayChange = q is null ? 0 : p.Quantity * q.Change;

            return new HoldingView(
                p.Symbol,
                co?.Name,
                co?.Industry ?? "Other",
                FinnhubClient.SafeLogo(co?.Logo),
                p.Quantity,
                Round(p.AvgCost, 4),
                Round(cost),
                price,
                Round(value),
                Round(value - cost),
                cost == 0 ? 0 : Round((value - cost) / cost * 100),
                Round(dayChange),
                q?.ChangePercent ?? 0,
                PriceStale: q is null || q.Stale);
        })
        .OrderByDescending(h => h.MarketValue)
        .ToList();

        var holdingsValue = holdings.Sum(h => h.MarketValue);
        var total = account.Cash + holdingsValue;
        var totalReturn = total - account.StartingCash;

        return new PortfolioView(
            account.StartingCash,
            account.Cash,
            holdingsValue,
            total,
            totalReturn,
            account.StartingCash == 0 ? 0 : Round(totalReturn / account.StartingCash * 100),
            holdings.Sum(h => h.UnrealizedPnl),
            account.RealizedPnl,
            holdings.Sum(h => h.DayChange),
            holdings,
            holdings.Any(h => h.PriceStale),
            DateTime.UtcNow);
    }

    private static decimal Round(decimal v, int places = 2) => decimal.Round(v, places, MidpointRounding.ToEven);
}
