// Folio front end: vanilla JS, no build step.

const REFRESH_MS = 60_000; // Quotes are cached 15s server-side; 60s keeps us far from Finnhub's 60/min.
const MAX_SLOTS = 7;       // Named allocation segments; the rest fold into "Other".

const $ = (sel) => document.querySelector(sel);
const usd = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const pct = new Intl.NumberFormat("en-US", { style: "percent", minimumFractionDigits: 1, maximumFractionDigits: 1 });
const shares = new Intl.NumberFormat("en-US", { maximumFractionDigits: 4 });
const timeFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

const state = { portfolio: null, quote: null, allocMode: "holding", configured: true, history: null, range: "1M", histClear: null, allocClear: null, orders: null, knownOpenOrders: null, watchlist: [], market: null };

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
  const res = await fetch(path, {
    ...opts,
    headers: { "Content-Type": "application/json", ...(opts.headers || {}) },
  });
  if (res.status === 204) return null;
  const body = await res.json().catch(() => null);
  if (!res.ok) throw new Error(body?.error || `Request failed (${res.status})`);
  return body;
}

function banner(html) {
  const el = $("#banner");
  el.hidden = !html;
  el.innerHTML = html || "";
}

// ---------- loading ----------
async function load() {
  try {
    const [portfolio, trades, history, openOrders, closedOrders, watchlist, market] = await Promise.all([
      api("/api/portfolio"),
      api("/api/trades?limit=200"),
      api(`/api/history?range=${state.range}`).catch(() => null),
      api("/api/orders?status=open"),
      api("/api/orders?status=closed&limit=100"),
      api("/api/watchlist").catch(() => state.watchlist),
      api("/api/market").catch(() => state.market),
    ]);
    notifyClosedOrders(openOrders, closedOrders);
    state.portfolio = portfolio;
    state.history = history;
    state.orders = { open: openOrders, closed: closedOrders };
    state.watchlist = watchlist;
    state.market = market;
    renderMarket();
    updateTicket();
    renderHistory();
    renderKpis(portfolio);
    renderAllocation();
    renderHoldings(portfolio);
    renderOrders();
    renderWatchlist();
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

// ---------- KPIs ----------
function renderKpis(p) {
  $("#k-total").textContent = usd.format(p.totalValue);
  $("#k-return").innerHTML = `${pnl(p.totalReturn, p.totalReturnPercent)} <span class="muted">all time on ${usd.format(p.startingCash)}</span>`;
  $("#k-day").innerHTML = pnl(p.dayChange);
  $("#k-cash").textContent = usd.format(p.cash);
  $("#k-unreal").innerHTML = pnl(p.unrealizedPnl);
  $("#k-real").innerHTML = pnl(p.realizedPnl);
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
      rows += `<div class="row"><span><span class="sw-line" style="background:var(--s2)"></span>${esc(benchSym)}</span><b>${usd.format(best.b)}</b></div>`;
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
  tbody.innerHTML = p.holdings.map((h) => `
    <tr>
      <td><div class="sym-cell">${logo(h.symbol, h.logo)}<div class="sym-text"><span class="sym">${esc(h.symbol)}</span>${h.priceStale ? `<span class="stale">stale</span>` : ""}<span class="co">${esc(h.name || h.sector)}</span></div></div></td>
      <td class="num">${shares.format(h.quantity)}</td>
      <td class="num">${usd.format(h.avgCost)}</td>
      <td class="num">${usd.format(h.price)}</td>
      <td class="num">${usd.format(h.marketValue)}</td>
      <td class="num">${pnl(h.dayChange, h.dayChangePercent)}</td>
      <td class="num">${pnl(h.unrealizedPnl, h.unrealizedPnlPercent)}</td>
      <td class="num">${pct.format(p.totalValue ? h.marketValue / p.totalValue : 0)}</td>
      <td class="num"><button class="btn xs" data-trade="${esc(h.symbol)}" data-side="Buy">Buy</button> <button class="btn xs" data-trade="${esc(h.symbol)}" data-side="Sell" data-qty="${h.quantity}">Sell</button></td>
    </tr>`).join("");
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
  const btn = ev.target.closest("[data-trade]");
  if (btn) openTicket(btn.dataset.trade, btn.dataset.side, btn.dataset.qty || "");
});

function renderTrades(trades) {
  const tbody = $("#trades");
  if (!trades.length) {
    tbody.innerHTML = `<tr class="empty"><td colspan="7">No trades yet.</td></tr>`;
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
      <td class="num">${t.realizedPnl == null ? `<span class="muted">—</span>` : pnl(t.realizedPnl)}</td>
    </tr>`).join("");
}

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
      const dist = o.currentPrice ? (o.price - o.currentPrice) / o.currentPrice : null;
      return `
      <tr>
        <td>${timeFmt.format(new Date(o.createdAt))}</td>
        <td><span class="side ${o.side === "Buy" ? "up" : "down"}">${o.side.toUpperCase()} · ${o.type.toUpperCase()}</span></td>
        <td class="sym">${esc(o.symbol)}</td>
        <td class="num">${shares.format(o.quantity)}</td>
        <td class="num">${usd.format(o.price)}</td>
        <td class="num">${o.currentPrice ? usd.format(o.currentPrice) : `<span class="muted">—</span>`}</td>
        <td class="num">${dist == null ? `<span class="muted">—</span>` : `${signed(dist, pct)} <span class="muted">away</span>`}</td>
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

// ---------- Watchlist ----------
const isWatched = (sym) => state.watchlist.some((w) => w.symbol === sym);

function renderWatchlist() {
  const list = state.watchlist;
  $("#watch-count").textContent = list.length ? `(${list.length})` : "";
  const tbody = $("#watchlist");
  if (!list.length) {
    tbody.innerHTML = `<tr class="empty"><td colspan="5">Nothing on your watchlist. Add a ticker above, or tap ☆ next to a quote in the order form.</td></tr>`;
  } else {
    tbody.innerHTML = list.map((w) => `
    <tr>
      <td><div class="sym-cell">${logo(w.symbol, w.logo)}<div class="sym-text"><button class="sym-link" type="button" data-trade="${esc(w.symbol)}" data-side="Buy" title="Open in order form">${esc(w.symbol)}</button>${w.stale ? `<span class="stale">${w.price == null ? "no quote" : "stale"}</span>` : ""}<span class="co">${esc(w.name || "")}</span></div></div></td>
      <td class="num">${w.price == null ? `<span class="muted">—</span>` : usd.format(w.price)}</td>
      <td class="num">${w.change == null ? `<span class="muted">—</span>` : pnl(w.change, w.changePercent)}</td>
      <td class="num">${w.heldQuantity ? shares.format(w.heldQuantity) : `<span class="muted">—</span>`}</td>
      <td class="num"><button class="btn xs" type="button" data-trade="${esc(w.symbol)}" data-side="Buy">Buy</button> <button class="btn xs icon" type="button" data-unwatch="${esc(w.symbol)}" aria-label="Remove ${esc(w.symbol)} from watchlist" title="Remove">✕</button></td>
    </tr>`).join("");
  }
  paintStar();
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
  $("#est").textContent = estPrice > 0 && qty > 0 ? usd.format(estPrice * qty) : "—";

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
  $("#qty").value = side() === "Sell"
    ? heldQty(q.symbol)
    : Math.floor((state.portfolio?.cash ?? 0) / price);
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
    $("#q-price").textContent = usd.format(q.price) + (q.stale ? " (stale)" : "");
    $("#q-change").innerHTML = pnl(q.change, q.changePercent) + ` <span class="muted">today</span>`;
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
    await load();
  } catch (e) {
    banner(esc(e.message));
  }
});

// ---------- Boot ----------
$("#refresh").addEventListener("click", load);

(async () => {
  try {
    state.configured = (await api("/api/status")).finnhubConfigured;
  } catch { /* load() will surface errors */ }
  updateTicket();
  await load();
  setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) load(); });
})();
