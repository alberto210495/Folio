using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

/// <summary>
/// The portfolio the current request or job works on. API requests pick it with <c>?portfolio=&lt;id&gt;</c>
/// (the first portfolio when omitted); background jobs set it per portfolio.
/// </summary>
public class PortfolioContext
{
    private int? _id;

    public int Id
    {
        get => _id ?? throw new InvalidOperationException("No portfolio selected.");
        set => _id = value;
    }
}

public record PortfolioSummary(int Id, string Name, decimal StartingCash, decimal Cash, decimal TotalValue, decimal TotalReturn,
    decimal TotalReturnPercent, int Holdings, DateTime CreatedAt);

public record CreatePortfolioRequest(string Name, decimal StartingCash);

public record RenamePortfolioRequest(string Name);

/// <summary>Creates, renames, deletes and lists portfolios.</summary>
public class PortfoliosService(FolioDb db, PortfolioService valuation)
{
    public const int MaxPortfolios = 20;
    public const int MaxNameLength = 40;

    /// <summary>Every portfolio with its value at live prices, oldest first.</summary>
    public async Task<IReadOnlyList<PortfolioSummary>> ListAsync(CancellationToken ct = default)
    {
        await db.DefaultAccountIdAsync(ct); // never return an empty list
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var list = new List<PortfolioSummary>();
        foreach (var a in accounts)
        {
            var v = await valuation.GetAsync(a.Id, ct); // quotes are cached and shared across portfolios
            list.Add(new(a.Id, a.Name, a.StartingCash, v.Cash, v.TotalValue, v.TotalReturn, v.TotalReturnPercent, v.Holdings.Count, a.CreatedAt));
        }
        return list;
    }

    public async Task<PortfolioSummary> CreateAsync(CreatePortfolioRequest req, CancellationToken ct = default)
    {
        var name = await ValidNameAsync(req.Name, exceptId: null, ct);
        if (req.StartingCash is <= 0 or > 1_000_000_000m)
            throw new TradeRejectedException("Starting cash must be between $1 and $1,000,000,000.");
        if (await db.Accounts.CountAsync(ct) >= MaxPortfolios)
            throw new TradeRejectedException($"You can have up to {MaxPortfolios} portfolios. Delete one to add another.");

        var cash = decimal.Round(req.StartingCash, 2, MidpointRounding.ToEven);
        var account = new Account { Name = name, StartingCash = cash, Cash = cash };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return new(account.Id, account.Name, cash, cash, cash, 0, 0, 0, account.CreatedAt);
    }

    public async Task RenameAsync(int id, string? newName, CancellationToken ct = default)
    {
        var account = await db.GetAccountAsync(id, ct);
        account.Name = await ValidNameAsync(newName, exceptId: id, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the portfolio and everything in it. The last portfolio can't be deleted.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await db.GetAccountAsync(id, ct); // 404 if it doesn't exist
        if (await db.Accounts.CountAsync(ct) <= 1)
            throw new TradeRejectedException("You can't delete your only portfolio. Reset it instead.");

        // Foreign keys cascade too; deleting explicitly keeps it independent of SQLite's foreign_keys setting.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Trades.Where(t => t.AccountId == id).ExecuteDeleteAsync(ct);
        await db.Orders.Where(o => o.AccountId == id).ExecuteDeleteAsync(ct);
        await db.Positions.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
        await db.Snapshots.Where(s => s.AccountId == id).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<string> ValidNameAsync(string? raw, int? exceptId, CancellationToken ct)
    {
        var name = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new TradeRejectedException("Give the portfolio a name.");
        if (name.Length > MaxNameLength) throw new TradeRejectedException($"Portfolio names can be up to {MaxNameLength} characters.");

        var lower = name.ToLowerInvariant();
        var taken = await db.Accounts.AnyAsync(a => a.Id != exceptId && a.Name.ToLower() == lower, ct);
        if (taken) throw new TradeRejectedException($"You already have a portfolio called \"{name}\".");
        return name;
    }
}
