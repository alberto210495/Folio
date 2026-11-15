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
- **Cost basis** is the weighted average. Selling books realized P&L = (fill − avg cost) × shares.
- **Unrealized P&L** and **Today** use live quotes. Total return = (cash + holdings) − starting cash.
- **Sector** comes from Finnhub's `/stock/profile2` and is cached in the `Companies` table on first buy. ETFs have no profile on the free tier, so they show as "Other".
- **Value over time:** a background job saves the account value at startup and every 15 minutes while the app runs, and again after each trade. A snapshot is skipped if any price is stale, or if the value hasn't changed since the last one (for example, when markets are closed). The chart always ends at the current live value. History only covers periods when Folio was running.
- **S&P 500 comparison:** each snapshot also stores SPY's price. The orange line shows what your starting cash would be worth if it had all gone into SPY when the account started. You can change the ticker with `Folio:BenchmarkSymbol` in `appsettings.json`. Free Finnhub has no historical prices, so the comparison begins when the first snapshot is saved.
- **Rate limits:** the free tier allows 60 calls/min. Quotes are cached for 15s (`Finnhub:QuoteCacheSeconds`) and the UI refreshes every 60s while the tab is visible. If Finnhub fails, holdings show the last known price marked *stale*, and trading pauses until live prices return.

## API

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/portfolio` | Cash, holdings with live P&L, totals |
| POST | `/api/trades` | `{ "symbol": "AAPL", "side": "Buy" \| "Sell", "quantity": 10 }` |
| GET | `/api/trades?limit=100` | Trade history, newest first |
| GET | `/api/history?range=1M` | Value snapshots; range is `1D`, `1W`, `1M`, `3M`, `1Y` or `ALL` (max 400 points) |
| GET | `/api/quote/{symbol}` | Cached live quote |
| GET | `/api/search?q=apple` | US symbol search |
| POST | `/api/portfolio/reset` | `{ "startingCash": 100000 }` (wipes positions and trades) |

Errors return `{ "error": "..." }`: 400 for rejected orders, 429 or 503 for Finnhub problems.

## Tests

```sh
dotnet test Folio.slnx
```

xUnit tests in `tests/Folio.Tests` cover trading (average cost, realized P&L, rounding, rejections), reset, valuation, snapshots and the benchmark maths. They use a fake Finnhub and an in-memory SQLite database with the real migrations, so they need neither an API key nor a network connection.

## Notes

- The schema is managed by EF Core migrations (in `Data/Migrations`) and applied automatically at startup. After changing an entity, run `dotnet tool restore` once, then `dotnet dotnet-ef migrations add <Name> -o Data/Migrations`.
- Single account, single user, no auth. Intended to run locally.
