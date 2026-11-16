// Folio front end: vanilla JS, no build step.

const REFRESH_MS = 60_000; // Quotes are cached 15s server-side; 60s keeps us far from Finnhub's 60/min.
const MAX_SLOTS = 7;       // Named allocation segments; the rest fold into "Other".

const $ = (sel) => document.querySelector(sel);
const usd = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const pct = new Intl.NumberFormat("en-US", { style: "percent", minimumFractionDigits: 1, maximumFractionDigits: 1 });
const shares = new Intl.NumberFormat("en-US", { maximumFractionDigits: 4 });
const timeFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

const state = { portfolio: null, quote: null, allocMode: "holding", configured: true, history: null, range: "1M", histClear: null, allocClear: null, orders: null, knownOpenOrders: null, watchlist: [], market: null, tradePage: 1, tradePageSize: 25, streaming: false, live: { open: false, upstream: false }, portfolios: [], portfolioId: null, alerts: { active: [], triggered: [] } };

// ---------- helpers ----------
function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function signed(v, fmt = usd) {
  if (v === 0) return fmt.format(0);
  return (v > 0 ? "+" : "−") + fmt.format(Math.abs(v));
}
function tone(v) { return v > 0 ? "up" : v < 0 ? "down" : ""; }
function arrow(v) { return v > 0 ? "▲ " : v < 0 ? "▼ " : ""; }
function pnl(v, p) {
  const pctPart = p === undefined ? "" : ` (${signed(p / 100, pct)})`;
  return `<span class="${tone(v)}">${arrow(v)}${signed(v)}${pctPart}</span>`;
}

// Company logo on a white tile, or the first letter when there's no logo or it fails to load.
const failedLogos = new Set();
function logo(sym, url) {
  const letter = esc((sym || "?")[0]);
  return url && url.startsWith("https://") && !failedLogos.has(url)
    ? `<span class="co-logo"><img src="${esc(url)}" alt="" loading="lazy" referrerpolicy="no-referrer" data-letter="${letter}"></span>`
    : `<span class="co-logo mono" aria-hidden="true">${letter}</span>`;
}
// Image errors don't bubble, so listen in the capture phase.
document.addEventListener("error", (ev) => {
  const img = ev.target;
  if (!(img instanceof HTMLImageElement) || !img.parentElement?.classList.contains("co-logo")) return;
  failedLogos.add(img.getAttribute("src"));
  const box = img.parentElement;
  box.classList.add("mono");
  box.setAttribute("aria-hidden", "true");
  box.textContent = img.dataset.letter;
}, true);

async function api(path, opts = {}) {
  if (state.portfolioId && path.startsWith("/api/") && !path.startsWith("/api/portfolios")) {
    path += (path.includes("?") ? "&" : "?") + `portfolio=${state.portfolioId}`;
  }
  const res = await fetch(path, {
    ...opts,
    headers: { "Content-Type": "application/json", ...(opts.headers || {}) },
  });
  if (res.status === 204) return null;
  const body = await res.json().catch(() => null);
  if (!res.ok) throw new Error(body?.error || `Request failed (${res.status})`);
  return body;
}

// Enter in a dialog's text or number field would press the form's *first* button, which in every
// dialog here is Cancel (or "Delete portfolio"). Press the dialog's main button, marked data-default, instead.
document.addEventListener("keydown", (ev) => {
  if (ev.key !== "Enter" || ev.isComposing || ev.defaultPrevented) return;
  const input = ev.target;
  if (!(input instanceof HTMLInputElement) || !input.form || !input.closest("dialog")) return;
  if (["checkbox", "radio", "button", "submit", "reset"].includes(input.type)) return;
  const main = input.form.querySelector("button[data-default]");
  if (!main || main.disabled) return;
  ev.preventDefault();
  input.form.requestSubmit(main);
});

function banner(html) {
  const el = $("#banner");
  el.hidden = !html;
  el.innerHTML = html || "";
}

// ---------- Portfolios ----------
const PORTFOLIO_KEY = "folio.portfolio";
try { state.portfolioId = Number(localStorage.getItem(PORTFOLIO_KEY)) || null; } catch { /* storage unavailable */ }

const currentPortfolio = () => state.portfolios.find((p) => p.id === state.portfolioId);

/** The open portfolio's trading costs: commission in dollars, slippage in basis points. */
function costs() {
  const p = currentPortfolio();
  return { fee: p?.commission ?? 0, bps: p?.slippageBps ?? 0 };
}

/** Commission plus slippage paid on one trade, with the split in a tooltip. */
function tradeCosts(t) {
  const total = (t.commission ?? 0) + (t.slippage ?? 0);
  if (!total) return `<span class="muted">—</span>`;
  return `<span title="${usd.format(t.commission ?? 0)} commission + ${usd.format(t.slippage ?? 0)} slippage">${usd.format(total)}</span>`;
}

async function loadPortfolios() {
  state.portfolios = await api("/api/portfolios");
  // Fall back to the first portfolio if the remembered one was deleted (or nothing is remembered yet).
  if (!currentPortfolio()) setPortfolioId(state.portfolios[0]?.id ?? null);
  renderPortfolios();
}

function setPortfolioId(id) {
  state.portfolioId = id;
  try { localStorage.setItem(PORTFOLIO_KEY, String(id)); } catch { /* ignore */ }
  // CSV links are plain downloads, so they carry the portfolio in their URL.
  for (const a of document.querySelectorAll("a[data-export]"))
    a.href = `/api/export/${a.dataset.export}.csv?portfolio=${id}`;
}

function renderPortfolios() {
  const tabs = $("#pf-tabs");
  tabs.innerHTML = state.portfolios.map((p) => {
    // The open portfolio shows its live value; the others use the last refresh.
    const live = p.id === state.portfolioId && state.portfolio ? state.portfolio : null;
    const value = live ? live.totalValue : p.totalValue;
    const retPct = live ? live.totalReturnPercent : p.totalReturnPercent;
    return `
      <button type="button" role="tab" class="pf-tab" data-pf="${p.id}" aria-selected="${p.id === state.portfolioId}">
        <span class="pf-name">${esc(p.name)}</span>
        <span class="pf-val">${usd.format(value)} <span class="${tone(retPct)}">${signed(retPct / 100, pct)}</span></span>
      </button>`;
  }).join("");
  const cur = currentPortfolio();
  $("#reset-name").textContent = cur ? `“${cur.name}”` : "this portfolio";
  document.title = cur && state.portfolios.length > 1 ? `${cur.name} · Folio` : "Folio";
}

/** Updates just the open tab's value as live prices arrive. */
function paintPortfolioTab() {
  const p = state.portfolio;
  const val = document.querySelector(`.pf-tab[data-pf="${state.portfolioId}"] .pf-val`);
  if (p && val) val.innerHTML = `${usd.format(p.totalValue)} <span class="${tone(p.totalReturnPercent)}">${signed(p.totalReturnPercent / 100, pct)}</span>`;
}

async function switchPortfolio(id) {
  if (id === state.portfolioId) return;
  setPortfolioId(id);
  // Per-portfolio view state starts fresh; the order form keeps its symbol.
  state.tradePage = 1;
  state.knownOpenOrders = null; // don't announce the other portfolio's orders as fills
  $("#trade-msg").textContent = "";
  renderPortfolios();
  await load();
}

$("#pf-tabs").addEventListener("click", (ev) => {
  const tab = ev.target.closest("[data-pf]");
  if (tab) switchPortfolio(Number(tab.dataset.pf));
});

// Arrow keys move between tabs.
$("#pf-tabs").addEventListener("keydown", (ev) => {
  if (ev.key !== "ArrowRight" && ev.key !== "ArrowLeft") return;
  const tabs = [...document.querySelectorAll(".pf-tab")];
  const i = tabs.indexOf(document.activeElement);
  if (i < 0) return;
  const next = tabs[(i + (ev.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
  next.focus();
  switchPortfolio(Number(next.dataset.pf));
});

// Trading costs: the page shows slippage in %, the API takes basis points (1 bp = 0.01%).
const toBps = (pctText) => Math.round((parseFloat(pctText) || 0) * 10000) / 100;
const toPct = (bps) => String(+(bps / 100).toFixed(4));
const feeOf = (text) => parseFloat(text) || 0;

// New portfolio
const newDlg = $("#pf-new-dlg");
$("#pf-new").addEventListener("click", () => {
  $("#pf-new-name").value = "";
  $("#pf-new-cash").value = 100000;
  $("#pf-new-fee").value = 0;
  $("#pf-new-slip").value = 0;
  newDlg.querySelector("details.costs").open = false;
  $("#pf-new-msg").textContent = "";
  newDlg.showModal();
  $("#pf-new-name").focus();
});
newDlg.querySelector("form").addEventListener("submit", async (ev) => {
  if (ev.submitter?.value !== "ok") return;
  ev.preventDefault(); // stay open until the server accepts it
  try {
    const created = await api("/api/portfolios", {
      method: "POST",
      body: JSON.stringify({
        name: $("#pf-new-name").value,
        startingCash: parseFloat($("#pf-new-cash").value),
        commission: feeOf($("#pf-new-fee").value),
        slippageBps: toBps($("#pf-new-slip").value),
      }),
    });
    newDlg.close();
    await loadPortfolios();
    await switchPortfolio(created.id);
    toast(`Created “${created.name}” with ${usd.format(created.startingCash)}.`);
  } catch (e) {
    $("#pf-new-msg").textContent = e.message;
  }
});

// Settings: name, trading costs, delete
const manageDlg = $("#pf-manage-dlg");
$("#pf-manage").addEventListener("click", () => {
  const cur = currentPortfolio();
  if (!cur) return;
  $("#pf-rename").value = cur.name;
  $("#pf-fee").value = cur.commission ?? 0;
  $("#pf-slip").value = toPct(cur.slippageBps ?? 0);
  $("#pf-manage-msg").textContent = "";
  const only = state.portfolios.length <= 1;
  $("#pf-delete").disabled = only;
  $("#pf-delete").title = only ? "You can't delete your only portfolio. Use Reset portfolio instead." : "";
  manageDlg.showModal();
  $("#pf-rename").select();
});
manageDlg.querySelector("form").addEventListener("submit", async (ev) => {
  const action = ev.submitter?.value;
  if (action !== "rename" && action !== "delete") return;
  ev.preventDefault();
  const cur = currentPortfolio();
  try {
    if (action === "rename") {
      await api(`/api/portfolios/${cur.id}`, {
        method: "PATCH",
        body: JSON.stringify({ name: $("#pf-rename").value, commission: feeOf($("#pf-fee").value), slippageBps: toBps($("#pf-slip").value) }),
      });
      manageDlg.close();
      await loadPortfolios();
      updateTicket(); // the order estimate uses the new costs
    } else {
      if (!confirm(`Delete “${cur.name}” with all its positions, trades, orders and history? This can't be undone.`)) return;
      await api(`/api/portfolios/${cur.id}`, { method: "DELETE" });
      manageDlg.close();
      state.portfolioId = null;
      await loadPortfolios(); // falls back to the first portfolio
      state.tradePage = 1;
      state.knownOpenOrders = null;
      await load();
      toast(`Deleted “${cur.name}”.`);
    }
  } catch (e) {
    $("#pf-manage-msg").textContent = e.message;
  }
});

// ---------- Loading ----------
let loadSeq = 0;

async function load() {
  const seq = ++loadSeq;
  try {
    const [portfolio, trades, history, openOrders, closedOrders, watchlist, market, portfolios, performance, alerts] = await Promise.all([
      api("/api/portfolio"),
      api(tradesUrl()),
      api(`/api/history?range=${state.range}`).catch(() => null),
      api("/api/orders?status=open"),
      api("/api/orders?status=closed&limit=100"),
      api("/api/watchlist").catch(() => state.watchlist),
      api("/api/market").catch(() => state.market),
      api("/api/portfolios").catch(() => state.portfolios),
      api("/api/performance").catch(() => null),
      api("/api/alerts").catch(() => state.alerts),
    ]);
    if (seq !== loadSeq) return; // you switched portfolios while this was loading
    notifyClosedOrders(openOrders, closedOrders);
    state.portfolio = portfolio;
    state.history = history;
    state.orders = { open: openOrders, closed: closedOrders };
    state.watchlist = watchlist;
    state.market = market;
    state.portfolios = portfolios;
    state.alerts = alerts;
    renderPortfolios();
    renderMarket();
    updateTicket();
    renderHistory();
    renderKpis(portfolio);
    renderPerformance(performance);
    renderAllocation();
    renderHoldings(portfolio);
    renderOrders();
    renderWatchlist();
    renderAlertHistory();
    announceAlerts();
    renderTrades(trades);
    $("#updated").textContent = `Updated ${new Date().toLocaleTimeString()}`;

    if (!state.configured) {
      banner("No Finnhub API key configured, so quotes and trading are off. Run <code>dotnet user-secrets set Finnhub:ApiKey &lt;your-key&gt;</code> and restart. Free keys: finnhub.io.");
    } else if (portfolio.anyStale) {
      banner("Some prices couldn't be refreshed (Finnhub rate limit or outage). They show the last known price, marked <b>stale</b>.");
    } else {
      banner("");
    }
  } catch (e) {
    if (seq !== loadSeq) return;
    // The open portfolio was deleted elsewhere (another tab): fall back to the first one.
    if (/portfolio doesn't exist/.test(e.message)) {
      state.portfolioId = null;
      await loadPortfolios();
      return load();
    }
    banner(esc(e.message));
  }
}

// ---------- Market hours ----------
const ET = "America/New_York";
const etDay = (d) => new Intl.DateTimeFormat("en-US", { timeZone: ET, dateStyle: "short" }).format(d);

/** "4:00 PM ET" today, "Mon 9:30 AM ET" on another day. Market times are always shown in New York time. */
function etTime(iso) {
  const d = new Date(iso);
  const sameDay = etDay(d) === etDay(new Date());
  return new Intl.DateTimeFormat("en-US", { timeZone: ET, hour: "numeric", minute: "2-digit", ...(sameDay ? {} : { weekday: "short" }) })
    .format(d) + " ET";
}

const marketClosed = () => state.market && !state.market.isOpen && state.market.enforced;

function renderMarket() {
  renderLive();
  const m = state.market;
  const el = $("#market");
  el.hidden = !m;
  if (!m) return;
  el.classList.toggle("open", m.isOpen);
  if (m.isOpen) {
    el.innerHTML = `<span class="dot"></span>Market open<span class="muted"> · closes ${m.earlyClose ? "early at " : ""}${etTime(m.closesAt)}</span>`;
    el.title = "US stock market regular session";
  } else {
    const why = m.closedReason && !["Weekend", "Overnight"].includes(m.closedReason) ? ` (${m.closedReason})` : "";
    el.innerHTML = `<span class="dot"></span>Market closed${esc(why)}<span class="muted"> · opens ${etTime(m.nextOpen)}</span>`;
    el.title = m.enforced
      ? "Market orders are paused until the open. Limit and stop orders can be placed and are checked once it opens."
      : "Market hours aren't enforced (Folio:EnforceMarketHours is off), so trades fill at the last price.";
  }
}

// ---------- Live prices (server-sent events from /api/stream) ----------
let stream = null;

function connectStream() {
  if (stream || !state.streaming || typeof EventSource === "undefined") return;
  stream = new EventSource("/api/stream");
  stream.onopen = () => { state.live.open = true; renderLive(); };
  stream.onerror = () => { state.live.open = false; renderLive(); }; // EventSource retries by itself
  stream.addEventListener("status", (e) => { state.live.upstream = JSON.parse(e.data).connected; renderLive(); });
  stream.addEventListener("prices", (e) => applyPrices(JSON.parse(e.data)));
  stream.addEventListener("alerts", () => loadAlerts());
}

function disconnectStream() {
  stream?.close();
  stream = null;
  state.live.open = false;
  renderLive();
}

function renderLive() {
  const el = $("#live");
  const show = state.streaming && !!state.market && (state.market.isOpen || !state.market.enforced);
  el.hidden = !show;
  if (!show) return;
  const on = state.live.open && state.live.upstream;
  el.classList.toggle("on", on);
  el.innerHTML = `<span class="dot"></span>${on ? "Live" : "Reconnecting…"}`;
  el.title = on ? "Prices update as trades happen (Finnhub websocket)." : "Live prices are reconnecting; prices refresh every minute meanwhile.";
}

const round2 = (v) => Math.round(v * 100) / 100;

/** Briefly tints a cell green or red when its price moves (skipped with reduced motion). */
function flash(td, dir) {
  if (!dir) return;
  td.classList.remove("tick-up", "tick-down");
  void td.offsetWidth; // restart the animation
  td.classList.add(dir > 0 ? "tick-up" : "tick-down");
}

/** Updates changed cells in place, so buttons in the row aren't replaced mid-click. */
function patchRow(row, cells, dir) {
  for (const [f, html] of Object.entries(cells)) {
    const td = row.querySelector(`[data-f="${f}"]`);
    if (!td || td.innerHTML === html) continue;
    td.innerHTML = html;
    if (f === "price") flash(td, dir);
  }
  row.querySelector(".stale")?.remove(); // a live price isn't stale
}

function applyPrices(prices) {
  const p = state.portfolio;
  const dirs = {};
  const live = (sym) => prices[sym]?.p;

  if (p) {
    for (const h of p.holdings) {
      const px = live(h.symbol);
      if (px == null) continue;
      dirs[h.symbol] = Math.sign(px - h.price);
      h.price = px;
      h.marketValue = round2(h.quantity * px);
      h.unrealizedPnl = round2(h.marketValue - h.costBasis);
      h.unrealizedPnlPercent = h.costBasis ? (h.unrealizedPnl / h.costBasis) * 100 : 0;
      if (h.previousClose) {
        h.dayChange = round2(h.quantity * (px - h.previousClose));
        h.dayChangePercent = ((px - h.previousClose) / h.previousClose) * 100;
      }
      h.priceStale = false;
    }
    p.holdingsValue = round2(p.holdings.reduce((s, h) => s + h.marketValue, 0));
    p.totalValue = round2(p.cash + p.holdingsValue);
    p.totalReturn = round2(p.totalValue - p.startingCash);
    p.totalReturnPercent = p.startingCash ? (p.totalReturn / p.startingCash) * 100 : 0;
    p.unrealizedPnl = round2(p.holdings.reduce((s, h) => s + h.unrealizedPnl, 0));
    p.dayChange = round2(p.holdings.reduce((s, h) => s + h.dayChange, 0));
    renderKpis(p);
    paintPortfolioTab();
    // Weights shift for every row when any price moves, so patch them all.
    for (const row of document.querySelectorAll("#holdings tr[data-sym]")) {
      const h = p.holdings.find((x) => x.symbol === row.dataset.sym);
      if (h) patchRow(row, holdingCells(h, p.totalValue), dirs[h.symbol]);
    }
  }

  for (const w of state.watchlist) {
    const px = live(w.symbol);
    if (px == null) continue;
    const dir = w.price == null ? 0 : Math.sign(px - w.price);
    w.price = px;
    if (w.previousClose) {
      w.change = px - w.previousClose;
      w.changePercent = (w.change / w.previousClose) * 100;
    }
    w.stale = false;
    const row = document.querySelector(`#watchlist tr[data-sym="${CSS.escape(w.symbol)}"]`);
    if (row) patchRow(row, watchCells(w), dir);
  }

  for (const o of state.orders?.open ?? []) {
    const px = live(o.symbol);
    if (px == null) continue;
    const dir = o.currentPrice == null ? 0 : Math.sign(px - o.currentPrice);
    o.currentPrice = px;
    const row = document.querySelector(`#orders tr[data-id="${o.id}"]`);
    if (row) patchRow(row, orderCells(o), dir);
  }

  const q = state.quote;
  if (q && live(q.symbol) != null) {
    const px = live(q.symbol);
    const dir = Math.sign(px - q.price);
    q.price = px;
    q.change = px - q.previousClose;
    q.changePercent = q.previousClose ? (q.change / q.previousClose) * 100 : 0;
    q.stale = false;
    paintQuote();
    flash($("#q-price"), dir);
    updateTicket(); // estimated total follows the price
  }
}

// ---------- KPIs ----------
function renderKpis(p) {
  $("#k-total").textContent = usd.format(p.totalValue);
  $("#k-return").innerHTML = `${pnl(p.totalReturn, p.totalReturnPercent)} <span class="muted">all time on ${usd.format(p.startingCash)}</span>`;
  $("#k-day").innerHTML = pnl(p.dayChange);
  $("#k-cash").textContent = usd.format(p.cash);
  $("#k-unreal").innerHTML = pnl(p.unrealizedPnl);
  $("#k-real").innerHTML = pnl(p.realizedPnl);
}

// ---------- Performance ----------
const dateFmt = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" });
const dateYearFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium" });
/** "Nov 5" this year, "Nov 5, 2025" otherwise. */
const fmtDate = (iso) => {
  const d = new Date(iso);
  return (d.getFullYear() === new Date().getFullYear() ? dateFmt : dateYearFmt).format(d);
};
const plural = (n, one, many = one + "s") => `${n} ${n === 1 ? one : many}`;

function stat(label, valueHtml, subHtml = "") {
  return `<div class="stat"><div class="stat-label">${label}</div><div class="stat-value">${valueHtml}</div>${subHtml ? `<div class="stat-sub">${subHtml}</div>` : ""}</div>`;
}
const money = (v) => v == null ? "—" : `<span class="${tone(v)}">${signed(v)}</span>`;
const pctFine = new Intl.NumberFormat("en-US", { style: "percent", minimumFractionDigits: 2, maximumFractionDigits: 2 });
// API percentages are 0–100. Under 1% gets two decimals, so a small cost shows as −0.01% rather than −0.0%.
const pctOf = (v) => v == null ? "" : signed(v / 100, v !== 0 && Math.abs(v) < 1 ? pctFine : pct);

function renderPerformance(perf) {
  const tradesEl = $("#perf-trades");
  const ddEl = $("#perf-dd");
  if (!perf) {
    tradesEl.innerHTML = ddEl.innerHTML = `<div class="stats-empty">Couldn't load performance stats.</div>`;
    $("#perf-since").textContent = "";
    $("#perf-note").textContent = "";
    return;
  }
  const t = perf.trades;
  const d = perf.drawdown;
  $("#perf-since").textContent = d.since ? `Since ${fmtDate(d.since)}` : "";

  if (!t.closed) {
    tradesEl.innerHTML = `<div class="stats-empty">These appear after your first sell. Each sell counts as one closed trade.</div>`;
  } else {
    const record = [plural(t.wins, "win"), plural(t.losses, "loss", "losses")];
    if (t.breakeven) record.push(`${t.breakeven} even`);
    tradesEl.innerHTML = [
      stat("Win rate", t.winRate == null ? "—" : pct.format(t.winRate / 100), record.join(" · ")),
      stat("Average win", money(t.avgWin), t.avgWinPercent == null ? "No winning sells yet" : `${pctOf(t.avgWinPercent)} on cost`),
      stat("Average loss", money(t.avgLoss), t.avgLossPercent == null ? "No losing sells yet" : `${pctOf(t.avgLossPercent)} on cost`),
      stat("Profit factor", t.profitFactor == null ? "—" : t.profitFactor.toFixed(2),
        t.profitFactor == null ? "No losses yet" : `${usd.format(t.grossProfit)} won ÷ ${usd.format(t.grossLoss)} lost`),
      stat("Per trade", money(t.expectancy), `Average across ${plural(t.closed, "sell")}`),
      stat("Best sell", money(t.largestWin), t.largestWin == null ? "No winning sells yet" : "Largest single gain"),
      stat("Worst sell", money(t.largestLoss), t.largestLoss == null ? "No losing sells yet" : "Largest single loss"),
    ].join("");
  }

  // Trading costs, once there are any (or they're switched on), whether or not anything has been sold.
  const c = perf.costs;
  const { fee, bps } = costs();
  if (c && (c.total > 0 || fee || bps)) {
    const setting = [fee ? `${usd.format(fee)}/trade` : "", bps ? `${toPct(bps)}% slippage` : ""].filter(Boolean).join(" + ") || "now free";
    tradesEl.insertAdjacentHTML("beforeend", stat("Trading costs", money(-c.total),
      `${usd.format(c.commission)} commission + ${usd.format(c.slippage)} slippage over ${plural(c.fills, "trade")}<br>Set to ${esc(setting)}`));
  }

  const max = d.maxPercent > 0
    ? stat("Largest drop", `<span class="down">${pctOf(-d.maxPercent)}</span>`,
        `${signed(-d.maxAmount)} · ${fmtDate(d.peakAt)} to ${fmtDate(d.troughAt)}<br>${d.recoveredAt ? `Recovered ${fmtDate(d.recoveredAt)}` : "Not recovered yet"}`)
    : stat("Largest drop", pct.format(0), "No drop from a high yet");
  const current = d.highWater == null ? ""
    : d.currentPercent > 0
      ? stat("Below high", `<span class="down">${pctOf(-d.currentPercent)}</span>`, `${signed(-d.currentAmount)} under ${usd.format(d.highWater)} on ${fmtDate(d.highWaterAt)}`)
      : stat("Below high", pct.format(0), `At the high of ${usd.format(d.highWater)}`);
  ddEl.innerHTML = max + current;

  const notes = ["Drawdown uses the saved value history, which only covers times Folio was running."];
  if (!d.liveIncluded) notes.push("The current value is left out while some prices are stale.");
  $("#perf-note").textContent = notes.join(" ");
}

// ---------- Value over time ----------
const RANGE_TEXT = { "1D": "past day", "1W": "past week", "1M": "past month", "3M": "past 3 months", "1Y": "past year", ALL: "all time" };
const SVG_NS = "http://www.w3.org/2000/svg";
const compactUsd = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", notation: "compact", maximumFractionDigits: 1 });
const BENCH_KEY = "folio.showBenchmark";

const benchToggle = $("#bench-toggle");
try { benchToggle.checked = localStorage.getItem(BENCH_KEY) !== "0"; } catch { /* storage unavailable */ }
benchToggle.addEventListener("change", () => {
  try { localStorage.setItem(BENCH_KEY, benchToggle.checked ? "1" : "0"); } catch { /* ignore */ }
  renderHistory();
});

async function loadHistory() {
  try {
    state.history = await api(`/api/history?range=${state.range}`);
  } catch {
    state.history = null;
  }
  renderHistory();
}

function historyPoints() {
  const pts = (state.history?.points ?? []).map((p) => ({ t: new Date(p.t), v: p.v, b: p.b }));
  // Extend both lines to live values so the chart always ends at "now".
  const p = state.portfolio;
  if (p && !p.anyStale) pts.push({ t: new Date(), v: p.totalValue, b: state.history?.benchmarkNow ?? null, live: true });
  return pts;
}

function niceStep(span, count) {
  const raw = span / count;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const norm = raw / mag;
  return (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
}

function svgEl(name, attrs, text) {
  const el = document.createElementNS(SVG_NS, name);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  if (text != null) el.textContent = text;
  return el;
}

function pctChange(from, to) {
  return from ? ((to - from) / from) * 100 : 0;
}

function renderHistory() {
  const box = $("#hist-chart");
  const pts = historyPoints();
  const change = $("#hist-change");
  const benchSym = state.history?.benchmarkSymbol ?? "SPY";
  const hasBench = pts.filter((p) => p.b != null).length >= 2;
  const showBench = hasBench && benchToggle.checked;

  $("#bench-legend").hidden = !hasBench;
  $("#bench-name").textContent = benchSym === "SPY" ? "S&P 500 (SPY)" : benchSym;
  state.histClear = null;

  if (pts.length < 2 || pts[pts.length - 1].t - pts[0].t < 60_000) {
    change.innerHTML = "";
    box.innerHTML = `<div class="hist-empty">Not enough history yet.<br>Folio saves your account value every 15 minutes while it's running, and after each trade.</div>`;
    return;
  }

  // Header: change over the range, and how the benchmark did over the same window.
  const first = pts[0].v, last = pts[pts.length - 1].v;
  let header = `${pnl(last - first, pctChange(first, last))} <span class="muted">${RANGE_TEXT[state.range]}</span>`;
  const bPts = pts.filter((p) => p.b != null);
  if (hasBench) {
    const bFirst = bPts[0].b, bLast = bPts[bPts.length - 1].b;
    const bp = pctChange(bFirst, bLast);
    header += ` <span class="muted">· ${esc(benchSym)}</span> <span class="${tone(bp)}">${arrow(bp)}${signed(bp / 100, pct)}</span>`;
  }
  change.innerHTML = header;

  const W = box.clientWidth, H = box.clientHeight;
  const m = { top: 10, right: showBench ? 76 : 12, bottom: 24, left: 64 };
  const iw = W - m.left - m.right, ih = H - m.top - m.bottom;

  // Y domain: data (both lines if shown), padded; starting cash joins only if it's already near the data.
  const values = pts.map((p) => p.v).concat(showBench ? bPts.map((p) => p.b) : []);
  let lo = Math.min(...values), hi = Math.max(...values);
  const ref = state.history?.startingCash;
  const span0 = Math.max(hi - lo, hi * 0.01);
  if (ref != null && ref >= lo - span0 && ref <= hi + span0) { lo = Math.min(lo, ref); hi = Math.max(hi, ref); }
  const span = Math.max(hi - lo, hi * 0.01, 1);
  const step = niceStep(span, 4);
  const y0 = Math.floor((lo - span * 0.08) / step) * step;
  const y1 = Math.ceil((hi + span * 0.08) / step) * step;

  const t0 = +pts[0].t, t1 = +pts[pts.length - 1].t;
  const x = (t) => m.left + ((+t - t0) / (t1 - t0)) * iw;
  const y = (v) => m.top + (1 - (v - y0) / (y1 - y0)) * ih;

  const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}`, role: "img",
    "aria-label": `Portfolio value ${RANGE_TEXT[state.range]}: from ${usd.format(first)} to ${usd.format(last)}` +
      (showBench ? `. ${benchSym} comparison ends at ${usd.format(bPts[bPts.length - 1].b)}` : "") });

  // Grid + y labels
  for (let v = y0; v <= y1 + step / 2; v += step) {
    svg.append(svgEl("line", { class: v === y0 ? "baseline" : "grid-line", x1: m.left, x2: m.left + iw, y1: y(v), y2: y(v) }));
    svg.append(svgEl("text", { class: "axis-label", x: m.left - 8, y: y(v) + 4, "text-anchor": "end" }, compactUsd.format(v)));
  }

  // X labels: time of day for short ranges, dates otherwise
  const short = t1 - t0 < 2 * 86_400_000;
  const xFmt = new Intl.DateTimeFormat(undefined, short ? { hour: "numeric", minute: "2-digit" } : { month: "short", day: "numeric" });
  const ticks = Math.max(2, Math.min(6, Math.floor(iw / 110)));
  for (let i = 0; i <= ticks; i++) {
    const t = t0 + ((t1 - t0) * i) / ticks;
    const anchor = i === 0 ? "start" : i === ticks ? "end" : "middle";
    svg.append(svgEl("text", { class: "axis-label", x: x(t), y: H - 6, "text-anchor": anchor }, xFmt.format(t)));
  }

  // Starting-cash reference
  if (ref != null && ref >= y0 && ref <= y1) {
    svg.append(svgEl("line", { class: "ref-line", x1: m.left, x2: m.left + iw, y1: y(ref), y2: y(ref) }));
    svg.append(svgEl("text", { class: "ref-label", x: m.left + 6, y: y(ref) - 6 }, "Starting cash"));
  }

  // Portfolio area + line
  const d = pts.map((p, i) => `${i ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join("");
  svg.append(svgEl("path", { class: "area", d: `${d}L${x(t1)},${y(y0)}L${x(t0)},${y(y0)}Z` }));

  // Benchmark line (drawn beneath the portfolio line); gaps where no price was recorded.
  if (showBench) {
    let bd = "", pen = false;
    for (const p of pts) {
      if (p.b == null) { pen = false; continue; }
      bd += `${pen ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.b).toFixed(1)}`;
      pen = true;
    }
    svg.append(svgEl("path", { class: "line bench", d: bd }));
  }
  svg.append(svgEl("path", { class: "line", d }));

  // Direct end labels, nudged apart when the lines finish close together.
  if (showBench) {
    let yv = y(last), yb = y(bPts[bPts.length - 1].b);
    const gap = 14;
    if (Math.abs(yv - yb) < gap) {
      const mid = (yv + yb) / 2, dir = yv <= yb ? -1 : 1;
      yv = mid + (dir * gap) / 2;
      yb = mid - (dir * gap) / 2;
    }
    svg.append(svgEl("text", { class: "end-label", x: m.left + iw + 8, y: yv + 4 }, "Portfolio"));
    svg.append(svgEl("text", { class: "end-label", x: m.left + iw + 8, y: yb + 4 }, benchSym));
  }

  // Hover / touch: crosshair + dots + tooltip on the nearest point
  const cross = svgEl("line", { class: "crosshair", y1: m.top, y2: m.top + ih, visibility: "hidden" });
  const dot = svgEl("circle", { class: "dot", r: 5, visibility: "hidden" });
  const bdot = svgEl("circle", { class: "dot", r: 5, visibility: "hidden", style: "fill:var(--s2)" });
  const hit = svgEl("rect", { x: m.left, y: 0, width: iw, height: H, fill: "transparent" });
  svg.append(cross, bdot, dot, hit);

  const tip = $("#hist-tip");
  const tipFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

  const hide = () => {
    tip.hidden = true;
    for (const el of [cross, dot, bdot]) el.setAttribute("visibility", "hidden");
  };

  const show = (ev) => {
    const rect = svg.getBoundingClientRect();
    const tx = t0 + ((ev.clientX - rect.left - m.left) / iw) * (t1 - t0);
    let best = pts[0];
    for (const p of pts) if (Math.abs(p.t - tx) < Math.abs(best.t - tx)) best = p;
    const px = x(best.t), py = y(best.v);
    cross.setAttribute("x1", px); cross.setAttribute("x2", px); cross.setAttribute("visibility", "visible");
    dot.setAttribute("cx", px); dot.setAttribute("cy", py); dot.setAttribute("visibility", "visible");

    let rows = `<div class="row"><span><span class="sw-line" style="background:var(--s1)"></span>Portfolio</span><b>${usd.format(best.v)}</b></div>`;
    if (showBench && best.b != null) {
      bdot.setAttribute("cx", px); bdot.setAttribute("cy", y(best.b)); bdot.setAttribute("visibility", "visible");
      const diff = best.v - best.b;
      rows += `<div class="row"><span><span class="sw-line dash"></span>${esc(benchSym)}</span><b>${usd.format(best.b)}</b></div>`;
      rows += `<div class="row"><span class="muted">${diff >= 0 ? "Ahead by" : "Behind by"}</span><span class="${tone(diff)}">${usd.format(Math.abs(diff))}</span></div>`;
    } else {
      bdot.setAttribute("visibility", "hidden");
      if (ref) rows += `<div class="row"><span class="muted">vs start</span>${pnl(best.v - ref, pctChange(ref, best.v))}</div>`;
    }
    tip.innerHTML = `<div class="muted">${best.live ? "Now" : tipFmt.format(best.t)}</div>${rows}`;
    tip.hidden = false;
    const sx = rect.left + px, sy = rect.top + py;
    tip.style.left = `${Math.max(8, Math.min(sx + 12, window.innerWidth - tip.offsetWidth - 8))}px`;
    tip.style.top = `${Math.max(8, sy - tip.offsetHeight - 12)}px`;
  };

  // Pointer events cover mouse, touch and pen. Touch keeps the tooltip until you tap elsewhere.
  hit.addEventListener("pointerdown", show);
  hit.addEventListener("pointermove", show);
  hit.addEventListener("pointerleave", (ev) => { if (ev.pointerType === "mouse") hide(); });
  state.histClear = hide;

  box.replaceChildren(svg);
}

document.querySelectorAll("[data-range]").forEach((btn) =>
  btn.addEventListener("click", () => {
    state.range = btn.dataset.range;
    document.querySelectorAll("[data-range]").forEach((b) => b.setAttribute("aria-selected", String(b === btn)));
    loadHistory();
  })
);

let resizeRaf;
new ResizeObserver(() => {
  cancelAnimationFrame(resizeRaf);
  resizeRaf = requestAnimationFrame(renderHistory);
}).observe($("#hist-chart"));

// A tap outside a chart dismisses its touch tooltip.
document.addEventListener("pointerdown", (ev) => {
  if (!ev.target.closest("#hist-chart")) state.histClear?.();
  if (!ev.target.closest("#alloc-bar, #alloc-rows")) state.allocClear?.();
});

// ---------- Allocation ----------
function allocationItems(p) {
  let items;
  if (state.allocMode === "sector") {
    const groups = new Map();
    for (const h of p.holdings) {
      const g = groups.get(h.sector) || { key: h.sector, label: h.sector, sub: "", value: 0, count: 0 };
      g.value += h.marketValue;
      g.count += 1;
      groups.set(h.sector, g);
    }
    items = [...groups.values()].map((g) => ({ ...g, sub: `${g.count} holding${g.count > 1 ? "s" : ""}` }));
  } else {
    items = p.holdings.map((h) => ({ key: h.symbol, label: h.symbol, sub: h.name || "", value: h.marketValue }));
  }

  items = items.filter((i) => i.value > 0).sort((a, b) => b.value - a.value);
  const named = items.slice(0, MAX_SLOTS);
  const rest = items.slice(MAX_SLOTS);

  // Color follows the entity, not its rank: slot by alphabetical key among the named set.
  const slotOrder = [...named].sort((a, b) => a.key.localeCompare(b.key));
  for (const it of named) it.color = `var(--s${slotOrder.indexOf(it) + 1})`;

  if (rest.length) {
    named.push({
      key: "__other", label: "Other", sub: `${rest.length} more`,
      value: rest.reduce((s, r) => s + r.value, 0),
      color: "repeating-linear-gradient(45deg, var(--axis) 0 3px, var(--cash) 3px 6px)",
    });
  }
  if (p.cash > 0) named.push({ key: "__cash", label: "Cash", sub: "", value: p.cash, color: "var(--cash)" });

  const total = named.reduce((s, i) => s + i.value, 0);
  for (const it of named) it.weight = total ? it.value / total : 0;
  return named;
}

function renderAllocation() {
  const p = state.portfolio;
  if (!p) return;
  const items = allocationItems(p);
  const bar = $("#alloc-bar");
  const rows = $("#alloc-rows");

  bar.setAttribute("aria-label", "Allocation: " + items.map((i) => `${i.label} ${pct.format(i.weight)}`).join(", "));
  bar.innerHTML = items.length
    ? items.map((i) => `<div class="seg-fill" data-key="${esc(i.key)}" style="flex:${i.weight} 0 0;background:${i.color}"></div>`).join("")
    : `<div class="alloc-empty"></div>`;

  rows.innerHTML = items.map((i) => `
    <tr data-key="${esc(i.key)}">
      <td class="nm"><span class="sw" style="background:${i.color}"></span>${esc(i.label)}${i.sub ? `<small>${esc(i.sub)}</small>` : ""}</td>
      <td class="val">${usd.format(i.value)}</td>
      <td class="pct">${pct.format(i.weight)}</td>
    </tr>`).join("");

  const byKey = new Map(items.map((i) => [i.key, i]));
  const tip = $("#alloc-tip");

  const highlight = (key, ev) => {
    bar.classList.toggle("hovering", !!key);
    bar.querySelectorAll(".seg-fill").forEach((s) => s.classList.toggle("on", s.dataset.key === key));
    rows.querySelectorAll("tr").forEach((r) => r.classList.toggle("on", r.dataset.key === key));
    const it = key && byKey.get(key);
    if (it && ev) {
      tip.hidden = false;
      tip.innerHTML = `<b>${esc(it.label)}</b>${it.sub ? ` · ${esc(it.sub)}` : ""}<br>${usd.format(it.value)} · ${pct.format(it.weight)}`;
      const x = Math.min(ev.clientX + 12, window.innerWidth - tip.offsetWidth - 8);
      tip.style.left = `${x}px`;
      tip.style.top = `${ev.clientY + 14}px`;
    } else {
      tip.hidden = true;
    }
  };

  // Pointer events cover mouse, touch and pen; touch keeps the highlight until you tap elsewhere.
  const fromBar = (ev) => highlight(ev.target.closest(".seg-fill")?.dataset.key, ev);
  const leave = (ev) => { if (ev.pointerType === "mouse") highlight(null); };
  bar.onpointerdown = fromBar;
  bar.onpointermove = fromBar;
  bar.onpointerleave = leave;
  rows.onpointerover = (ev) => highlight(ev.target.closest("tr")?.dataset.key);
  rows.onpointerdown = (ev) => highlight(ev.target.closest("tr")?.dataset.key);
  rows.onpointerleave = leave;
  state.allocClear = () => highlight(null);
}

document.querySelectorAll("[data-alloc]").forEach((btn) =>
  btn.addEventListener("click", () => {
    state.allocMode = btn.dataset.alloc;
    document.querySelectorAll("[data-alloc]").forEach((b) => b.setAttribute("aria-selected", String(b === btn)));
    renderAllocation();
  })
);

// ---------- Holdings & trades ----------
function renderHoldings(p) {
  const tbody = $("#holdings");
  if (!p.holdings.length) {
    tbody.innerHTML = `<tr class="empty"><td colspan="9">No positions yet. Place an order to get started.</td></tr>`;
    return;
  }
  tbody.innerHTML = p.holdings.map((h) => {
    const c = holdingCells(h, p.totalValue);
    return `
    <tr data-sym="${esc(h.symbol)}">
      <td><div class="sym-cell">${logo(h.symbol, h.logo)}<div class="sym-text"><span class="sym">${esc(h.symbol)}</span>${h.priceStale ? `<span class="stale">stale</span>` : ""}<span class="co">${esc(h.name || h.sector)}</span></div></div></td>
      <td class="num">${shares.format(h.quantity)}</td>
      <td class="num">${usd.format(h.avgCost)}</td>
      <td class="num" data-f="price">${c.price}</td>
      <td class="num" data-f="mv">${c.mv}</td>
      <td class="num" data-f="day">${c.day}</td>
      <td class="num" data-f="unreal">${c.unreal}</td>
      <td class="num" data-f="weight">${c.weight}</td>
      <td class="num"><button class="btn xs" data-trade="${esc(h.symbol)}" data-side="Buy">Buy</button> <button class="btn xs" data-trade="${esc(h.symbol)}" data-side="Sell" data-qty="${h.quantity}">Sell</button> <button class="btn xs ghost" type="button" data-split="${esc(h.symbol)}" title="Record a stock split">Split</button></td>
    </tr>`;
  }).join("");
}

/** Cell contents shared by the full render and live patching. */
function holdingCells(h, total) {
  return {
    price: usd.format(h.price),
    mv: usd.format(h.marketValue),
    day: pnl(h.dayChange, h.dayChangePercent),
    unreal: pnl(h.unrealizedPnl, h.unrealizedPnlPercent),
    weight: pct.format(total ? h.marketValue / total : 0),
  };
}

/** Loads a symbol into the order form, e.g. from a Buy/Sell button. */
function openTicket(sym, sideName, qty = "") {
  setSide(sideName);
  $("#symbol").value = sym;
  $("#qty").value = qty;
  selectSymbol(sym);
  $("#qty").focus();
  $("#trade-form").scrollIntoView({ behavior: "smooth", block: "nearest" });
}

$("#holdings").addEventListener("click", (ev) => {
  const split = ev.target.closest("[data-split]");
  if (split) return openSplit(split.dataset.split);
  const btn = ev.target.closest("[data-trade]");
  if (btn) openTicket(btn.dataset.trade, btn.dataset.side, btn.dataset.qty || "");
});

// ---------- Stock splits ----------
const splitDlg = $("#split-dlg");
let splitSym = null;

function splitRatio() {
  return { to: Math.trunc(Number($("#split-to").value)), from: Math.trunc(Number($("#split-from").value)) };
}

function paintSplitPreview() {
  const h = state.portfolio?.holdings.find((x) => x.symbol === splitSym);
  const { to, from } = splitRatio();
  const el = $("#split-preview");
  if (!h || !(to >= 1 && from >= 1) || to === from) { el.textContent = ""; return; }
  const qty = Math.floor((h.quantity * to / from) * 10000) / 10000;
  el.textContent = `In this portfolio: ${shares.format(h.quantity)} shares at ${usd.format(h.avgCost)} become ${shares.format(qty)} at ${usd.format(h.avgCost * from / to)}.`;
}

async function openSplit(sym) {
  splitSym = sym;
  $("#split-sym").textContent = sym;
  $("#split-to").value = 2;
  $("#split-from").value = 1;
  $("#split-msg").textContent = "";
  $("#split-prev").textContent = "";
  paintSplitPreview();
  splitDlg.showModal();
  $("#split-to").select();
  // Applying the same split twice would adjust holdings twice, so show what's already recorded.
  try {
    const prev = await api(`/api/splits?symbol=${encodeURIComponent(sym)}`);
    if (prev?.length && splitSym === sym) {
      $("#split-prev").textContent = "Already recorded: " + prev.slice(0, 3)
        .map((s) => `${s.to}-for-${s.from} on ${new Date(s.appliedAt).toLocaleDateString()}`).join(", ") + ".";
    }
  } catch { /* the list is only a hint */ }
}

$("#split-to").addEventListener("input", paintSplitPreview);
$("#split-from").addEventListener("input", paintSplitPreview);

splitDlg.querySelector("form").addEventListener("submit", async (ev) => {
  if (ev.submitter?.value !== "ok") return;
  ev.preventDefault(); // stay open until the server accepts it
  const { to, from } = splitRatio();
  try {
    const r = await api("/api/splits", { method: "POST", body: JSON.stringify({ symbol: splitSym, from, to }) });
    splitDlg.close();
    const parts = [`${r.positions} holding${r.positions === 1 ? "" : "s"}`];
    if (r.orders) parts.push(`${r.orders} open order${r.orders === 1 ? "" : "s"}`);
    if (r.ordersCancelled) parts.push(`${r.ordersCancelled} order${r.ordersCancelled === 1 ? "" : "s"} cancelled`);
    toast(`Applied ${r.to}-for-${r.from} split to ${r.symbol}: ${parts.join(", ")}.`);
    await load();
  } catch (e) {
    $("#split-msg").textContent = e.message;
  }
});

// ---------- Trade history (paged on the server) ----------
const PAGE_SIZE_KEY = "folio.tradePageSize";
const PAGE_SIZES = [25, 50, 100];
try {
  const saved = Number(localStorage.getItem(PAGE_SIZE_KEY));
  if (PAGE_SIZES.includes(saved)) state.tradePageSize = saved;
} catch { /* storage unavailable */ }
$("#trades-size").value = String(state.tradePageSize);

const tradesUrl = () => `/api/trades?page=${state.tradePage}&pageSize=${state.tradePageSize}`;
let tradesSeq = 0;

/** Loads just the trade table, e.g. when changing pages. */
async function loadTrades() {
  const seq = ++tradesSeq;
  try {
    const page = await api(tradesUrl());
    if (seq === tradesSeq) renderTrades(page); // ignore responses for pages you've already left
  } catch (e) {
    toast(e.message);
  }
}

function renderTrades(page) {
  const { items: trades, total, page: current, pageSize, pages } = page;
  state.tradePage = current; // the server clamps past-the-end pages
  const pager = $("#trades-pager");
  pager.hidden = total <= PAGE_SIZES[0];
  if (!pager.hidden) {
    const from = (current - 1) * pageSize + 1;
    $("#trades-range").textContent = `${from.toLocaleString()}–${(from + trades.length - 1).toLocaleString()} of ${total.toLocaleString()} trades`;
    $("#trades-pageno").textContent = `Page ${current} of ${pages}`;
    pager.querySelector("[data-page=prev]").disabled = current <= 1;
    pager.querySelector("[data-page=next]").disabled = current >= pages;
  }

  const tbody = $("#trades");
  if (!trades.length) {
    tbody.innerHTML = `<tr class="empty"><td colspan="8">No trades yet.</td></tr>`;
    return;
  }
  tbody.innerHTML = trades.map((t) => `
    <tr>
      <td>${timeFmt.format(new Date(t.executedAt + (t.executedAt.endsWith("Z") ? "" : "Z")))}</td>
      <td><span class="side ${t.side === "Buy" ? "up" : "down"}">${t.side.toUpperCase()}${t.orderType && t.orderType !== "Market" ? ` · ${t.orderType.toUpperCase()}` : ""}</span></td>
      <td class="sym">${esc(t.symbol)}</td>
      <td class="num">${shares.format(t.quantity)}</td>
      <td class="num">${usd.format(t.price)}</td>
      <td class="num">${usd.format(t.quantity * t.price)}</td>
      <td class="num">${tradeCosts(t)}</td>
      <td class="num">${t.realizedPnl == null ? `<span class="muted">—</span>` : pnl(t.realizedPnl)}</td>
    </tr>`).join("");
}

$("#trades-pager").addEventListener("click", (ev) => {
  const btn = ev.target.closest("[data-page]");
  if (!btn || btn.disabled) return;
  state.tradePage += btn.dataset.page === "next" ? 1 : -1;
  loadTrades();
  // Keep the table's top in view when a long page shrinks.
  $("#trades-h").scrollIntoView({ block: "nearest", behavior: "smooth" });
});

$("#trades-size").addEventListener("change", (ev) => {
  // Stay near the same trades: keep the first row of the current page on the new page.
  const firstRow = (state.tradePage - 1) * state.tradePageSize;
  state.tradePageSize = Number(ev.target.value);
  state.tradePage = Math.floor(firstRow / state.tradePageSize) + 1;
  try { localStorage.setItem(PAGE_SIZE_KEY, String(state.tradePageSize)); } catch { /* ignore */ }
  loadTrades();
});

// ---------- Orders ----------
let ordersTab = "open";
let toastTimer;

function toast(text) {
  const el = $("#toast");
  el.textContent = text;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 8000);
}

function orderLabel(o) {
  return `${o.side.toLowerCase()} ${o.type.toLowerCase()}`;
}

/** Tells you when an order that was open on the last refresh has since filled or been rejected. */
function notifyClosedOrders(open, closed) {
  const prev = state.knownOpenOrders;
  state.knownOpenOrders = new Set(open.map((o) => o.id));
  if (!prev) return; // first load: nothing to compare against
  const msgs = closed
    .filter((o) => prev.has(o.id) && o.status !== "Cancelled")
    .map((o) => o.status === "Filled"
      ? `Your ${orderLabel(o)} for ${shares.format(o.quantity)} ${o.symbol} filled at ${usd.format(o.fillPrice)}.`
      : `Your ${orderLabel(o)} for ${o.symbol} was rejected: ${o.reason}`);
  if (msgs.length) toast(msgs.join(" "));
}

function renderOrders() {
  const { open = [], closed = [] } = state.orders ?? {};
  $("#open-count").textContent = open.length ? `(${open.length})` : "";
  const head = $("#orders-head");
  const body = $("#orders");

  if (ordersTab === "open") {
    head.innerHTML = `<tr><th>Placed</th><th>Order</th><th>Symbol</th><th class="num">Shares</th><th class="num">Trigger</th><th class="num">Current</th><th class="num">Distance</th><th></th></tr>`;
    if (!open.length) {
      body.innerHTML = `<tr class="empty"><td colspan="8">No open orders. Choose Limit or Stop on the order form to place one.</td></tr>`;
      return;
    }
    body.innerHTML = open.map((o) => {
      const c = orderCells(o);
      return `
      <tr data-sym="${esc(o.symbol)}" data-id="${o.id}">
        <td>${timeFmt.format(new Date(o.createdAt))}</td>
        <td><span class="side ${o.side === "Buy" ? "up" : "down"}">${o.side.toUpperCase()} · ${o.type.toUpperCase()}</span></td>
        <td class="sym">${esc(o.symbol)}</td>
        <td class="num">${shares.format(o.quantity)}</td>
        <td class="num">${usd.format(o.price)}</td>
        <td class="num" data-f="price">${c.price}</td>
        <td class="num" data-f="dist">${c.dist}</td>
        <td class="num"><button class="btn xs" data-cancel="${o.id}">Cancel</button></td>
      </tr>`;
    }).join("");
  } else {
    head.innerHTML = `<tr><th>Placed</th><th>Closed</th><th>Order</th><th>Symbol</th><th class="num">Shares</th><th class="num">Trigger</th><th>Result</th></tr>`;
    if (!closed.length) {
      body.innerHTML = `<tr class="empty"><td colspan="7">No filled, cancelled or rejected orders yet.</td></tr>`;
      return;
    }
    body.innerHTML = closed.map((o) => `
      <tr>
        <td>${timeFmt.format(new Date(o.createdAt))}</td>
        <td>${o.closedAt ? timeFmt.format(new Date(o.closedAt)) : ""}</td>
        <td><span class="side ${o.side === "Buy" ? "up" : "down"}">${o.side.toUpperCase()} · ${o.type.toUpperCase()}</span></td>
        <td class="sym">${esc(o.symbol)}</td>
        <td class="num">${shares.format(o.quantity)}</td>
        <td class="num">${usd.format(o.price)}</td>
        <td><span class="status ${o.status.toLowerCase()}">${o.status === "Filled" ? `Filled at ${usd.format(o.fillPrice)}` : o.status}</span>${o.reason ? `<span class="reason">${esc(o.reason)}</span>` : ""}</td>
      </tr>`).join("");
  }
}

document.querySelectorAll("[data-orders]").forEach((btn) =>
  btn.addEventListener("click", () => {
    ordersTab = btn.dataset.orders;
    document.querySelectorAll("[data-orders]").forEach((b) => b.setAttribute("aria-selected", String(b === btn)));
    renderOrders();
  })
);

$("#orders").addEventListener("click", async (ev) => {
  const btn = ev.target.closest("[data-cancel]");
  if (!btn) return;
  btn.disabled = true;
  try {
    const o = await api(`/api/orders/${btn.dataset.cancel}`, { method: "DELETE" });
    state.knownOpenOrders?.delete(o.id); // you cancelled it; no need to announce
    toast(`Cancelled ${orderLabel(o)} for ${o.symbol}.`);
    await load();
  } catch (e) {
    toast(e.message);
    btn.disabled = false;
  }
});

function orderCells(o) {
  const dist = o.currentPrice ? (o.price - o.currentPrice) / o.currentPrice : null;
  return {
    price: o.currentPrice ? usd.format(o.currentPrice) : `<span class="muted">—</span>`,
    dist: dist == null ? `<span class="muted">—</span>` : `${signed(dist, pct)} <span class="muted">away</span>`,
  };
}

// ---------- Price alerts ----------
const BELL = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0"/></svg>`;
const alertsFor = (sym) => state.alerts.active.filter((a) => a.symbol === sym);
const alertText = (a) => `Alert when ${a.symbol} ${a.direction === "Above" ? "rises" : "falls"} to ${usd.format(a.price)}`;
const announced = new Set(); // ids this tab has already shown, while the server catches up

async function loadAlerts() {
  try {
    state.alerts = await api("/api/alerts");
  } catch {
    return; // keep what we have; the next refresh retries
  }
  renderWatchlist();
  renderAlertHistory();
  if (alertDlg.open) renderAlertDialog();
  announceAlerts();
}

/** Shows alerts that fired since you last looked (including while the page was closed), once each. */
function announceAlerts() {
  const fresh = state.alerts.triggered.filter((a) => !a.seen && !announced.has(a.id));
  if (!fresh.length) return;
  fresh.forEach((a) => announced.add(a.id));
  const lines = fresh.map((a) =>
    `${a.symbol} ${a.direction === "Above" ? "rose" : "fell"} to ${usd.format(a.triggerPrice)} (your alert at ${usd.format(a.price)}).`);
  toast(lines.join(" "));
  // A system notification too when you're in another tab or app and have allowed them.
  if (document.hidden && typeof Notification !== "undefined" && Notification.permission === "granted") {
    for (const [i, a] of fresh.slice(0, 3).entries()) {
      try { new Notification(`${a.symbol} price alert`, { body: lines[i], tag: `folio-alert-${a.id}` }); } catch { /* not supported here */ }
    }
  }
  api("/api/alerts/seen", { method: "POST", body: JSON.stringify({ ids: fresh.map((a) => a.id) }) }).catch(() => { /* shown again next load at worst */ });
}

function renderAlertHistory() {
  const box = $("#alert-history");
  const items = state.alerts.triggered;
  box.hidden = !items.length;
  $("#alert-history-list").innerHTML = items.slice(0, 8).map((a) => `
    <li class="${a.seen ? "" : "new"}">
      <span><span class="${a.direction === "Above" ? "up" : "down"}">${a.direction === "Above" ? "▲" : "▼"}</span> ${esc(a.symbol)} ${a.direction === "Above" ? "rose" : "fell"} to ${usd.format(a.triggerPrice)} <span class="muted">· alert at ${usd.format(a.price)}</span></span>
      <span class="when">${timeFmt.format(new Date(a.triggeredAt))}</span>
    </li>`).join("");
}

$("#alert-clear").addEventListener("click", async () => {
  try {
    await api("/api/alerts/triggered", { method: "DELETE" });
    state.alerts.triggered = [];
    renderAlertHistory();
  } catch (e) {
    toast(e.message);
  }
});

const alertDlg = $("#alert-dlg");
let alertSym = null;

function alertCurrentPrice() {
  return state.watchlist.find((w) => w.symbol === alertSym)?.price ?? null;
}

function renderAlertDialog() {
  const now = alertCurrentPrice();
  $("#alert-now").textContent = now == null
    ? "No current price, so an alert can't be set right now."
    : `Now ${usd.format(now)}. Folio tells you once, the first time a live price reaches your target. Alerts are checked while Folio is running.`;
  paintAlertHint();
  const list = alertsFor(alertSym);
  $("#alert-list").innerHTML = list.map((a) => `
    <li><span>${a.direction === "Above" ? "▲ Rises" : "▼ Falls"} to <b>${usd.format(a.price)}</b>${now ? ` <span class="muted">(${signed((a.price - now) / now, pct)})</span>` : ""}</span>
      <button class="btn xs ghost" type="button" data-remove-alert="${a.id}">Remove</button></li>`).join("");
}

function paintAlertHint() {
  const now = alertCurrentPrice();
  const target = parseFloat($("#alert-price").value);
  const el = $("#alert-hint");
  if (!(target > 0) || now == null) { el.textContent = ""; return; }
  if (target === now) { el.textContent = `That's the current price. Pick a price above or below ${usd.format(now)}.`; return; }
  const up = target > now;
  el.innerHTML = `Alert when ${esc(alertSym)} <b>${up ? "rises" : "falls"}</b> to ${usd.format(target)} <span class="${up ? "up" : "down"}">(${signed((target - now) / now, pct)})</span>.`;
}

function openAlerts(sym) {
  alertSym = sym;
  $("#alert-sym").textContent = sym;
  $("#alert-price").value = "";
  $("#alert-msg").textContent = "";
  renderAlertDialog();
  alertDlg.showModal();
  $("#alert-price").focus();
}

$("#alert-price").addEventListener("input", paintAlertHint);

alertDlg.querySelector("form").addEventListener("submit", async (ev) => {
  if (ev.submitter && ev.submitter.value !== "ok") return; // Done
  ev.preventDefault(); // stay open so you can add several
  const price = parseFloat($("#alert-price").value);
  if (!(price > 0)) { $("#alert-msg").textContent = "Enter a price above zero."; return; }
  // Ask once, from this click, so alerts can reach you in other tabs too. Toasts work either way.
  if (typeof Notification !== "undefined" && Notification.permission === "default") Notification.requestPermission().catch(() => {});
  try {
    await api("/api/alerts", { method: "POST", body: JSON.stringify({ symbol: alertSym, price }) });
    $("#alert-price").value = "";
    $("#alert-msg").textContent = "";
    await loadAlerts();
    $("#alert-price").focus();
  } catch (e) {
    $("#alert-msg").textContent = e.message;
  }
});

$("#alert-list").addEventListener("click", async (ev) => {
  const btn = ev.target.closest("[data-remove-alert]");
  if (!btn) return;
  btn.disabled = true;
  try {
    await api(`/api/alerts/${btn.dataset.removeAlert}`, { method: "DELETE" }).catch((e) => {
      if (!/doesn't exist/.test(e.message)) throw e; // already gone (fired or removed elsewhere) is fine
    });
    await loadAlerts();
  } catch (e) {
    $("#alert-msg").textContent = e.message;
    btn.disabled = false;
  }
});

// ---------- Watchlist ----------
const isWatched = (sym) => state.watchlist.some((w) => w.symbol === sym);

function renderWatchlist() {
  const list = state.watchlist;
  $("#watch-count").textContent = list.length ? `(${list.length})` : "";
  const tbody = $("#watchlist");
  if (!list.length) {
    tbody.innerHTML = `<tr class="empty"><td colspan="5">Nothing on your watchlist. Add a ticker above, or tap ☆ next to a quote in the order form.</td></tr>`;
  } else {
    tbody.innerHTML = list.map((w) => {
      const alerts = alertsFor(w.symbol);
      const chips = alerts.length
        ? `<span class="alert-chips">${alerts.map((a) => `<span class="alert-chip" title="${esc(alertText(a))}">${a.direction === "Above" ? "▲" : "▼"} ${usd.format(a.price)}</span>`).join("")}</span>`
        : "";
      const bellLabel = alerts.length ? `Price alerts for ${w.symbol} (${alerts.length} set)` : `Set a price alert for ${w.symbol}`;
      return `
    <tr data-sym="${esc(w.symbol)}">
      <td><div class="sym-cell">${logo(w.symbol, w.logo)}<div class="sym-text"><button class="sym-link" type="button" data-trade="${esc(w.symbol)}" data-side="Buy" title="Open in order form">${esc(w.symbol)}</button>${w.stale ? `<span class="stale">${w.price == null ? "no quote" : "stale"}</span>` : ""}<span class="co">${esc(w.name || "")}</span>${chips}</div></div></td>
      <td class="num" data-f="price">${watchCells(w).price}</td>
      <td class="num" data-f="change">${watchCells(w).change}</td>
      <td class="num">${w.heldQuantity ? shares.format(w.heldQuantity) : `<span class="muted">—</span>`}</td>
      <td class="num"><button class="btn xs" type="button" data-trade="${esc(w.symbol)}" data-side="Buy">Buy</button> <button class="btn xs bell${alerts.length ? " on" : ""}" type="button" data-alert="${esc(w.symbol)}" aria-label="${esc(bellLabel)}" title="${esc(bellLabel)}">${BELL}</button> <button class="btn xs icon" type="button" data-unwatch="${esc(w.symbol)}" aria-label="Remove ${esc(w.symbol)} from watchlist" title="Remove">✕</button></td>
    </tr>`;
    }).join("");
  }
  paintStar();
}

function watchCells(w) {
  return {
    price: w.price == null ? `<span class="muted">—</span>` : usd.format(w.price),
    change: w.change == null ? `<span class="muted">—</span>` : pnl(w.change, w.changePercent),
  };
}

function paintStar() {
  const btn = $("#q-watch");
  const sym = state.quote?.symbol;
  const on = !!sym && isWatched(sym);
  btn.textContent = on ? "★" : "☆";
  btn.setAttribute("aria-pressed", String(on));
  btn.title = on ? `Remove ${sym} from watchlist` : sym ? `Add ${sym} to watchlist` : "Add to watchlist";
  btn.setAttribute("aria-label", btn.title);
}

async function addToWatchlist(sym) {
  const item = await api("/api/watchlist", { method: "POST", body: JSON.stringify({ symbol: sym }) });
  if (!isWatched(item.symbol)) state.watchlist = [...state.watchlist, item];
  renderWatchlist();
  return item;
}

async function removeFromWatchlist(sym) {
  await api(`/api/watchlist/${encodeURIComponent(sym)}`, { method: "DELETE" }).catch((e) => {
    if (!/isn't on your watchlist/.test(e.message)) throw e; // already gone is fine
  });
  state.watchlist = state.watchlist.filter((w) => w.symbol !== sym);
  state.alerts.active = state.alerts.active.filter((a) => a.symbol !== sym); // removed with it on the server
  renderWatchlist();
}

$("#watch-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const input = $("#watch-input");
  const msg = $("#watch-msg");
  const sym = input.value.trim().toUpperCase();
  if (!sym) return;
  msg.textContent = "";
  try {
    await addToWatchlist(sym);
    input.value = "";
  } catch (e) {
    msg.textContent = e.message;
  }
});

$("#watchlist").addEventListener("click", async (ev) => {
  const trade = ev.target.closest("[data-trade]");
  if (trade) return openTicket(trade.dataset.trade, trade.dataset.side);
  const bell = ev.target.closest("[data-alert]");
  if (bell) return openAlerts(bell.dataset.alert);
  const rm = ev.target.closest("[data-unwatch]");
  if (!rm) return;
  rm.disabled = true;
  try {
    await removeFromWatchlist(rm.dataset.unwatch);
  } catch (e) {
    $("#watch-msg").textContent = e.message;
    rm.disabled = false;
  }
});

$("#q-watch").addEventListener("click", async () => {
  const sym = state.quote?.symbol;
  if (!sym) return;
  try {
    if (isWatched(sym)) {
      await removeFromWatchlist(sym);
      toast(`Removed ${sym} from your watchlist.`);
    } else {
      await addToWatchlist(sym);
      toast(`Added ${sym} to your watchlist.`);
    }
  } catch (e) {
    toast(e.message);
  }
});

// ---------- Order ticket ----------
const form = $("#trade-form");
const symbolInput = $("#symbol");
const suggest = $("#suggest");
let searchTimer, searchSeq = 0, activeIdx = -1;

const side = () => form.querySelector("input[name=side]:checked").value;
function setSide(s) {
  form.querySelector(`input[name=side][value=${s}]`).checked = true;
  updateTicket();
}
form.querySelectorAll("input[name=side]").forEach((r) => r.addEventListener("change", updateTicket));

function heldQty(sym) {
  return state.portfolio?.holdings.find((h) => h.symbol === sym)?.quantity ?? 0;
}

const otype = () => form.querySelector("input[name=otype]:checked").value;
form.querySelectorAll("input[name=otype]").forEach((r) => r.addEventListener("change", () => {
  prefillPrice(true);
  updateTicket();
}));
$("#price").addEventListener("input", updateTicket);

/** Seeds the limit/stop price with the current quote so you only have to nudge it. */
function prefillPrice(force) {
  const q = state.quote;
  if (!q || otype() === "Market") return;
  if (force || !$("#price").value) $("#price").value = q.price.toFixed(2);
}

const HINTS = {
  "Buy|Limit": (p) => `Buys when the price is ${p} or lower.`,
  "Sell|Limit": (p) => `Sells when the price is ${p} or higher.`,
  "Buy|Stop": (p) => `Buys when the price rises to ${p} or higher.`,
  "Sell|Stop": (p) => `Sells when the price falls to ${p} or lower (a stop-loss).`,
};

function updateTicket() {
  const s = side();
  const type = otype();
  const submit = $("#submit");
  submit.textContent = type === "Market" ? s : `Place ${s.toLowerCase()} ${type.toLowerCase()}`;
  submit.classList.toggle("sell", s === "Sell");

  const resting = type !== "Market";
  const limitPrice = parseFloat($("#price").value);
  $("#price-field").hidden = !resting;
  $("#price-label").textContent = `${type} price`;
  const hint = $("#order-hint");
  const closed = marketClosed();
  const opens = closed ? etTime(state.market.nextOpen) : "";
  hint.hidden = !resting && !closed;
  hint.classList.toggle("warn", closed && !resting);
  hint.textContent = resting
    ? HINTS[`${s}|${type}`](limitPrice > 0 ? usd.format(limitPrice) : "your price") + " Fills at the market price when triggered." +
      (closed ? ` The market is closed, so it's checked from ${opens}.` : "")
    : closed ? `The market is closed until ${opens}. Market orders open then; you can place a limit or stop order now.` : "";

  const q = state.quote;
  const qty = parseFloat($("#qty").value);
  const estPrice = resting ? limitPrice : q?.price;
  const est = $("#est");
  if (estPrice > 0 && qty > 0) {
    // Same rules as the server: slippage against you, except a limit never fills past its price.
    const { fee, bps } = costs();
    const fill = type === "Limit" ? estPrice : estPrice * (s === "Buy" ? 1 + bps / 10000 : 1 - bps / 10000);
    const slip = Math.abs(fill - estPrice) * qty;
    const total = fill * qty + (s === "Buy" ? fee : -fee);
    const parts = [];
    if (fee) parts.push(`${usd.format(fee)} commission`);
    if (slip >= 0.005) parts.push(`~${usd.format(slip)} slippage`);
    est.innerHTML = usd.format(total) + (parts.length ? `<span class="est-costs">${s === "Buy" ? "incl." : "after"} ${parts.join(" + ")}</span>` : "");
  } else {
    est.textContent = "—";
  }

  const held = q ? heldQty(q.symbol) : 0;
  $("#held").textContent = q
    ? s === "Sell" ? `You hold ${shares.format(held)}` : `Cash ${usd.format(state.portfolio?.cash ?? 0)}`
    : "";
  submit.disabled = !state.configured || (closed && !resting);
}

$("#qty").addEventListener("input", updateTicket);

form.querySelector("[data-max]").addEventListener("click", () => {
  const q = state.quote;
  if (!q) return;
  const limitPrice = parseFloat($("#price").value);
  const price = otype() !== "Market" && limitPrice > 0 ? limitPrice : q.price;
  const { fee, bps } = costs();
  const fill = otype() === "Limit" ? price : price * (1 + bps / 10000);
  $("#qty").value = side() === "Sell"
    ? heldQty(q.symbol)
    : Math.max(0, Math.floor(((state.portfolio?.cash ?? 0) - fee) / fill));
  updateTicket();
});

async function selectSymbol(sym) {
  closeSuggest();
  sym = sym.trim().toUpperCase();
  if (!sym) return;
  symbolInput.value = sym;
  const msg = $("#trade-msg");
  try {
    const q = await api(`/api/quote/${encodeURIComponent(sym)}`);
    const changed = state.quote?.symbol !== q.symbol;
    state.quote = q;
    prefillPrice(changed);
    $("#quote").hidden = false;
    $("#q-sym").textContent = q.symbol;
    paintQuote();
    if (changed) showCompany(q.symbol);
    paintStar();
    msg.textContent = "";
  } catch (e) {
    state.quote = null;
    $("#quote").hidden = true;
    msg.className = "small msg down";
    msg.textContent = e.message;
  }
  updateTicket();
}

function paintQuote() {
  const q = state.quote;
  if (!q) return;
  $("#q-price").textContent = usd.format(q.price) + (q.stale ? " (stale)" : "");
  $("#q-change").innerHTML = pnl(q.change, q.changePercent) + ` <span class="muted">today</span>`;
}

/** Name and logo for the quote box. Cached server-side after the first lookup of each symbol. */
async function showCompany(sym) {
  $("#q-logo").innerHTML = logo(sym, null);
  $("#q-name").textContent = "";
  try {
    const c = await api(`/api/company/${encodeURIComponent(sym)}`);
    if (state.quote?.symbol !== sym) return; // you've moved on to another symbol
    $("#q-logo").innerHTML = logo(sym, c.logo);
    $("#q-name").textContent = c.name || "";
  } catch { /* letter tile stays */ }
}

function closeSuggest() {
  suggest.hidden = true;
  symbolInput.setAttribute("aria-expanded", "false");
  activeIdx = -1;
}

function paintActive() {
  [...suggest.children].forEach((li, i) => li.setAttribute("aria-selected", String(i === activeIdx)));
}

symbolInput.addEventListener("input", () => {
  clearTimeout(searchTimer);
  state.quote = null;
  $("#quote").hidden = true;
  updateTicket();
  const q = symbolInput.value.trim();
  if (q.length < 1 || !state.configured) return closeSuggest();
  searchTimer = setTimeout(async () => {
    const seq = ++searchSeq;
    try {
      const results = await api(`/api/search?q=${encodeURIComponent(q)}`);
      if (seq !== searchSeq) return; // a newer keystroke won
      if (!results.length) return closeSuggest();
      suggest.innerHTML = results.map((r, i) =>
        `<li role="option" id="opt-${i}" data-sym="${esc(r.symbol)}"><span class="s-sym">${esc(r.symbol)}</span><span class="s-desc">${esc(r.description)}</span></li>`
      ).join("");
      suggest.hidden = false;
      symbolInput.setAttribute("aria-expanded", "true");
      activeIdx = -1;
    } catch {
      closeSuggest();
    }
  }, 300);
});

symbolInput.addEventListener("keydown", (ev) => {
  const items = suggest.hidden ? [] : [...suggest.children];
  if (ev.key === "ArrowDown" && items.length) {
    ev.preventDefault(); activeIdx = (activeIdx + 1) % items.length; paintActive();
  } else if (ev.key === "ArrowUp" && items.length) {
    ev.preventDefault(); activeIdx = (activeIdx - 1 + items.length) % items.length; paintActive();
  } else if (ev.key === "Enter") {
    ev.preventDefault();
    selectSymbol(activeIdx >= 0 ? items[activeIdx].dataset.sym : symbolInput.value);
    $("#qty").focus();
  } else if (ev.key === "Escape") {
    closeSuggest();
  }
});

suggest.addEventListener("mousedown", (ev) => {
  const li = ev.target.closest("li");
  if (!li) return;
  ev.preventDefault();
  selectSymbol(li.dataset.sym);
  $("#qty").focus();
});

symbolInput.addEventListener("blur", () => {
  setTimeout(closeSuggest, 100);
  const v = symbolInput.value.trim().toUpperCase();
  if (v && v !== state.quote?.symbol) selectSymbol(v);
});

form.addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const msg = $("#trade-msg");
  const symbol = symbolInput.value.trim().toUpperCase();
  const quantity = parseFloat($("#qty").value);
  if (!symbol) { msg.className = "small msg down"; msg.textContent = "Choose a symbol."; return; }
  if (!(quantity > 0)) { msg.className = "small msg down"; msg.textContent = "Enter a share quantity."; return; }

  const type = otype();
  const price = parseFloat($("#price").value);
  if (type !== "Market" && !(price > 0)) { msg.className = "small msg down"; msg.textContent = `Enter a ${type.toLowerCase()} price.`; return; }

  const submit = $("#submit");
  submit.disabled = true;
  try {
    if (type === "Market") {
      const t = await api("/api/trades", { method: "POST", body: JSON.stringify({ symbol, side: side(), quantity }) });
      msg.className = "small msg up";
      msg.textContent = `${t.side === "Buy" ? "Bought" : "Sold"} ${shares.format(t.quantity)} ${t.symbol} at ${usd.format(t.price)}` +
        (t.realizedPnl != null ? ` · realized ${signed(t.realizedPnl)}` : "");
    } else {
      const o = await api("/api/orders", { method: "POST", body: JSON.stringify({ symbol, side: side(), type, quantity, price }) });
      msg.className = o.status === "Rejected" ? "small msg down" : "small msg up";
      msg.textContent = o.status === "Filled"
        ? `Price was already there, so your ${orderLabel(o)} filled immediately: ${shares.format(o.quantity)} ${o.symbol} at ${usd.format(o.fillPrice)}.`
        : o.status === "Rejected"
          ? `Order rejected: ${o.reason}`
          : `Placed ${orderLabel(o)}: ${shares.format(o.quantity)} ${o.symbol} at ${usd.format(o.price)}. It's in Open orders below.`;
      if (o.status === "Open") state.knownOpenOrders?.add(o.id);
    }
    $("#qty").value = "";
    state.tradePage = 1; // show the new trade
    await load();
  } catch (e) {
    msg.className = "small msg down";
    msg.textContent = e.message;
  } finally {
    updateTicket();
  }
});

// ---------- Reset ----------
const dlg = $("#reset-dlg");
$("#reset").addEventListener("click", () => {
  $("#reset-cash").value = state.portfolio?.startingCash ?? 100000;
  dlg.showModal();
});
dlg.addEventListener("close", async () => {
  if (dlg.returnValue !== "ok") return;
  try {
    await api("/api/portfolio/reset", { method: "POST", body: JSON.stringify({ startingCash: parseFloat($("#reset-cash").value) }) });
    state.quote = null;
    $("#quote").hidden = true;
    $("#trade-msg").textContent = "";
    state.tradePage = 1;
    await load();
  } catch (e) {
    banner(esc(e.message));
  }
});

// ---------- Backups & data ----------
const dataDlg = $("#data-dlg");
const MAX_IMPORT_BYTES = 25 * 1024 * 1024; // same cap as the server
const bytesFmt = (n) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`;

async function renderBackups() {
  const list = $("#backup-list");
  try {
    const view = await api("/api/backups");
    $("#backup-dir").textContent = view.directory;
    $("#backup-keep").textContent = view.keep;
    list.innerHTML = view.backups.length
      ? view.backups.slice(0, 6).map((b) => `
        <li><span>${timeFmt.format(new Date(b.createdAt))}${/-before-import\.db$/.test(b.name) ? ` <span class="muted">· before import</span>` : /-manual\.db$/.test(b.name) ? ` <span class="muted">· manual</span>` : ""}</span>
        <span class="muted">${bytesFmt(b.bytes)}</span></li>`).join("")
      : `<li class="muted">No backups yet.</li>`;
  } catch (e) {
    list.innerHTML = `<li class="down">${esc(e.message)}</li>`;
  }
}

$("#data").addEventListener("click", () => {
  $("#data-msg").textContent = "";
  $("#import-file").value = "";
  dataDlg.showModal();
  renderBackups();
});

$("#backup-now").addEventListener("click", async (ev) => {
  const btn = ev.currentTarget;
  btn.disabled = true;
  try {
    const b = await api("/api/backups", { method: "POST" });
    $("#data-msg").className = "small msg";
    $("#data-msg").textContent = `Backed up (${bytesFmt(b.bytes)}).`;
    await renderBackups();
  } catch (e) {
    $("#data-msg").className = "small msg down";
    $("#data-msg").textContent = e.message;
  } finally {
    btn.disabled = false;
  }
});

$("#import-file").addEventListener("change", async (ev) => {
  const file = ev.target.files?.[0];
  const msg = $("#data-msg");
  msg.className = "small msg down";
  if (!file) return;
  if (file.size > MAX_IMPORT_BYTES) { msg.textContent = "That file is larger than 25 MB, so it isn't a Folio export."; return; }

  let archive;
  try { archive = JSON.parse(await file.text()); } catch { msg.textContent = "That file isn't valid JSON."; return; }
  const n = archive?.portfolios?.length ?? 0;
  if (!confirm(`Replace ALL portfolios, trades, orders, history, watchlist and alerts with the ${n} portfolio${n === 1 ? "" : "s"} in “${file.name}”?\n\nFolio backs up your current data first, so this can be undone.`)) {
    ev.target.value = "";
    return;
  }
  try {
    const r = await api("/api/import", { method: "POST", body: JSON.stringify(archive) });
    dataDlg.close();
    state.portfolioId = null;
    state.tradePage = 1;
    state.knownOpenOrders = null;
    await loadPortfolios();
    await load();
    toast(`Imported ${r.portfolios} portfolio${r.portfolios === 1 ? "" : "s"} and ${r.trades} trades. Your previous data is in backup ${r.backupName}.`);
  } catch (e) {
    msg.textContent = e.message;
  } finally {
    ev.target.value = "";
  }
});

// ---------- Boot ----------
$("#refresh").addEventListener("click", load);

(async () => {
  try {
    const status = await api("/api/status");
    state.configured = status.finnhubConfigured;
    state.streaming = status.streaming;
  } catch { /* load() will surface errors */ }
  updateTicket();
  try { await loadPortfolios(); } catch (e) { banner(esc(e.message)); }
  await load();
  connectStream();
  setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) return disconnectStream(); // no point streaming to a hidden tab
    load();
    connectStream();
  });
})();
