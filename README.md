# Folio

Paper-trading portfolio tracker. Buy and sell real tickers with fake money at live Finnhub prices, track P&L, and see your allocation by holding and by sector. Run several portfolios side by side to compare strategies.

**Stack:** ASP.NET Core 10 minimal API · EF Core 10 + SQLite · vanilla HTML/CSS/JS (no build step) · Finnhub.

## Run

1. Get a free API key at <https://finnhub.io/register>.
2. Store it with user secrets (stays out of the repo):

   ```sh
   dotnet user-secrets set Finnhub:ApiKey <your-key>
   ```

   Or use an environment variable: `Finnhub__ApiKey=<your-key>`.
3. `dotnet run`, then open the URL it prints.

The SQLite file `folio.db` is created on first run with one portfolio, "My portfolio", holding $100,000. Use **Reset portfolio** to start it over with any amount, or **+ New** to add another.

## How it works

- **Portfolios:** each portfolio has its own cash, holdings, orders, trade history, value chart and S&P comparison. The tabs at the top show every portfolio's value and return; click one to switch, and Folio remembers your choice. **+ New** creates one with any starting cash and, optionally, trading costs. **Settings** renames the open one, changes its trading costs or deletes it (up to 20 portfolios; the last one can't be deleted, only reset). The watchlist and company data are shared. Background jobs cover every portfolio: orders fill into the portfolio they were placed in, and each portfolio gets its own value snapshots.
- **Orders** are market orders filled at Finnhub's latest price (`/quote`). Fractional shares are allowed, up to 4 decimals.
- **Market hours:** trades only execute during the regular US session, 9:30 AM–4:00 PM ET on weekdays. Exchange holidays and 1:00 PM early closes (the day after Thanksgiving, Christmas Eve, July 3) are worked out from NYSE's published rules, so this needs no API calls; one-off closures announced at short notice aren't known. While the market is closed, market orders are refused and the top bar shows when it next opens. Limit and stop orders can still be placed; one that's already marketable waits for the open instead of filling on the last close. To trade around the clock at the last price, set `Folio:EnforceMarketHours` to `false`.
- **Live prices:** Folio keeps one websocket open to Finnhub (the free tier allows one connection and 50 symbols) and streams trade prices for your holdings, open orders, SPY, your watchlist and the symbol in the order form, in that order of priority. The page receives them about once a second over server-sent events (`/api/stream`), so your API key never reaches the browser. Prices update in place with a brief green or red flash, and a **Live** badge shows in the top bar while the market is open. Streamed prices are layered over the REST quote everywhere: valuations, market orders and order fills all use the latest trade. Prints outside regular hours are ignored unless `Folio:EnforceMarketHours` is off. If the stream drops, Folio reconnects with backoff and falls back to the 15-second REST quotes meanwhile. Set `Finnhub:Streaming` to `false` to turn streaming off.
- **Limit and stop orders** stay open until filled or cancelled. During market hours an order is checked as soon as a streamed trade crosses its price, and every 60 seconds as a fallback (`Folio:OrderCheckSeconds`). When the condition is met, the order fills at that market price:
  - Buy limit: fills when the price is at or below the limit.
  - Sell limit: fills when the price is at or above the limit.
  - Buy stop: fills when the price rises to the stop or higher.
  - Sell stop (stop-loss): fills when the price falls to the stop or lower.

  A limit that can already fill when you place it fills immediately. A stop that would trigger immediately is refused. New orders can't commit more cash or shares than you have once other open orders are counted, but nothing is reserved: market orders can spend it, in which case the resting order is rejected when it triggers. With live prices on, a trade that crosses your price fills the order even if the price comes straight back; without them (streaming off or disconnected), a price that crosses and returns between two 60-second checks won't fill.
- **Watchlist:** follow up to 25 symbols you don't hold, with live price and today's change. Add a ticker in the Watchlist card, or tap ☆ next to a quote in the order form. Tap a symbol or **Buy** to load it into the order form. Resetting the account keeps your watchlist.
- **Price alerts:** the bell on a watchlist row sets alerts for that symbol. Enter a target price and Folio works out from the current price whether you're waiting for it to rise or fall. Waiting alerts show under the symbol (▲ $130.00). An alert fires once, the first time a live trade reaches the target: it's checked on every streamed trade, and against quotes every 60 seconds as a fallback, during market hours while Folio is running. When one fires, open pages hear about it straight away over `/api/stream` and show a message. If you've allowed notifications and the page is in the background, you also get a system notification. Alerts that fired while the page was closed are shown the next time you open it, and the last few are listed under **Recent alerts**. A target the price has already reached is refused. Up to 50 alerts can wait at once. Removing a symbol from the watchlist removes its waiting alerts, and a recorded stock split adjusts their prices.
- **CSV export:** **Export CSV** on Holdings, Orders and Trade history downloads that table in full (every trade and order, not just what's on screen). Times are UTC in ISO 8601, numbers use `.` as the decimal point, and the holdings file ends with a `CASH` row. The trades file includes each fill's `commission` and `slippage`. Text that a spreadsheet could run as a formula (starting with `=`, `+`, `-` or `@`) gets a leading `'`.
- **Trade history** is paged on the server, 25 trades per page by default (50 or 100 if you choose; the page remembers your choice). Placing a trade jumps back to page 1 so you see it. **Export CSV** still downloads every trade.
- **Trading costs** are off by default and set per portfolio (in **+ New** or **Settings**), so you can run the same strategy with and without them:
  - **Commission:** a flat fee in dollars on every fill, buy or sell ($0–$1,000).
  - **Slippage**, entered as a % (stored in basis points): buys fill that much above the market price and sells that much below (0–5%). A limit order never fills past its limit; a stop can slip past its stop, as at a real broker.
  - Cash checks include costs: a buy needs cash for its commission too, and open buy orders count their estimated costs. The order form's estimate and **Max** include them.
  - Each trade records the commission and slippage it paid (in the **Costs** column of Trade history and the CSV export), and the Performance card shows the total. New settings apply to trades from then on; resetting a portfolio keeps them.
- **Cost basis** is the weighted average, and includes buy commissions. Selling books realized P&L = (fill − avg cost) × shares − commission, so costs show up in realized and unrealized P&L.
- **Stock splits** aren't detected automatically (the free Finnhub tier has no split data), so record them yourself: **Split** on a holding opens a dialog where you enter the ratio, such as 4 for 1, or 1 for 10 for a reverse split. Every portfolio's shares are multiplied by the ratio and the average cost divided by it, so cost basis and P&L don't change. Open orders for the symbol get the same adjustment. Shares are kept to 4 decimals; anything smaller is paid out as cash at the adjusted cost, and an order left with no shares is cancelled. Each split is saved, and the dialog lists the splits already recorded for that symbol so you don't apply one twice.
- **Performance** (the card under the value chart) covers the open portfolio since it started or was last reset:
  - **Closed trades:** every sell counts as one. It's a win if it realized a profit, a loss if it realized a loss, and breakeven otherwise. Win rate = wins ÷ (wins + losses). Average win and loss are shown in dollars and as a % of what the shares sold had cost. Profit factor = total won ÷ total lost. Per trade is the average realized P&L across all sells.
  - **Drawdown:** the largest fall from a high in account value (when it peaked, bottomed out and recovered), and how far the value is now below its all-time high. It's worked out from the saved value history plus the live value (left out while any price is stale). History is only saved while Folio runs, so a dip between snapshots can be missed.
- **Unrealized P&L** and **Today** use live quotes. Total return = (cash + holdings) − starting cash.
- **Sector, name and logo** come from Finnhub's `/stock/profile2`. They're fetched on first buy, watch or lookup and cached in the `Companies` table, then re-fetched once they're 30 days old, so renames and new logos show up. The snapshot job refreshes up to 5 out-of-date profiles per run for symbols you hold or watch; if a refresh fails, the cached data stays. ETFs have no profile on the free tier, so they show as "Other" with a letter tile instead of a logo. Logo images load straight from Finnhub's image host, without a referrer; only `https` URLs are used.
- **Value over time:** a background job saves the account value at startup and every 15 minutes while the app runs, and again after each trade. A snapshot is skipped if any price is stale, or if the value hasn't changed since the last one (for example, when markets are closed). The chart always ends at the current live value. History only covers periods when Folio was running.
- **S&P 500 comparison:** each snapshot also stores SPY's price. The orange line shows what your starting cash would be worth if it had all gone into SPY when the account started. You can change the ticker with `Folio:BenchmarkSymbol` in `appsettings.json`. Free Finnhub has no historical prices, so the comparison begins when the first snapshot is saved.
- **Rate limits:** the free tier allows 60 calls/min. Quotes are cached for 15s (`Finnhub:QuoteCacheSeconds`) and the UI refreshes every 60s while the tab is visible. Each refresh costs about one call per holding and watched symbol, plus SPY, which is why the watchlist is capped at 25. If Finnhub fails, holdings show the last known price marked *stale*, and trading pauses until live prices return.

## API

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/stream` | Server-sent events: `status` (`{ connected }`) and `prices` (`{ "AAPL": { p, t } }`), at most once a second, and `alerts` (`{ version }`) when a price alert fires |
| GET | `/api/market` | Whether the US market is open, when it closes or next opens, and the reason it's closed |
| GET | `/api/portfolios` | Every portfolio with its live value and return |
| POST | `/api/portfolios` | `{ "name": "Growth", "startingCash": 25000, "commission": 4.95, "slippageBps": 5 }` (costs optional, default 0) |
| PATCH | `/api/portfolios/{id}` | Any of `{ "name": "Long term", "commission": 0, "slippageBps": 10 }`; fields left out are kept |
| DELETE | `/api/portfolios/{id}` | Deletes the portfolio and everything in it (not the last one) |
| GET | `/api/portfolio` | Cash, holdings with live P&L, totals |
| POST | `/api/trades` | `{ "symbol": "AAPL", "side": "Buy" \| "Sell", "quantity": 10 }` |
| GET | `/api/trades?page=1&pageSize=25` | One page of trade history, newest first: `{ items, total, page, pageSize, pages }`. `pageSize` is 1–200; a page past the end returns the last page. Each item's `orderType` is `Market`, `Limit` or `Stop`; `price` is the fill price after slippage, with `commission` and `slippage` in dollars |
| POST | `/api/orders` | `{ "symbol": "AAPL", "side": "Buy", "type": "Limit" \| "Stop", "quantity": 10, "price": 190 }` |
| GET | `/api/orders?status=open` | `status` is `open`, `closed` or `all`; open orders include `currentPrice` |
| DELETE | `/api/orders/{id}` | Cancel an open order |
| POST | `/api/splits` | `{ "symbol": "AAPL", "from": 1, "to": 4 }` for a 4-for-1 split (ratios 1–1000). Applies to every portfolio; returns the holdings and orders adjusted |
| GET | `/api/splits?symbol=AAPL` | Recorded splits, newest first (all symbols when `symbol` is omitted) |
| GET | `/api/watchlist` | Watched symbols with live quotes and shares held |
| POST | `/api/watchlist` | `{ "symbol": "NVDA" }` (adding twice is a no-op) |
| DELETE | `/api/watchlist/{symbol}` | Remove a symbol (and its waiting price alerts) |
| GET | `/api/alerts` | `{ active, triggered }`: waiting alerts, and the last 20 that fired (newest first) |
| POST | `/api/alerts` | `{ "symbol": "NVDA", "price": 130 }` on a watchlist symbol. `direction` (`Above` or `Below`) is optional and worked out from the current price |
| DELETE | `/api/alerts/{id}` | Remove an alert |
| DELETE | `/api/alerts/triggered` | Clear the list of alerts that fired |
| POST | `/api/alerts/seen` | `{ "ids": [4, 5] }`: mark fired alerts as shown |
| GET | `/api/export/trades.csv` | Every trade, oldest first |
| GET | `/api/export/holdings.csv` | Holdings at live prices, plus a `CASH` row |
| GET | `/api/export/orders.csv` | Every order, any status |
| GET | `/api/performance` | `{ trades, drawdown, costs }`: win rate, average win and loss, profit factor, best and worst sell; largest and current drawdown; commission and slippage paid. Percentages are 0–100; empty values are `null` |
| GET | `/api/history?range=1M` | Value snapshots; range is `1D`, `1W`, `1M`, `3M`, `1Y` or `ALL` (max 400 points) |
| GET | `/api/quote/{symbol}` | Cached live quote |
| GET | `/api/search?q=apple` | US symbol search |
| GET | `/api/company/{symbol}` | Name, industry and logo URL (cached after the first lookup) |
| POST | `/api/portfolio/reset` | `{ "startingCash": 100000 }` (wipes the portfolio's positions, trades, orders and history; keeps the watchlist) |

Portfolio-specific endpoints (portfolio, trades, orders, history, exports, reset, and the watchlist's shares held) work on the portfolio given by `?portfolio=<id>`, or the first portfolio when it's omitted.

Errors return `{ "error": "..." }`: 400 for rejected orders, 404 for an unknown portfolio, 429 or 503 for Finnhub problems.

## Tests

```sh
dotnet test Folio.slnx
```

xUnit tests in `tests/Folio.Tests` cover multiple portfolios (separation of cash, holdings, orders and history; fills into the right portfolio; reset; validation; deletion), trading (average cost, realized P&L, rounding, rejections, invalid sides), stock splits (forward and reverse, every portfolio, open orders, fractions, validation), timestamps from the app clock, company profile refresh, performance stats (win rate, averages, profit factor, drawdown and recovery), trading costs (fill prices, limit caps, cost basis, cash checks, settings), price alerts (direction, refusals, firing once, market hours, history, watchlist removal, splits, the stream event), live prices (merging ticks into quotes, message parsing, subscriptions, the event stream, tick-triggered fills), market hours (NYSE holidays, early closes, daylight saving, closed-market trading), limit and stop orders (trigger rules, fills, commitments, cancels, stale prices), company profiles and logos, the watchlist, trade history paging, CSV export (quoting, formula escaping, locale), reset, valuation, snapshots and the benchmark maths. They use a fake Finnhub and an in-memory SQLite database with the real migrations, so they need neither an API key nor a network connection. One optional test connects to the real Finnhub websocket when a `FINNHUB_KEY` environment variable is set; it streams a crypto pair, so it works even when US markets are closed.

## Notes

- The schema is managed by EF Core migrations (in `Data/Migrations`) and applied automatically at startup. After changing an entity, run `dotnet tool restore` once, then `dotnet dotnet-ef migrations add <Name> -o Data/Migrations`.
- Single user, no auth. Intended to run locally.
