# Folio

Paper-trading portfolio tracker. Buy and sell real tickers with fake money at live Finnhub prices, track P&L, and see your allocation by holding and by sector.

**Stack:** ASP.NET Core 10 minimal API · EF Core 10 + SQLite · vanilla HTML/CSS/JS (no build step) · Finnhub.

## Run

1. Get a free API key at <https://finnhub.io/register>.
2. Store it with user secrets (stays out of the repo):

   ```sh
   dotnet user-secrets set Finnhub:ApiKey <your-key>
   ```

   Or use an environment variable: `Finnhub__ApiKey=<your-key>`.
3. `dotnet run`, then open the URL it prints.

The SQLite file `folio.db` is created on first run. The account starts with $100,000. Use **Reset account** to start over with any amount.

## How it works

- **Orders** are market orders filled at Finnhub's latest price (`/quote`). Fractional shares are allowed, up to 4 decimals.
- **Market hours:** trades only execute during the regular US session, 9:30 AM–4:00 PM ET on weekdays. Exchange holidays and 1:00 PM early closes (the day after Thanksgiving, Christmas Eve, July 3) are worked out from NYSE's published rules, so this needs no API calls; one-off closures announced at short notice aren't known. While the market is closed, market orders are refused and the top bar shows when it next opens. Limit and stop orders can still be placed; one that's already marketable waits for the open instead of filling on the last close. To trade around the clock at the last price, set `Folio:EnforceMarketHours` to `false`.
- **Limit and stop orders** stay open until filled or cancelled. During market hours a background job checks them against live prices every 60 seconds (`Folio:OrderCheckSeconds`). When the condition is met, the order fills at the market price seen at that check:
  - Buy limit: fills when the price is at or below the limit.
  - Sell limit: fills when the price is at or above the limit.
  - Buy stop: fills when the price rises to the stop or higher.
  - Sell stop (stop-loss): fills when the price falls to the stop or lower.

  A limit that can already fill when you place it fills immediately. A stop that would trigger immediately is refused. New orders can't commit more cash or shares than you have once other open orders are counted, but nothing is reserved: market orders can spend it, in which case the resting order is rejected when it triggers. A price that crosses your trigger and comes back between two checks won't fill.
- **Watchlist:** follow up to 25 symbols you don't hold, with live price and today's change. Add a ticker in the Watchlist card, or tap ☆ next to a quote in the order form. Tap a symbol or **Buy** to load it into the order form. Resetting the account keeps your watchlist.
- **CSV export:** **Export CSV** on Holdings, Orders and Trade history downloads that table in full (every trade and order, not just what's on screen). Times are UTC in ISO 8601, numbers use `.` as the decimal point, and the holdings file ends with a `CASH` row. Text that a spreadsheet could run as a formula (starting with `=`, `+`, `-` or `@`) gets a leading `'`.
- **Cost basis** is the weighted average. Selling books realized P&L = (fill − avg cost) × shares.
- **Unrealized P&L** and **Today** use live quotes. Total return = (cash + holdings) − starting cash.
- **Sector, name and logo** come from Finnhub's `/stock/profile2`. They're fetched once per symbol (on first buy, watch or lookup) and cached in the `Companies` table. ETFs have no profile on the free tier, so they show as "Other" with a letter tile instead of a logo. Logo images load straight from Finnhub's image host, without a referrer; only `https` URLs are used.
- **Value over time:** a background job saves the account value at startup and every 15 minutes while the app runs, and again after each trade. A snapshot is skipped if any price is stale, or if the value hasn't changed since the last one (for example, when markets are closed). The chart always ends at the current live value. History only covers periods when Folio was running.
- **S&P 500 comparison:** each snapshot also stores SPY's price. The orange line shows what your starting cash would be worth if it had all gone into SPY when the account started. You can change the ticker with `Folio:BenchmarkSymbol` in `appsettings.json`. Free Finnhub has no historical prices, so the comparison begins when the first snapshot is saved.
- **Rate limits:** the free tier allows 60 calls/min. Quotes are cached for 15s (`Finnhub:QuoteCacheSeconds`) and the UI refreshes every 60s while the tab is visible. Each refresh costs about one call per holding and watched symbol, plus SPY, which is why the watchlist is capped at 25. If Finnhub fails, holdings show the last known price marked *stale*, and trading pauses until live prices return.

## API

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/market` | Whether the US market is open, when it closes or next opens, and the reason it's closed |
| GET | `/api/portfolio` | Cash, holdings with live P&L, totals |
| POST | `/api/trades` | `{ "symbol": "AAPL", "side": "Buy" \| "Sell", "quantity": 10 }` |
| GET | `/api/trades?limit=100` | Trade history, newest first; `orderType` is `Market`, `Limit` or `Stop` |
| POST | `/api/orders` | `{ "symbol": "AAPL", "side": "Buy", "type": "Limit" \| "Stop", "quantity": 10, "price": 190 }` |
| GET | `/api/orders?status=open` | `status` is `open`, `closed` or `all`; open orders include `currentPrice` |
| DELETE | `/api/orders/{id}` | Cancel an open order |
| GET | `/api/watchlist` | Watched symbols with live quotes and shares held |
| POST | `/api/watchlist` | `{ "symbol": "NVDA" }` (adding twice is a no-op) |
| DELETE | `/api/watchlist/{symbol}` | Remove a symbol |
| GET | `/api/export/trades.csv` | Every trade, oldest first |
| GET | `/api/export/holdings.csv` | Holdings at live prices, plus a `CASH` row |
| GET | `/api/export/orders.csv` | Every order, any status |
| GET | `/api/history?range=1M` | Value snapshots; range is `1D`, `1W`, `1M`, `3M`, `1Y` or `ALL` (max 400 points) |
| GET | `/api/quote/{symbol}` | Cached live quote |
| GET | `/api/search?q=apple` | US symbol search |
| GET | `/api/company/{symbol}` | Name, industry and logo URL (cached after the first lookup) |
| POST | `/api/portfolio/reset` | `{ "startingCash": 100000 }` (wipes positions, trades and orders; keeps the watchlist) |

Errors return `{ "error": "..." }`: 400 for rejected orders, 429 or 503 for Finnhub problems.

## Tests

```sh
dotnet test Folio.slnx
```

xUnit tests in `tests/Folio.Tests` cover trading (average cost, realized P&L, rounding, rejections), market hours (NYSE holidays, early closes, daylight saving, closed-market trading), limit and stop orders (trigger rules, fills, commitments, cancels, stale prices), company profiles and logos, the watchlist, CSV export (quoting, formula escaping, locale), reset, valuation, snapshots and the benchmark maths. They use a fake Finnhub and an in-memory SQLite database with the real migrations, so they need neither an API key nor a network connection.

## Notes

- The schema is managed by EF Core migrations (in `Data/Migrations`) and applied automatically at startup. After changing an entity, run `dotnet tool restore` once, then `dotnet dotnet-ef migrations add <Name> -o Data/Migrations`.
- Single account, single user, no auth. Intended to run locally.
