using System.Globalization;
using System.Text;
using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

/// <summary>
/// RFC 4180 CSV writer. Numbers use the invariant culture and times are ISO 8601 UTC,
/// so files open the same way in any locale.
/// </summary>
public sealed class Csv
{
    private readonly StringBuilder _sb = new();

    public Csv(params string[] header) => Row(header);

    /// <summary>Adds a row. Strings are treated as text; numbers and dates are formatted invariantly.</summary>
    public Csv Row(params object?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(Format(cells[i]));
        }
        _sb.Append("\r\n");
        return this;
    }

    public override string ToString() => _sb.ToString();

    private static string Format(object? v) => v switch
    {
        null => "",
        string s => Text(s),
        // SQLite hands decimals back with arbitrary scale (10.0, 1000.00); write them without trailing zeros.
        decimal d => d.ToString("0.##########", CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime t => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        _ => Text(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
    };

    /// <summary>
    /// Quotes when needed. Text starting with = + - @ (or a tab/CR) gets a leading apostrophe so
    /// spreadsheets show it instead of running it as a formula. Numbers are never text, so
    /// negative values stay numeric.
    /// </summary>
    public static string Text(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}

public record CsvFile(string FileName, string Content);

public class ExportService(FolioDb db, PortfolioService portfolio, PortfolioContext current)
{
    public async Task<CsvFile> TradesAsync(CancellationToken ct = default)
    {
        var trades = await db.Trades.AsNoTracking().Where(t => t.AccountId == current.Id).OrderBy(t => t.ExecutedAt).ThenBy(t => t.Id).ToListAsync(ct);
        var types = await db.Orders.AsNoTracking().Where(o => o.AccountId == current.Id).ToDictionaryAsync(o => o.Id, o => o.Type, ct);

        var csv = new Csv("executed_at_utc", "side", "order_type", "symbol", "quantity", "price", "total", "realized_pnl", "order_id");
        foreach (var t in trades)
        {
            var type = t.OrderId is int id && types.TryGetValue(id, out var ot) ? ot.ToString() : "Market";
            csv.Row(t.ExecutedAt, t.Side, type, t.Symbol, t.Quantity, t.Price,
                decimal.Round(t.Quantity * t.Price, 2, MidpointRounding.ToEven), t.RealizedPnl, t.OrderId);
        }
        return new(await NameAsync("trades", ct), csv.ToString());
    }

    /// <summary>Holdings at live prices, as on the dashboard. Cash is its own row with symbol CASH.</summary>
    public async Task<CsvFile> HoldingsAsync(CancellationToken ct = default)
    {
        var p = await portfolio.GetAsync(ct);
        var csv = new Csv("symbol", "name", "sector", "quantity", "avg_cost", "cost_basis", "price", "market_value",
            "unrealized_pnl", "unrealized_pnl_pct", "day_change", "weight_pct", "price_stale");
        foreach (var h in p.Holdings)
        {
            csv.Row(h.Symbol, h.Name, h.Sector, h.Quantity, h.AvgCost, h.CostBasis, h.Price, h.MarketValue,
                h.UnrealizedPnl, h.UnrealizedPnlPercent, h.DayChange, Weight(h.MarketValue, p.TotalValue), h.PriceStale);
        }
        csv.Row("CASH", "Cash", null, null, null, null, null, p.Cash, null, null, null, Weight(p.Cash, p.TotalValue), false);
        return new(await NameAsync("holdings", ct), csv.ToString());
    }

    public async Task<CsvFile> OrdersAsync(CancellationToken ct = default)
    {
        var orders = await db.Orders.AsNoTracking().Where(o => o.AccountId == current.Id).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync(ct);
        var csv = new Csv("id", "created_at_utc", "closed_at_utc", "side", "type", "symbol", "quantity", "price",
            "status", "fill_price", "reason");
        foreach (var o in orders)
            csv.Row(o.Id, o.CreatedAt, o.ClosedAt, o.Side, o.Type, o.Symbol, o.Quantity, o.Price, o.Status, o.FillPrice, o.Reason);
        return new(await NameAsync("orders", ct), csv.ToString());
    }

    private static decimal Weight(decimal value, decimal total) =>
        total == 0 ? 0 : decimal.Round(value / total * 100, 2, MidpointRounding.ToEven);

    /// <summary>folio-growth-trades-2026-11-15.csv: the portfolio's name, reduced to letters, digits and dashes.</summary>
    private async Task<string> NameAsync(string what, CancellationToken ct)
    {
        var name = await db.Accounts.Where(a => a.Id == current.Id).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? "";
        var slug = Slug(name);
        return $"folio-{(slug.Length > 0 ? slug + "-" : "")}{what}-{DateTime.UtcNow:yyyy-MM-dd}.csv";
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
