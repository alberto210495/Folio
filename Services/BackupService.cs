using System.Globalization;
using System.Text.RegularExpressions;
using Folio.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Folio.Services;

public record BackupInfo(string Name, long Bytes, DateTime CreatedAt);

/// <summary>Where backups go and how many are kept.</summary>
public record BackupView(string Directory, int Keep, IReadOnlyList<BackupInfo> Backups);

/// <summary>
/// Everything Folio stores, as one portable file. Ids are kept so links (a trade's order) survive a round trip.
/// Company profiles are left out: they're a cache Folio refetches from Finnhub.
/// </summary>
public record FolioArchive(
    int Version,
    DateTime ExportedAt,
    List<Account> Portfolios,
    List<Position> Positions,
    List<Trade> Trades,
    List<Order> Orders,
    List<PortfolioSnapshot> Snapshots,
    List<WatchlistItem> Watchlist,
    List<PriceAlert> Alerts,
    List<StockSplit> Splits);

public record ImportResult(int Portfolios, int Trades, int Orders, string? BackupName);

/// <summary>
/// Copies of the SQLite database (automatic daily, kept for <see cref="FolioOptions.BackupKeep"/> backups), plus a
/// JSON export of everything and an import that replaces everything after taking a safety backup.
/// </summary>
public partial class BackupService(FolioDb db, IOptions<FolioOptions> options, IHostEnvironment env, TimeProvider time, ILogger<BackupService> log)
{
    public const int ArchiveVersion = 1;
    /// <summary>Largest export Folio will import. Years of trading are a few MB.</summary>
    public const long MaxImportBytes = 25 * 1024 * 1024;

    private static readonly SemaphoreSlim BackupLock = new(1, 1);

    // Only names Folio generated itself are ever listed, so a file name can't point outside the folder.
    [GeneratedRegex(@"^folio-\d{8}-\d{6}(-[a-z-]{1,20})?\.db$")]
    private static partial Regex BackupName();

    public string Directory => Path.GetFullPath(options.Value.BackupDirectory, env.ContentRootPath);

    public BackupView List()
    {
        var files = System.IO.Directory.Exists(Directory)
            ? new DirectoryInfo(Directory).GetFiles("folio-*.db").Where(f => BackupName().IsMatch(f.Name))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal) // names sort by time
                .Select(f => new BackupInfo(f.Name, f.Length, f.LastWriteTimeUtc)).ToList()
            : [];
        return new(Directory, options.Value.BackupKeep, files);
    }

    /// <summary>
    /// Copies the live database with SQLite's online backup (consistent even while Folio is writing), then
    /// deletes the oldest backups beyond the keep count. <paramref name="label"/> tags why it was taken.
    /// </summary>
    public async Task<BackupInfo> CreateAsync(string? label = null, CancellationToken ct = default)
    {
        await BackupLock.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = label is null ? $"folio-{stamp}.db" : $"folio-{stamp}-{label}.db";
            if (!BackupName().IsMatch(name)) throw new ArgumentException("Invalid backup label.", nameof(label));
            var path = Path.Combine(Directory, name);

            var source = (SqliteConnection)db.Database.GetDbConnection();
            var wasClosed = source.State != System.Data.ConnectionState.Open;
            if (wasClosed) await source.OpenAsync(ct);
            try
            {
                using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                target.Open();
                source.BackupDatabase(target);
            }
            finally
            {
                if (wasClosed) await source.CloseAsync();
            }

            Prune();
            var info = new FileInfo(path);
            log.LogInformation("Backed up the database to {Name} ({Bytes} bytes)", name, info.Length);
            return new(name, info.Length, info.LastWriteTimeUtc);
        }
        finally
        {
            BackupLock.Release();
        }
    }

    /// <summary>When the newest backup was taken, or null if there are none.</summary>
    public DateTime? LatestAt() => List().Backups.FirstOrDefault()?.CreatedAt;

    private void Prune()
    {
        foreach (var old in List().Backups.Skip(Math.Max(1, options.Value.BackupKeep)))
        {
            try { File.Delete(Path.Combine(Directory, old.Name)); }
            catch (IOException ex) { log.LogWarning("Couldn't delete old backup {Name}: {Message}", old.Name, ex.Message); }
        }
    }

    public async Task<FolioArchive> ExportAsync(CancellationToken ct = default) => new(
        ArchiveVersion,
        time.GetUtcNow().UtcDateTime,
        await db.Accounts.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
        await db.Positions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
        await db.Trades.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
        await db.Orders.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
        await db.Snapshots.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
        await db.Watchlist.AsNoTracking().OrderBy(x => x.AddedAt).ToListAsync(ct),
        await db.Alerts.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
        await db.Splits.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct));

    /// <summary>
    /// Replaces all portfolios, trades, orders, history, watchlist, alerts and splits with the archive's.
    /// The archive is fully validated first; then a backup is taken so the import can be undone.
    /// </summary>
    public async Task<ImportResult> ImportAsync(FolioArchive? archive, CancellationToken ct = default)
    {
        Validate(archive ?? throw new TradeRejectedException("That file isn't a Folio export."), time.GetUtcNow().UtcDateTime);

        var backup = await CreateAsync("before-import", ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Trades.ExecuteDeleteAsync(ct);
        await db.Orders.ExecuteDeleteAsync(ct);
        await db.Positions.ExecuteDeleteAsync(ct);
        await db.Snapshots.ExecuteDeleteAsync(ct);
        await db.Alerts.ExecuteDeleteAsync(ct);
        await db.Splits.ExecuteDeleteAsync(ct);
        await db.Watchlist.ExecuteDeleteAsync(ct);
        await db.Accounts.ExecuteDeleteAsync(ct);
        db.ChangeTracker.Clear();

        foreach (var p in archive.Portfolios) p.Name = p.Name.Trim();
        db.Accounts.AddRange(archive.Portfolios);
        db.Positions.AddRange(archive.Positions);
        db.Orders.AddRange(archive.Orders);
        db.Trades.AddRange(archive.Trades);
        db.Snapshots.AddRange(archive.Snapshots);
        db.Watchlist.AddRange(archive.Watchlist);
        db.Alerts.AddRange(archive.Alerts);
        db.Splits.AddRange(archive.Splits);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        log.LogInformation("Imported {Portfolios} portfolios and {Trades} trades (backup {Backup})", archive.Portfolios.Count, archive.Trades.Count, backup.Name);
        return new(archive.Portfolios.Count, archive.Trades.Count, archive.Orders.Count, backup.Name);
    }

    /// <summary>Rejects anything Folio couldn't have written itself, before touching the database.</summary>
    public static void Validate(FolioArchive a, DateTime? now = null)
    {
        static TradeRejectedException Bad(string why) => new($"Import file: {why}");

        if (a.Version != ArchiveVersion) throw Bad($"unsupported version {a.Version} (expected {ArchiveVersion}).");
        if (a.Portfolios is null || a.Positions is null || a.Trades is null || a.Orders is null || a.Snapshots is null
            || a.Watchlist is null || a.Alerts is null || a.Splits is null)
            throw Bad("a section is missing.");
        if (a.Portfolios.Count is 0 or > PortfoliosService.MaxPortfolios)
            throw Bad($"it must contain 1 to {PortfoliosService.MaxPortfolios} portfolios.");

        void Sym(string? s, string where)
        {
            if (s is null || s != s.Trim().ToUpperInvariant()) throw Bad($"invalid symbol in {where}.");
            try { TradingService.NormalizeSymbol(s); } catch (TradeRejectedException) { throw Bad($"invalid symbol \"{Truncate(s)}\" in {where}."); }
        }
        static void Unique<T>(IEnumerable<T> keys, string what)
        {
            var seen = new HashSet<T>();
            foreach (var k in keys) if (!seen.Add(k)) throw Bad($"duplicate {what}.");
        }
        static void Money(decimal v, string what, decimal max = 1_000_000_000_000m)
        {
            if (v < 0 || v > max) throw Bad($"{what} is out of range.");
        }

        // Dates can't be in the future (a day's slack for clock differences between computers).
        var latest = (now ?? DateTime.UtcNow).AddDays(1);
        var dates = a.Portfolios.Select(x => (DateTime?)x.CreatedAt)
            .Concat(a.Positions.Select(x => (DateTime?)x.OpenedAt))
            .Concat(a.Trades.Select(x => (DateTime?)x.ExecutedAt))
            .Concat(a.Orders.SelectMany(x => new[] { (DateTime?)x.CreatedAt, x.ClosedAt }))
            .Concat(a.Snapshots.Select(x => (DateTime?)x.TakenAt))
            .Concat(a.Watchlist.Select(x => (DateTime?)x.AddedAt))
            .Concat(a.Alerts.SelectMany(x => new[] { (DateTime?)x.CreatedAt, x.TriggeredAt }))
            .Concat(a.Splits.Select(x => (DateTime?)x.AppliedAt));
        if (dates.Any(d => d > latest)) throw Bad("it contains dates in the future.");

        Unique(a.Portfolios.Select(p => p.Id), "portfolio id");
        Unique(a.Portfolios.Select(p => (p.Name ?? "").Trim().ToLowerInvariant()), "portfolio name");
        foreach (var p in a.Portfolios)
        {
            if (p.Id <= 0) throw Bad("portfolio ids must be positive.");
            var name = p.Name?.Trim() ?? "";
            if (name.Length is 0 or > PortfoliosService.MaxNameLength) throw Bad("portfolio names must be 1 to 40 characters.");
            if (p.StartingCash <= 0 || p.StartingCash > 1_000_000_000m) throw Bad($"starting cash of \"{name}\" is out of range.");
            Money(p.Cash, $"cash of \"{name}\"");
            if (Math.Abs(p.RealizedPnl) > 1_000_000_000_000m) throw Bad($"realized P&L of \"{name}\" is out of range.");
            if (p.Commission is < 0 or > TradingService.MaxCommission) throw Bad($"commission of \"{name}\" is out of range.");
            if (p.SlippageBps is < 0 or > TradingService.MaxSlippageBps) throw Bad($"slippage of \"{name}\" is out of range.");
        }
        var accounts = a.Portfolios.Select(p => p.Id).ToHashSet();
        void Owned(int accountId, string what)
        {
            if (!accounts.Contains(accountId)) throw Bad($"{what} belongs to a portfolio that isn't in the file.");
        }

        Unique(a.Positions.Select(p => p.Id), "position id");
        Unique(a.Positions.Select(p => (p.AccountId, p.Symbol)), "holding (same symbol twice in one portfolio)");
        foreach (var p in a.Positions)
        {
            Owned(p.AccountId, "a holding");
            Sym(p.Symbol, "holdings");
            if (p.Quantity <= 0) throw Bad($"holding {p.Symbol} has no shares.");
            Money(p.AvgCost, $"average cost of {p.Symbol}");
        }

        Unique(a.Orders.Select(o => o.Id), "order id");
        foreach (var o in a.Orders)
        {
            Owned(o.AccountId, "an order");
            Sym(o.Symbol, "orders");
            if (!Enum.IsDefined(o.Side) || !Enum.IsDefined(o.Type) || !Enum.IsDefined(o.Status)) throw Bad($"order {o.Id} has an unknown side, type or status.");
            if (o.Quantity <= 0 || o.Price <= 0) throw Bad($"order {o.Id} has no quantity or price.");
            if (o.Reason is { Length: > 300 }) throw Bad($"order {o.Id}'s reason is too long.");
        }
        var orderAccounts = a.Orders.ToDictionary(o => o.Id, o => o.AccountId);

        Unique(a.Trades.Select(t => t.Id), "trade id");
        foreach (var t in a.Trades)
        {
            Owned(t.AccountId, "a trade");
            Sym(t.Symbol, "trades");
            if (!Enum.IsDefined(t.Side)) throw Bad($"trade {t.Id} has an unknown side.");
            if (t.Quantity <= 0 || t.Price <= 0) throw Bad($"trade {t.Id} has no quantity or price.");
            Money(t.Commission, $"commission of trade {t.Id}");
            Money(t.Slippage, $"slippage of trade {t.Id}");
            if (t.OrderId is int oid && (!orderAccounts.TryGetValue(oid, out var acct) || acct != t.AccountId))
                throw Bad($"trade {t.Id} points at an order that isn't in its portfolio.");
        }

        Unique(a.Snapshots.Select(s => s.Id), "snapshot id");
        foreach (var s in a.Snapshots)
        {
            Owned(s.AccountId, "a value snapshot");
            Money(s.TotalValue, "a snapshot value");
        }

        if (a.Watchlist.Count > WatchlistService.MaxItems) throw Bad($"the watchlist has more than {WatchlistService.MaxItems} symbols.");
        Unique(a.Watchlist.Select(w => w.Symbol), "watchlist symbol");
        foreach (var w in a.Watchlist) Sym(w.Symbol, "the watchlist");

        Unique(a.Alerts.Select(x => x.Id), "alert id");
        foreach (var x in a.Alerts)
        {
            Sym(x.Symbol, "alerts");
            if (!Enum.IsDefined(x.Direction) || x.Price <= 0) throw Bad($"alert {x.Id} is invalid.");
        }

        Unique(a.Splits.Select(x => x.Id), "split id");
        foreach (var x in a.Splits)
        {
            Sym(x.Symbol, "splits");
            if (x.From <= 0 || x.To <= 0) throw Bad($"split {x.Id} has an invalid ratio.");
        }
    }

    private static string Truncate(string s) => s.Length <= 24 ? s : s[..24] + "…";
}

/// <summary>Takes a backup at startup if the newest is more than a day old, then checks hourly.</summary>
public class BackupWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<BackupWorker> log) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan BackupEvery = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckEvery);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
                var latest = backups.LatestAt();
                if (latest is null || time.GetUtcNow().UtcDateTime - latest.Value >= BackupEvery)
                    await backups.CreateAsync(ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Automatic backup failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
