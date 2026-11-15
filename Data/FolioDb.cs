using Microsoft.EntityFrameworkCore;

namespace Folio.Data;

public class FolioDb(DbContextOptions<FolioDb> options) : DbContext(options)
{
    public const int AccountId = 1;
    public const decimal DefaultStartingCash = 100_000m;

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<PortfolioSnapshot> Snapshots => Set<PortfolioSnapshot>();

    // SQLite has no timezone-aware type; everything is stored as UTC, so mark it UTC on the way out.
    protected override void ConfigureConventions(ModelConfigurationBuilder c) =>
        c.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Position>().HasIndex(p => p.Symbol).IsUnique();
        b.Entity<Position>().Property(p => p.Symbol).HasMaxLength(20);

        b.Entity<Trade>().HasIndex(t => t.ExecutedAt);
        b.Entity<Trade>().Property(t => t.Symbol).HasMaxLength(20);
        b.Entity<Trade>().Property(t => t.Side).HasConversion<string>().HasMaxLength(4);

        b.Entity<Company>().HasKey(c => c.Symbol);
        b.Entity<Company>().Property(c => c.Symbol).HasMaxLength(20);

        b.Entity<PortfolioSnapshot>().HasIndex(s => s.TakenAt);
    }

    public async Task<Account> GetAccountAsync(CancellationToken ct = default)
    {
        var account = await Accounts.FindAsync([AccountId], ct);
        if (account is not null) return account;

        account = new Account
        {
            Id = AccountId,
            StartingCash = DefaultStartingCash,
            Cash = DefaultStartingCash,
        };
        Accounts.Add(account);
        await SaveChangesAsync(ct);
        return account;
    }
}
