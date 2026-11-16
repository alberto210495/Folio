namespace Folio.Data;

/// <summary>A paper-trading portfolio: its own cash, positions, trades, orders and value history.</summary>
public class Account
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal StartingCash { get; set; }
    public decimal Cash { get; set; }
    public decimal RealizedPnl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Benchmark (SPY) price when the account started; baseline for the comparison line.</summary>
    public decimal? BenchmarkStartPrice { get; set; }
    /// <summary>Flat commission charged on every fill, in dollars. 0 = free trading.</summary>
    public decimal Commission { get; set; }
    /// <summary>
    /// Simulated slippage in basis points (1 bp = 0.01%): buys fill this much above the market price, sells
    /// this much below. Limit orders never fill past their limit.
    /// </summary>
    public decimal SlippageBps { get; set; }
}

/// <summary>An open holding. Removed when quantity reaches zero.</summary>
public class Position
{
    public int Id { get; set; }
    public int AccountId { get; set; }
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
    public int AccountId { get; set; }
    public required string Symbol { get; set; }
    public TradeSide Side { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>The fill price, including any slippage.</summary>
    public decimal Price { get; set; }
    /// <summary>Set on sells: (price − avg cost) × quantity − commission. A buy's commission is in its avg cost.</summary>
    public decimal? RealizedPnl { get; set; }
    /// <summary>Commission paid on this fill.</summary>
    public decimal Commission { get; set; }
    /// <summary>What slippage cost on this fill: the gap between the market and fill prices × quantity.</summary>
    public decimal Slippage { get; set; }
    /// <summary>The resting order this fill came from; null for market orders.</summary>
    public int? OrderId { get; set; }
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
    public int AccountId { get; set; }
    public DateTime TakenAt { get; set; } = DateTime.UtcNow;
    public decimal TotalValue { get; set; }
    public decimal Cash { get; set; }
    public decimal HoldingsValue { get; set; }
    /// <summary>Benchmark (SPY) price at the same moment, when it was available.</summary>
    public decimal? BenchmarkPrice { get; set; }
}

public enum OrderType { Limit, Stop }

public enum OrderStatus { Open, Filled, Cancelled, Rejected }

/// <summary>
/// A resting limit or stop order, good until cancelled. Checked against live quotes by
/// <c>OrderWorker</c>; fills at the market price at the moment the condition is seen.
/// </summary>
public class Order
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public required string Symbol { get; set; }
    public TradeSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Limit price or stop (trigger) price.</summary>
    public decimal Price { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public decimal? FillPrice { get; set; }
    /// <summary>Why the order was rejected at fill time (e.g. not enough cash by then).</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// A stock split you recorded: every <see cref="From"/> shares became <see cref="To"/> (4-for-1 is To 4, From 1;
/// a 1-for-10 reverse split is To 1, From 10). Applied to every portfolio's holdings and open orders.
/// </summary>
public class StockSplit
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public int From { get; set; }
    public int To { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}

public enum AlertDirection { Above, Below }

/// <summary>
/// "Tell me when NVDA rises to $130" (Above) or "falls to $110" (Below). Fires once, the first time a live
/// price reaches the target, then stays as history. Shared by every portfolio, like the watchlist.
/// </summary>
public class PriceAlert
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public AlertDirection Direction { get; set; }
    public decimal Price { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When it fired; null while it's still waiting.</summary>
    public DateTime? TriggeredAt { get; set; }
    /// <summary>The price that set it off.</summary>
    public decimal? TriggerPrice { get; set; }
    /// <summary>Whether the page has shown you this alert firing.</summary>
    public bool Seen { get; set; }
}

/// <summary>A symbol you're following without holding it. Not account data, so a reset keeps it.</summary>
public class WatchlistItem
{
    public required string Symbol { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
