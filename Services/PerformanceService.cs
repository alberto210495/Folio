using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

/// <summary>
/// Results of closed trades. Every sell closes (part of) a position and counts as one trade: a win when it
/// realized a profit, a loss when it realized a loss, breakeven otherwise. Percentages are 0–100.
/// </summary>
/// <param name="WinRate">Wins ÷ (wins + losses); breakeven sells don't count either way. Null with no wins or losses.</param>
/// <param name="AvgWinPercent">Average gain on a winning sell, relative to the cost of the shares sold.</param>
/// <param name="ProfitFactor">Gross profit ÷ gross loss. Null when there are no losses.</param>
/// <param name="Expectancy">Average realized P&amp;L per closed trade, wins and losses together.</param>
public record TradeStats(
    int Closed,
    int Wins,
    int Losses,
    int Breakeven,
    decimal? WinRate,
    decimal? AvgWin,
    decimal? AvgLoss,
    decimal? AvgWinPercent,
    decimal? AvgLossPercent,
    decimal? LargestWin,
    decimal? LargestLoss,
    decimal? ProfitFactor,
    decimal? Expectancy,
    decimal GrossProfit,
    decimal GrossLoss);

/// <summary>
/// Falls from a high in account value, worked out from the saved value history (plus the live value when no
/// price is stale). Only covers times when Folio was running, so a dip between snapshots can be missed.
/// Amounts and percentages are positive numbers: a 12% drawdown is 12.
/// </summary>
/// <param name="MaxPercent">Largest fall from a previous high, as a share of that high.</param>
/// <param name="PeakAt">When the high before the largest fall was reached.</param>
/// <param name="TroughAt">The low point of the largest fall.</param>
/// <param name="RecoveredAt">When the value first got back to that high; null if it hasn't yet.</param>
/// <param name="CurrentPercent">How far the latest value is below the all-time high.</param>
public record DrawdownStats(
    int Points,
    DateTime? Since,
    decimal? HighWater,
    DateTime? HighWaterAt,
    decimal CurrentAmount,
    decimal CurrentPercent,
    decimal MaxAmount,
    decimal MaxPercent,
    DateTime? PeakAt,
    DateTime? TroughAt,
    DateTime? RecoveredAt,
    bool LiveIncluded);

/// <summary>What trading costs have taken from this portfolio, on buys and sells alike.</summary>
public record CostStats(decimal Commission, decimal Slippage, decimal Total, int Fills);

public record PerformanceView(TradeStats Trades, DrawdownStats Drawdown, CostStats Costs);

/// <summary>Win rate, average win and loss, and drawdown for the current portfolio, since it started or was last reset.</summary>
public class PerformanceService(FolioDb db, PortfolioService valuation, PortfolioContext portfolio, TimeProvider time)
{
    public async Task<PerformanceView> GetAsync(CancellationToken ct = default)
    {
        var id = portfolio.Id;
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new PortfolioNotFoundException(id);

        var sells = await db.Trades.AsNoTracking()
            .Where(t => t.AccountId == id && t.Side == TradeSide.Sell && t.RealizedPnl != null)
            .Select(t => new ClosedTrade(t.Quantity, t.Price, t.RealizedPnl!.Value))
            .ToListAsync(ct);

        var snapshots = await db.Snapshots.AsNoTracking()
            .Where(s => s.AccountId == id)
            .OrderBy(s => s.TakenAt)
            .Select(s => new ValuePoint(s.TakenAt, s.TotalValue))
            .ToListAsync(ct);

        var points = new List<ValuePoint> { new(account.CreatedAt, account.StartingCash) };
        points.AddRange(snapshots.Where(s => s.T >= account.CreatedAt));

        var live = await valuation.GetAsync(id, ct);
        var liveIncluded = !live.AnyStale;
        if (liveIncluded) points.Add(new(time.GetUtcNow().UtcDateTime, live.TotalValue));

        var fills = await db.Trades.AsNoTracking().Where(t => t.AccountId == id)
            .Select(t => new { t.Commission, t.Slippage }).ToListAsync(ct);
        var commission = fills.Sum(f => f.Commission);
        var slippage = fills.Sum(f => f.Slippage);
        var costs = new CostStats(commission, slippage, commission + slippage, fills.Count);

        return new(Trades(sells), Drawdown(points, liveIncluded), costs);
    }

    public record ClosedTrade(decimal Quantity, decimal Price, decimal RealizedPnl)
    {
        /// <summary>What the shares sold had cost: proceeds minus profit.</summary>
        public decimal Cost => Quantity * Price - RealizedPnl;
        public decimal? ReturnPercent => Cost > 0 ? RealizedPnl / Cost * 100 : null;
    }

    public record ValuePoint(DateTime T, decimal V);

    public static TradeStats Trades(IReadOnlyList<ClosedTrade> sells)
    {
        var wins = sells.Where(s => s.RealizedPnl > 0).ToList();
        var losses = sells.Where(s => s.RealizedPnl < 0).ToList();
        var grossProfit = wins.Sum(s => s.RealizedPnl);
        var grossLoss = -losses.Sum(s => s.RealizedPnl);
        var decided = wins.Count + losses.Count;

        return new TradeStats(
            Closed: sells.Count,
            Wins: wins.Count,
            Losses: losses.Count,
            Breakeven: sells.Count - decided,
            WinRate: decided == 0 ? null : Round((decimal)wins.Count / decided * 100),
            AvgWin: wins.Count == 0 ? null : Round(grossProfit / wins.Count),
            AvgLoss: losses.Count == 0 ? null : Round(-grossLoss / losses.Count),
            AvgWinPercent: AvgPercent(wins),
            AvgLossPercent: AvgPercent(losses),
            LargestWin: wins.Count == 0 ? null : wins.Max(s => s.RealizedPnl),
            LargestLoss: losses.Count == 0 ? null : losses.Min(s => s.RealizedPnl),
            ProfitFactor: grossLoss == 0 ? null : Round(grossProfit / grossLoss),
            Expectancy: sells.Count == 0 ? null : Round(sells.Sum(s => s.RealizedPnl) / sells.Count),
            GrossProfit: grossProfit,
            GrossLoss: grossLoss);
    }

    private static decimal? AvgPercent(List<ClosedTrade> trades)
    {
        var pcts = trades.Select(t => t.ReturnPercent).OfType<decimal>().ToList();
        return pcts.Count == 0 ? null : Round(pcts.Average());
    }

    /// <summary>Largest peak-to-trough fall (by percentage) and the current fall from the all-time high.</summary>
    public static DrawdownStats Drawdown(IReadOnlyList<ValuePoint> points, bool liveIncluded)
    {
        if (points.Count == 0)
            return new(0, null, null, null, 0, 0, 0, 0, null, null, null, liveIncluded);

        var peak = points[0];
        decimal maxPct = 0, maxAmt = 0, maxPeakValue = 0;
        DateTime? peakAt = null, troughAt = null, recoveredAt = null;

        foreach (var p in points)
        {
            if (troughAt is not null && recoveredAt is null && p.T > troughAt && p.V >= maxPeakValue)
                recoveredAt = p.T;

            if (p.V >= peak.V)
            {
                peak = p;
                continue;
            }

            var amt = peak.V - p.V;
            var pct = peak.V > 0 ? amt / peak.V * 100 : 0;
            if (pct > maxPct)
            {
                // A deeper fall: either a lower point in the same fall, or a new one after a higher peak.
                maxPct = pct;
                maxAmt = amt;
                maxPeakValue = peak.V;
                peakAt = peak.T;
                troughAt = p.T;
                recoveredAt = null;
            }
        }

        var last = points[^1];
        var currentAmt = peak.V - last.V;
        return new DrawdownStats(
            points.Count,
            points[0].T,
            peak.V,
            peak.T,
            Round(currentAmt),
            peak.V > 0 ? Round(currentAmt / peak.V * 100) : 0,
            Round(maxAmt),
            Round(maxPct),
            peakAt,
            troughAt,
            recoveredAt,
            liveIncluded);
    }

    private static decimal Round(decimal v) => decimal.Round(v, 2, MidpointRounding.ToEven);
}
