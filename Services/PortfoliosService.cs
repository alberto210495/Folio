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

/// <param name="Commission">Dollars charged per fill.</param>
/// <param name="SlippageBps">Basis points (0.01%) added to buys and taken off sells.</param>
public record PortfolioSummary(int Id, string Name, decimal StartingCash, decimal Cash, decimal TotalValue, decimal TotalReturn,
    decimal TotalReturnPercent, int Holdings, DateTime CreatedAt, decimal Commission = 0, decimal SlippageBps = 0);

/// <param name="Commission">Optional dollars per fill; 0 when left out.</param>
/// <param name="SlippageBps">Optional basis points of slippage; 0 when left out.</param>
public record CreatePortfolioRequest(string Name, decimal StartingCash, decimal? Commission = null, decimal? SlippageBps = null);

/// <summary>Changes whichever fields are given. New costs apply to fills from now on.</summary>
public record UpdatePortfolioRequest(string? Name = null, decimal? Commission = null, decimal? SlippageBps = null);

/// <summary>Creates, renames, deletes and lists portfolios.</summary>
public class PortfoliosService(FolioDb db, PortfolioService valuation, TimeProvider time)
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
            list.Add(new(a.Id, a.Name, a.StartingCash, v.Cash, v.TotalValue, v.TotalReturn, v.TotalReturnPercent, v.Holdings.Count, a.CreatedAt,
                a.Commission, a.SlippageBps));
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

        var commission = ValidCommission(req.Commission ?? 0);
        var slippage = ValidSlippage(req.SlippageBps ?? 0);

        var cash = decimal.Round(req.StartingCash, 2, MidpointRounding.ToEven);
        var account = new Account
        {
            Name = name, StartingCash = cash, Cash = cash, CreatedAt = time.GetUtcNow().UtcDateTime,
            Commission = commission, SlippageBps = slippage,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return new(account.Id, account.Name, cash, cash, cash, 0, 0, 0, account.CreatedAt, commission, slippage);
    }

    public Task RenameAsync(int id, string? newName, CancellationToken ct = default) =>
        UpdateAsync(id, new UpdatePortfolioRequest(Name: newName ?? ""), ct);

    /// <summary>Renames and/or changes trading costs. Fields left null are kept.</summary>
    public async Task UpdateAsync(int id, UpdatePortfolioRequest req, CancellationToken ct = default)
    {
        var account = await db.GetAccountAsync(id, ct);
        if (req.Name is not null) account.Name = await ValidNameAsync(req.Name, exceptId: id, ct);
        if (req.Commission is { } c) account.Commission = ValidCommission(c);
        if (req.SlippageBps is { } s) account.SlippageBps = ValidSlippage(s);
        await db.SaveChangesAsync(ct);
    }

    public static decimal ValidCommission(decimal v)
    {
        if (v is < 0 or > TradingService.MaxCommission)
            throw new TradeRejectedException("Commission must be between $0 and $1,000 per trade.");
        if (decimal.Round(v, 2) != v) throw new TradeRejectedException("Commission supports at most 2 decimal places (cents).");
        return v;
    }

    public static decimal ValidSlippage(decimal bps)
    {
        if (bps is < 0 or > TradingService.MaxSlippageBps)
            throw new TradeRejectedException($"Slippage must be between 0% and {TradingService.MaxSlippageBps / 100:0.##}%.");
        if (decimal.Round(bps, 2) != bps) throw new TradeRejectedException("Slippage supports at most 0.0001% precision.");
        return bps;
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
