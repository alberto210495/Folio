using Folio.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Folio.Services;

public class FolioOptions
{
    /// <summary>Ticker the portfolio is compared against. SPY tracks the S&amp;P 500.</summary>
    public string BenchmarkSymbol { get; set; } = "SPY";
}

/// <param name="T">Time (UTC).</param>
/// <param name="V">Account value.</param>
/// <param name="B">What the starting cash would be worth if it had all gone into the benchmark at account start.</param>
public record HistoryPoint(DateTime T, decimal V, decimal? B);

public record HistoryView(decimal StartingCash, string BenchmarkSymbol, decimal? BenchmarkNow, IReadOnlyList<HistoryPoint> Points);

/// <summary>Records and serves account-value snapshots for the value-over-time chart.</summary>
public class SnapshotService(FolioDb db, PortfolioService portfolio, FinnhubClient finnhub, IOptions<FolioOptions> options)
{
    private const int MaxPoints = 400;
    private readonly string _benchmark = options.Value.BenchmarkSymbol;

    /// <summary>
    /// Takes a snapshot at current prices. Skipped when any holding's price is stale (it would distort
    /// the line), and when nothing changed since the last one (markets closed), unless a day has passed.
    /// </summary>
    public async Task<bool> RecordAsync(CancellationToken ct = default)
    {
        var view = await portfolio.GetAsync(ct);
        if (view.AnyStale) return false;

        var bench = await BenchmarkPriceAsync(ct);
        var account = await db.GetAccountAsync(ct);
        // First good benchmark price after account start (or reset) becomes the baseline.
        if (bench is not null && account.BenchmarkStartPrice is null) account.BenchmarkStartPrice = bench;

        var last = await db.Snapshots.AsNoTracking().OrderByDescending(s => s.TakenAt).FirstOrDefaultAsync(ct);
        var unchanged = last is not null
            && last.TotalValue == view.TotalValue
            && last.BenchmarkPrice == bench
            && DateTime.UtcNow - last.TakenAt < TimeSpan.FromDays(1);

        if (!unchanged)
        {
            db.Snapshots.Add(new PortfolioSnapshot
            {
                TotalValue = view.TotalValue,
                Cash = view.Cash,
                HoldingsValue = view.HoldingsValue,
                BenchmarkPrice = bench,
            });
        }
        await db.SaveChangesAsync(ct);
        return !unchanged;
    }

    public static DateTime? RangeStart(string? range) => (range ?? "1M").ToUpperInvariant() switch
    {
        "1D" => DateTime.UtcNow.AddDays(-1),
        "1W" => DateTime.UtcNow.AddDays(-7),
        "1M" => DateTime.UtcNow.AddMonths(-1),
        "3M" => DateTime.UtcNow.AddMonths(-3),
        "1Y" => DateTime.UtcNow.AddYears(-1),
        "ALL" => null,
        _ => throw new TradeRejectedException("Range must be one of 1D, 1W, 1M, 3M, 1Y, ALL."),
    };

    public async Task<HistoryView> GetHistoryAsync(string? range, CancellationToken ct = default)
    {
        var since = RangeStart(range);
        var account = await db.GetAccountAsync(ct);
        var basePrice = account.BenchmarkStartPrice;

        var query = db.Snapshots.AsNoTracking();
        if (since is not null) query = query.Where(s => s.TakenAt >= since);
        var rows = await query.OrderBy(s => s.TakenAt)
            .Select(s => new { s.TakenAt, s.TotalValue, s.BenchmarkPrice })
            .ToListAsync(ct);

        var points = rows
            .Select(r => new HistoryPoint(r.TakenAt, r.TotalValue, BenchmarkValue(account.StartingCash, basePrice, r.BenchmarkPrice)))
            .ToList();

        // The account's opening balance anchors both lines at their true start.
        if (since is null || account.CreatedAt >= since)
            points.Insert(0, new HistoryPoint(account.CreatedAt, account.StartingCash, basePrice is null ? null : account.StartingCash));

        var now = await BenchmarkPriceAsync(ct);
        return new HistoryView(
            account.StartingCash,
            _benchmark,
            BenchmarkValue(account.StartingCash, basePrice, now),
            Downsample(points));
    }

    public static decimal? BenchmarkValue(decimal startingCash, decimal? basePrice, decimal? price) =>
        basePrice is > 0 && price is not null
            ? decimal.Round(startingCash * price.Value / basePrice.Value, 2, MidpointRounding.ToEven)
            : null;

    /// <summary>Live benchmark price, or null if unavailable or stale. Never throws for Finnhub errors.</summary>
    private async Task<decimal?> BenchmarkPriceAsync(CancellationToken ct)
    {
        try
        {
            var q = await finnhub.GetQuoteAsync(_benchmark, ct);
            return q is { Stale: false, Price: > 0 } ? q.Price : null;
        }
        catch (FinnhubException)
        {
            return null;
        }
    }

    /// <summary>Keeps the last point of each equal-width time bucket, plus the first point.</summary>
    public static List<HistoryPoint> Downsample(List<HistoryPoint> points, int maxPoints = MaxPoints)
    {
        if (points.Count <= maxPoints) return points;

        var start = points[0].T.Ticks;
        var span = Math.Max(1, points[^1].T.Ticks - start);
        var result = new List<HistoryPoint> { points[0] };
        // maxPoints − 1 buckets (indices 0..maxPoints − 2) plus the first point = at most maxPoints.
        foreach (var bucket in points.Skip(1).GroupBy(p => (int)((p.T.Ticks - start) * (maxPoints - 2) / span)))
            result.Add(bucket.Last());
        return result;
    }
}

/// <summary>Snapshots the account at startup and every <see cref="Interval"/> while the app runs.</summary>
public class SnapshotWorker(IServiceScopeFactory scopes, ILogger<SnapshotWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var finnhub = scope.ServiceProvider.GetRequiredService<FinnhubClient>();
                if (finnhub.IsConfigured)
                    await scope.ServiceProvider.GetRequiredService<SnapshotService>().RecordAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Portfolio snapshot failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
