// A small stand-in for Folio's server: enough of the real API, with the same JSON shapes (camelCase, enums as
// strings), for the page to boot and trade. It keeps a ledger, so tests can check what actually got booked, and
// honours clientOrderId the way the server does. Tests can script the next reply to any request with once().

const NOW = "2026-11-18T15:00:00Z";

export class FakeApi {
  constructor() {
    this.requests = [];      // every request the page made: { method, path, query, body }
    this.unknown = [];       // requests to routes the fake doesn't know (a test should keep this empty)
    this.inflight = 0;
    this.overrides = [];     // one-shot scripted replies, see once()
    this.prices = { AAPL: 200, MSFT: 400, SPY: 500 };
    this.names = { AAPL: "Apple Inc", MSFT: "Microsoft Corp" };
    this.cash = 100_000;
    this.startingCash = 100_000;
    this.positions = new Map(); // symbol -> { quantity, avgCost }
    this.trades = [];
    this.orders = [];
    this.market = { isOpen: true, closedReason: null, closesAt: "2026-11-18T21:00:00Z", nextOpen: "2026-11-19T14:30:00Z", earlyClose: false, enforced: true };
    this.watchlist = [];
    this.locked = false;
    this.nextId = 1;
  }

  /**
   * Scripts the next matching request. `reply(ctx)` gets { method, path, query, body, proceed } and returns
   * { status, body } or throws (a network failure). `proceed()` runs the normal handler, so a test can let the
   * server book an order and then lose the answer.
   */
  once(method, path, reply) {
    this.overrides.push({ method, path, reply });
  }

  /** Requests to one route, e.g. posts("/api/trades"). */
  posts(path) {
    return this.requests.filter((r) => r.method === "POST" && r.path === path);
  }

  fetch = async (input, init = {}) => {
    const url = new URL(String(input), "https://localhost:7246/");
    const method = (init.method || "GET").toUpperCase();
    const body = init.body ? JSON.parse(init.body) : null;
    const ctx = { method, path: url.pathname, query: url.searchParams, body };
    this.requests.push({ method, path: url.pathname, query: Object.fromEntries(url.searchParams), body });
    this.inflight++;
    try {
      // Let the page get on with other work first, like a real network round trip.
      await new Promise((r) => setImmediate(r));
      const i = this.overrides.findIndex((o) => o.method === method && o.path === url.pathname);
      let result;
      if (i >= 0) {
        const [o] = this.overrides.splice(i, 1);
        result = await o.reply({ ...ctx, proceed: () => this.handle(ctx) });
      } else {
        result = this.handle(ctx);
      }
      const { status = 200, body: out = null } = result;
      return new Response(status === 204 ? null : JSON.stringify(out), {
        status,
        headers: { "Content-Type": "application/json" },
      });
    } finally {
      this.inflight--;
    }
  };

  handle({ method, path, query, body }) {
    const ok = (b) => ({ status: 200, body: b });
    const error = (status, message) => ({ status, body: { error: message } });
    const route = `${method} ${path}`;
    let m;

    // Without the access cookie, the real server answers every API request with 401.
    if (this.locked) return error(401, "Folio is locked. Open it from the Folio shortcut or the tray icon.");

    if (route === "GET /api/status") return ok({ finnhubConfigured: true, streaming: false, streamConnected: false });
    if (route === "GET /api/market") return ok(this.market);
    if (route === "GET /api/portfolios") return ok([this.summary()]);
    if (route === "GET /api/portfolio") return ok(this.portfolio());
    if (route === "GET /api/trades") return ok(this.tradePage(Number(query.get("page") || 1), Number(query.get("pageSize") || 25)));
    if (route === "GET /api/history") return ok({ startingCash: this.startingCash, benchmarkSymbol: "SPY", benchmarkNow: this.prices.SPY, points: [], benchmarkBasePrice: null });
    if (route === "GET /api/orders") {
      const open = query.get("status") === "open";
      return ok(this.orders.filter((o) => (o.status === "Open") === open));
    }
    if (route === "GET /api/watchlist") return ok(this.watchlist);
    if (route === "GET /api/performance") return ok(PERFORMANCE);
    if (route === "GET /api/alerts") return ok({ active: [], triggered: [] });
    if (route === "GET /api/search") return ok([]);
    if ((m = route.match(/^GET \/api\/quote\/(.+)$/))) {
      const sym = decodeURIComponent(m[1]).toUpperCase();
      return sym in this.prices ? ok(this.quote(sym)) : error(404, `No quote for ${sym}.`);
    }
    if ((m = route.match(/^GET \/api\/company\/(.+)$/))) {
      const sym = decodeURIComponent(m[1]).toUpperCase();
      return ok({ symbol: sym, name: this.names[sym] ?? null, industry: "Technology", logo: null });
    }
    if (route === "POST /api/trades") return this.trade(body);
    if (route === "POST /api/orders") return this.placeOrder(body);

    this.unknown.push(route);
    return error(404, `Fake API has no route for ${route}.`);
  }

  // ---------- trading, with the server's clientOrderId rules ----------

  trade({ symbol, side, quantity, clientOrderId }) {
    if (clientOrderId != null) {
      const earlier = this.trades.find((t) => t.clientOrderId === clientOrderId);
      if (earlier) {
        return earlier.symbol === symbol && earlier.side === side && earlier.quantity === quantity
          ? { status: 201, body: earlier }
          : { status: 400, body: { error: "This order ID was already used for a different order. Reload the page and try again." } };
      }
    }
    const price = this.prices[symbol];
    if (price == null) return { status: 400, body: { error: `No quote found for ${symbol}.` } };
    const pos = this.positions.get(symbol) ?? { quantity: 0, avgCost: 0 };
    let realizedPnl = null;
    if (side === "Buy") {
      const cost = price * quantity;
      if (cost > this.cash) return { status: 400, body: { error: `Insufficient cash: order costs $${cost.toFixed(2)}, you have $${this.cash.toFixed(2)}.` } };
      this.cash -= cost;
      pos.avgCost = (pos.quantity * pos.avgCost + cost) / (pos.quantity + quantity);
      pos.quantity += quantity;
    } else {
      if (pos.quantity < quantity) return { status: 400, body: { error: `You only hold ${pos.quantity} shares of ${symbol}.` } };
      realizedPnl = (price - pos.avgCost) * quantity;
      this.cash += price * quantity;
      pos.quantity -= quantity;
    }
    if (pos.quantity > 0) this.positions.set(symbol, pos);
    else this.positions.delete(symbol);
    const trade = {
      id: this.nextId++, accountId: 1, symbol, side, quantity, price, realizedPnl, commission: 0, slippage: 0,
      orderId: null, clientOrderId: clientOrderId ?? null, executedAt: NOW,
    };
    this.trades.push(trade);
    return { status: 201, body: trade };
  }

  placeOrder({ symbol, side, type, quantity, price, clientOrderId }) {
    if (clientOrderId != null) {
      const earlier = this.orders.find((o) => o.clientOrderId === clientOrderId);
      if (earlier) return { status: 201, body: this.orderView(earlier) };
    }
    const order = {
      id: this.nextId++, symbol, side, type, quantity, price, status: "Open", createdAt: NOW, closedAt: null,
      fillPrice: null, reason: null, clientOrderId: clientOrderId ?? null,
    };
    this.orders.push(order);
    return { status: 201, body: this.orderView(order) };
  }

  orderView(o) {
    const { clientOrderId, ...view } = o;
    return { ...view, currentPrice: this.prices[o.symbol] ?? null };
  }

  // ---------- views, shaped like the server's records ----------

  quote(symbol) {
    const price = this.prices[symbol];
    return { symbol, price, change: 1, changePercent: 0.5, previousClose: price - 1, asOf: NOW, stale: false };
  }

  holdings() {
    return [...this.positions].map(([symbol, p]) => {
      const price = this.prices[symbol];
      const marketValue = price * p.quantity;
      const costBasis = p.avgCost * p.quantity;
      return {
        symbol, name: this.names[symbol] ?? null, sector: "Technology", logo: null, quantity: p.quantity,
        avgCost: p.avgCost, costBasis, price, marketValue, unrealizedPnl: marketValue - costBasis,
        unrealizedPnlPercent: costBasis ? ((marketValue - costBasis) / costBasis) * 100 : 0,
        dayChange: p.quantity, dayChangePercent: 0.5, previousClose: price - 1, priceStale: false,
      };
    });
  }

  portfolio() {
    const holdings = this.holdings();
    const holdingsValue = holdings.reduce((s, h) => s + h.marketValue, 0);
    const totalValue = this.cash + holdingsValue;
    return {
      startingCash: this.startingCash, cash: this.cash, holdingsValue, totalValue,
      totalReturn: totalValue - this.startingCash, totalReturnPercent: ((totalValue - this.startingCash) / this.startingCash) * 100,
      unrealizedPnl: holdings.reduce((s, h) => s + h.unrealizedPnl, 0), realizedPnl: 0,
      dayChange: holdings.reduce((s, h) => s + h.dayChange, 0), holdings, anyStale: false, asOf: NOW,
    };
  }

  summary() {
    const p = this.portfolio();
    return {
      id: 1, name: "My portfolio", startingCash: p.startingCash, cash: p.cash, totalValue: p.totalValue,
      totalReturn: p.totalReturn, totalReturnPercent: p.totalReturnPercent, holdings: p.holdings.length,
      createdAt: "2026-11-01T15:00:00Z", commission: 0, slippageBps: 0,
    };
  }

  tradePage(page, pageSize) {
    const items = [...this.trades].reverse().map((t) => ({
      id: t.id, symbol: t.symbol, side: t.side, quantity: t.quantity, price: t.price, realizedPnl: t.realizedPnl,
      executedAt: t.executedAt, orderId: t.orderId, orderType: "Market", commission: t.commission, slippage: t.slippage,
    }));
    const pages = Math.max(1, Math.ceil(items.length / pageSize));
    const p = Math.min(Math.max(1, page), pages);
    return { items: items.slice((p - 1) * pageSize, p * pageSize), total: items.length, page: p, pageSize, pages };
  }
}

const PERFORMANCE = {
  trades: {
    closed: 0, wins: 0, losses: 0, breakeven: 0, winRate: null, avgWin: null, avgLoss: null, avgWinPercent: null,
    avgLossPercent: null, largestWin: null, largestLoss: null, profitFactor: null, expectancy: null, grossProfit: 0, grossLoss: 0,
  },
  drawdown: {
    points: 0, since: null, highWater: null, highWaterAt: null, currentAmount: 0, currentPercent: 0, maxAmount: 0,
    maxPercent: 0, peakAt: null, troughAt: null, recoveredAt: null, liveIncluded: false,
  },
  costs: { commission: 0, slippage: 0, total: 0, fills: 0 },
};

/** A reply that drops the connection, like a timeout or a closed laptop lid. */
export const networkError = () => {
  throw new TypeError("Failed to fetch");
};

/** The server books the order, then the answer is lost: a network failure, or an error status. */
export const bookThen = (failure) => async ({ proceed }) => {
  proceed();
  if (failure === "network") networkError();
  return { status: failure, body: { error: "Something went wrong." } };
};
