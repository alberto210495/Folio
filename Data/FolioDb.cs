using Microsoft.EntityFrameworkCore;

namespace Folio.Data;

public class FolioDb(DbContextOptions<FolioDb> options) : DbContext(options)
{
    public const decimal DefaultStartingCash = 100_000m;
    public const string DefaultName = "My portfolio";

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<PortfolioSnapshot> Snapshots => Set<PortfolioSnapshot>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<WatchlistItem> Watchlist => Set<WatchlistItem>();
    public DbSet<StockSplit> Splits => Set<StockSplit>();
    public DbSet<PriceAlert> Alerts => Set<PriceAlert>();

    // SQLite has no timezone-aware type; everything is stored as UTC, so mark it UTC on the way out.
    protected override void ConfigureConventions(ModelConfigurationBuilder c) =>
        c.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>().Property(a => a.Name).HasMaxLength(40);

        // Every row below belongs to one portfolio; deleting the portfolio deletes them.
        b.Entity<Position>().HasOne<Account>().WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Trade>().HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Order>().HasOne<Account>().WithMany().HasForeignKey(o => o.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PortfolioSnapshot>().HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Position>().HasIndex(p => new { p.AccountId, p.Symbol }).IsUnique();
        b.Entity<Position>().Property(p => p.Symbol).HasMaxLength(20);

        b.Entity<Trade>().HasIndex(t => new { t.AccountId, t.ExecutedAt });
        b.Entity<Trade>().Property(t => t.Symbol).HasMaxLength(20);
        b.Entity<Trade>().Property(t => t.Side).HasConversion<string>().HasMaxLength(4);

        b.Entity<Company>().HasKey(c => c.Symbol);
        b.Entity<Company>().Property(c => c.Symbol).HasMaxLength(20);

        b.Entity<PortfolioSnapshot>().HasIndex(s => new { s.AccountId, s.TakenAt });

        b.Entity<Trade>().HasIndex(t => t.OrderId);
        b.Entity<Order>().HasIndex(o => o.Status);
        b.Entity<Order>().Property(o => o.Symbol).HasMaxLength(20);
        b.Entity<Order>().Property(o => o.Side).HasConversion<string>().HasMaxLength(4);
        b.Entity<Order>().Property(o => o.Type).HasConversion<string>().HasMaxLength(5);
        b.Entity<Order>().Property(o => o.Status).HasConversion<string>().HasMaxLength(9);
        b.Entity<Order>().Property(o => o.Reason).HasMaxLength(300);

        b.Entity<WatchlistItem>().HasKey(w => w.Symbol);
        b.Entity<WatchlistItem>().Property(w => w.Symbol).HasMaxLength(20);
        b.Entity<WatchlistItem>().HasIndex(w => w.AddedAt);

        b.Entity<StockSplit>().Property(s => s.Symbol).HasMaxLength(20);
        b.Entity<StockSplit>().HasIndex(s => new { s.Symbol, s.AppliedAt });

        b.Entity<PriceAlert>().Property(a => a.Symbol).HasMaxLength(20);
        b.Entity<PriceAlert>().Property(a => a.Direction).HasConversion<string>().HasMaxLength(5);
        b.Entity<PriceAlert>().HasIndex(a => new { a.TriggeredAt, a.Symbol });
    }

    /// <summary>The portfolio with this id (tracked), or <see cref="PortfolioNotFoundException"/>.</summary>
    public async Task<Account> GetAccountAsync(int id, CancellationToken ct = default) =>
        await Accounts.FindAsync([id], ct) ?? throw new PortfolioNotFoundException(id);

    /// <summary>The first portfolio, creating "My portfolio" with $100,000 if there are none.</summary>
    public async Task<Account> GetAccountAsync(CancellationToken ct = default) =>
        await Accounts.FindAsync([await DefaultAccountIdAsync(ct)], ct) ?? throw new PortfolioNotFoundException(0);

    public async Task<int> DefaultAccountIdAsync(CancellationToken ct = default)
    {
        var id = await Accounts.OrderBy(a => a.Id).Select(a => (int?)a.Id).FirstOrDefaultAsync(ct);
        if (id is not null) return id.Value;

        var account = new Account { Name = DefaultName, StartingCash = DefaultStartingCash, Cash = DefaultStartingCash };
        Accounts.Add(account);
        await SaveChangesAsync(ct);
        return account.Id;
    }
}

public class PortfolioNotFoundException(int id) : Exception($"Portfolio {id} not found.")
{
    public int Id { get; } = id;
}
