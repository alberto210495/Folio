namespace Folio.Data;

/// <summary>The paper-trading account. Folio runs a single account (Id = 1).</summary>
public class Account
{
    public int Id { get; set; }
    public decimal StartingCash { get; set; }
    public decimal Cash { get; set; }
    public decimal RealizedPnl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Benchmark (SPY) price when the account started; baseline for the comparison line.</summary>
    public decimal? BenchmarkStartPrice { get; set; }
}

/// <summary>An open holding. Removed when quantity reaches zero.</summary>
public class Position
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Weighted-average cost per share.</summary>
    public decimal AvgCost { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
}

public enum TradeSide { Buy, Sell }

/// <summary>Immutable ledger of every fill.</summary>
public class Trade
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public TradeSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    /// <summary>Set on sells: (price - avg cost) × quantity.</summary>
    public decimal? RealizedPnl { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Company profile cached from Finnhub so we don't refetch it per request.</summary>
public class Company
{
    public required string Symbol { get; set; }
    public string? Name { get; set; }
    public string? Industry { get; set; }
    public string? Logo { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Point-in-time account value, for the value-over-time chart.</summary>
public class PortfolioSnapshot
{
    public int Id { get; set; }
    public DateTime TakenAt { get; set; } = DateTime.UtcNow;
    public decimal TotalValue { get; set; }
    public decimal Cash { get; set; }
    public decimal HoldingsValue { get; set; }
    /// <summary>Benchmark (SPY) price at the same moment, when it was available.</summary>
    public decimal? BenchmarkPrice { get; set; }
}
