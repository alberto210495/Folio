using Folio.Data;
using Folio.Services;

namespace Folio.Tests;

public class ExportServiceTests
{
    private static ExportService Export(TestApp.Scope s) => new(s.Db, s.Portfolio);

    private static string[] Lines(CsvFile f) => f.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("=HYPERLINK(\"x\",\"y\")", "\"'=HYPERLINK(\"\"x\"\",\"\"y\"\")\"")]
    [InlineData("", "")]
    public void Text_cells_are_quoted_and_defused(string input, string expected) =>
        Assert.Equal(expected, Csv.Text(input));

    [Fact]
    public void Numbers_stay_numeric_and_dates_are_utc_iso()
    {
        var row = new Csv("a").Row(-12.50m, 3, null, true, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), TradeSide.Sell).ToString();
        Assert.EndsWith("-12.5,3,,true,2026-01-02T03:04:05Z,Sell\r\n", row);
    }

    [Fact]
    public void Numbers_ignore_the_current_culture()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("de-DE"); // decimal comma
            Assert.EndsWith("1234.56\r\n", new Csv("a").Row(1234.56m).ToString());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public async Task Trades_export_lists_every_fill_oldest_first_with_order_type()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using (var s = app.NewScope()) await s.Buy("AAPL", 10);
        app.Finnhub.Prices["AAPL"] = 120m; // new scopes so the 15s quote cache sees the new price
        using (var s = app.NewScope()) await s.Sell("AAPL", 4);
        using (var s = app.NewScope()) await s.Place("AAPL", TradeSide.Sell, OrderType.Limit, 2, 110m); // marketable: fills now

        using var check = app.NewScope();
        var file = await Export(check).TradesAsync();
        var lines = Lines(file);

        Assert.Matches(@"^folio-trades-\d{4}-\d{2}-\d{2}\.csv$", file.FileName);
        Assert.Equal("executed_at_utc,side,order_type,symbol,quantity,price,total,realized_pnl,order_id", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Matches(@"Z,Buy,Market,AAPL,10,100,1000,,$", lines[1]);
        Assert.Matches(@"Z,Sell,Market,AAPL,4,120,480,80,$", lines[2]);
        Assert.Matches(@"Z,Sell,Limit,AAPL,2,120,240,40,\d+$", lines[3]);
    }

    [Fact]
    public async Task Holdings_export_includes_live_values_and_a_cash_row()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using var s = app.NewScope();
        await s.Buy("AAPL", 10);
        app.Finnhub.Prices["AAPL"] = 150m;

        using var fresh = app.NewScope();
        var lines = Lines(await Export(fresh).HoldingsAsync());

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("AAPL,AAPL Inc,Technology,10,100,1000,150,1500,500,50,", lines[1]);
        Assert.EndsWith(",false", lines[1]);
        Assert.StartsWith("CASH,Cash,,,,,,99000,", lines[2]);
    }

    [Fact]
    public async Task Orders_export_includes_every_status_and_reason()
    {
        using var app = new TestApp();
        app.Finnhub.Prices["AAPL"] = 100m;
        using var s = app.NewScope();
        var open = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 90m);
        var cancelled = await s.Place("AAPL", TradeSide.Buy, OrderType.Limit, 1, 80m);
        await s.Orders.CancelAsync(cancelled.Id);

        var lines = Lines(await Export(s).OrdersAsync());

        Assert.Equal(3, lines.Length);
        Assert.Contains($"{open.Id},", lines[1]);
        Assert.Contains(",Buy,Limit,AAPL,1,90,Open,,", lines[1]);
        Assert.Contains(",Cancelled,", lines[2]);
    }

    [Fact]
    public async Task Empty_account_exports_headers_only()
    {
        using var app = new TestApp();
        using var s = app.NewScope();
        Assert.Single(Lines(await Export(s).TradesAsync()));
        Assert.Single(Lines(await Export(s).OrdersAsync()));
    }
}
