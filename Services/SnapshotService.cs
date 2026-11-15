using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

public record HistoryPoint(DateTime T, decimal V);

public record HistoryView(decimal StartingCash, IReadOnlyList<HistoryPoint> Points);

/// <summary>Records and serves account-value snapshots for the value-over-time chart.</summary>
public class SnapshotService(FolioDb db, PortfolioService portfolio)
{
    private const int MaxPoints = 400;

    /// <summary>
    /// Takes a snapshot at current prices. Skipped when any price is stale (it would distort
    /// the line), and when nothing changed since the last one (markets closed), unless a day has passed.
    /// </summary>
    public async Task<bool> RecordAsync(CancellationToken ct = default)
    {
        var view = await portfolio.GetAsync(ct);
        if (view.AnyStale) return false;

        var last = await db.Snapshots.AsNoTracking().OrderByDescending(s => s.TakenAt).FirstOrDefaultAsync(ct);
        if (last is not null && last.TotalValue == view.TotalValue && DateTime.UtcNow - last.TakenAt < TimeSpan.FromDays(1))
            return false;

        db.Snapshots.Add(new PortfolioSnapshot
        {
            TotalValue = view.TotalValue,
            Cash = view.Cash,
            HoldingsValue = view.HoldingsValue,
        });
        await db.SaveChangesAsync(ct);
        return true;
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

        var query = db.Snapshots.AsNoTracking();
        if (since is not null) query = query.Where(s => s.TakenAt >= since);
        var points = await query.OrderBy(s => s.TakenAt)
            .Select(s => new HistoryPoint(s.TakenAt, s.TotalValue))
            .ToListAsync(ct);

        // The account's opening balance anchors the line at its true start.
        if (since is null || account.CreatedAt >= since)
            points.Insert(0, new HistoryPoint(account.CreatedAt, account.StartingCash));

        return new HistoryView(account.StartingCash, Downsample(points));
    }

    /// <summary>Keeps the last point of each equal-width time bucket, plus the first point.</summary>
    private static List<HistoryPoint> Downsample(List<HistoryPoint> points)
    {
        if (points.Count <= MaxPoints) return points;

        var start = points[0].T.Ticks;
        var span = Math.Max(1, points[^1].T.Ticks - start);
        var result = new List<HistoryPoint> { points[0] };
        foreach (var bucket in points.Skip(1).GroupBy(p => (int)((p.T.Ticks - start) * (MaxPoints - 1) / span)))
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
