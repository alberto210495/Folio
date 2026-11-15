// Folio front end: vanilla JS, no build step.

const REFRESH_MS = 60_000; // Quotes are cached 15s server-side; 60s keeps us far from Finnhub's 60/min.
const MAX_SLOTS = 7;       // Named allocation segments; the rest fold into "Other".

const $ = (sel) => document.querySelector(sel);
const usd = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const pct = new Intl.NumberFormat("en-US", { style: "percent", minimumFractionDigits: 1, maximumFractionDigits: 1 });
const shares = new Intl.NumberFormat("en-US", { maximumFractionDigits: 4 });
const timeFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

const state = { portfolio: null, quote: null, allocMode: "holding", configured: true, history: null, range: "1M" };

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
    const [portfolio, trades, history] = await Promise.all([
      api("/api/portfolio"),
      api("/api/trades?limit=200"),
      api(`/api/history?range=${state.range}`).catch(() => null),
    ]);
    state.portfolio = portfolio;
    state.history = history;
    renderHistory();
    renderKpis(portfolio);
    renderAllocation();
    renderHoldings(portfolio);
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

async function loadHistory() {
  try {
    state.history = await api(`/api/history?range=${state.range}`);
  } catch {
    state.history = null;
  }
  renderHistory();
}

function historyPoints() {
  const pts = (state.history?.points ?? []).map((p) => ({ t: new Date(p.t), v: p.v }));
  // Extend the line to the live value so the chart always ends at "now".
  const p = state.portfolio;
  if (p && !p.anyStale) pts.push({ t: new Date(), v: p.totalValue, live: true });
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

function renderHistory() {
  const box = $("#hist-chart");
  const pts = historyPoints();
  const change = $("#hist-change");

  if (pts.length < 2 || pts[pts.length - 1].t - pts[0].t < 60_000) {
    change.innerHTML = "";
    box.innerHTML = `<div class="hist-empty">Not enough history yet.<br>Folio saves your account value every 15 minutes while it's running, and after each trade.</div>`;
    return;
  }

  const first = pts[0].v, last = pts[pts.length - 1].v;
  change.innerHTML = `${pnl(last - first, first ? ((last - first) / first) * 100 : 0)} <span class="muted">${RANGE_TEXT[state.range]}</span>`;

  const W = box.clientWidth, H = box.clientHeight;
  const m = { top: 10, right: 12, bottom: 24, left: 64 };
  const iw = W - m.left - m.right, ih = H - m.top - m.bottom;

  // Y domain: data, padded; starting cash joins only if it's already near the data.
  let lo = Math.min(...pts.map((p) => p.v)), hi = Math.max(...pts.map((p) => p.v));
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
    "aria-label": `Portfolio value ${RANGE_TEXT[state.range]}: from ${usd.format(first)} to ${usd.format(last)}` });

  // Grid + y labels
  for (let v = y0; v <= y1 + step / 2; v += step) {
    svg.append(svgEl("line", { class: v === y0 ? "baseline" : "grid-line", x1: m.left, x2: W - m.right, y1: y(v), y2: y(v) }));
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
    svg.append(svgEl("line", { class: "ref-line", x1: m.left, x2: W - m.right, y1: y(ref), y2: y(ref) }));
    svg.append(svgEl("text", { class: "ref-label", x: m.left + 6, y: y(ref) - 6 }, "Starting cash"));
  }

  // Area + line
  const d = pts.map((p, i) => `${i ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join("");
  svg.append(svgEl("path", { class: "area", d: `${d}L${x(t1)},${y(y0)}L${x(t0)},${y(y0)}Z` }));
  svg.append(svgEl("path", { class: "line", d }));

  // Hover: crosshair + dot + tooltip on the nearest point
  const cross = svgEl("line", { class: "crosshair", y1: m.top, y2: m.top + ih, visibility: "hidden" });
  const dot = svgEl("circle", { class: "dot", r: 5, visibility: "hidden" });
  const hit = svgEl("rect", { x: m.left, y: 0, width: iw, height: H, fill: "transparent" });
  svg.append(cross, dot, hit);

  const tip = $("#hist-tip");
  const tipFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
  hit.addEventListener("mousemove", (ev) => {
    const rect = svg.getBoundingClientRect();
    const tx = t0 + ((ev.clientX - rect.left - m.left) / iw) * (t1 - t0);
    let best = pts[0];
    for (const p of pts) if (Math.abs(p.t - tx) < Math.abs(best.t - tx)) best = p;
    const px = x(best.t), py = y(best.v);
    cross.setAttribute("x1", px); cross.setAttribute("x2", px); cross.setAttribute("visibility", "visible");
    dot.setAttribute("cx", px); dot.setAttribute("cy", py); dot.setAttribute("visibility", "visible");

    const vsStart = ref ? `<br>${pnl(best.v - ref, ((best.v - ref) / ref) * 100)} <span class="muted">vs start</span>` : "";
    tip.innerHTML = `<b>${usd.format(best.v)}</b><br><span class="muted">${best.live ? "Now" : tipFmt.format(best.t)}</span>${vsStart}`;
    tip.hidden = false;
    const sx = rect.left + px, sy = rect.top + py;
    tip.style.left = `${Math.min(sx + 12, window.innerWidth - tip.offsetWidth - 8)}px`;
    tip.style.top = `${Math.max(8, sy - tip.offsetHeight - 12)}px`;
  });
  hit.addEventListener("mouseleave", () => {
    tip.hidden = true;
    cross.setAttribute("visibility", "hidden");
    dot.setAttribute("visibility", "hidden");
  });

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
    if (it && ev?.type === "mousemove") {
      tip.hidden = false;
      tip.innerHTML = `<b>${esc(it.label)}</b>${it.sub ? ` · ${esc(it.sub)}` : ""}<br>${usd.format(it.value)} · ${pct.format(it.weight)}`;
      const x = Math.min(ev.clientX + 12, window.innerWidth - tip.offsetWidth - 8);
      tip.style.left = `${x}px`;
      tip.style.top = `${ev.clientY + 14}px`;
    } else {
      tip.hidden = true;
    }
  };

  bar.onmousemove = (ev) => highlight(ev.target.closest(".seg-fill")?.dataset.key, ev);
  bar.onmouseleave = () => highlight(null);
  rows.onmouseover = (ev) => highlight(ev.target.closest("tr")?.dataset.key, ev);
  rows.onmouseleave = () => highlight(null);
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
      <td><span class="sym">${esc(h.symbol)}</span>${h.priceStale ? `<span class="stale">stale</span>` : ""}<span class="co">${esc(h.name || h.sector)}</span></td>
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

$("#holdings").addEventListener("click", (ev) => {
  const btn = ev.target.closest("[data-trade]");
  if (!btn) return;
  setSide(btn.dataset.side);
  $("#symbol").value = btn.dataset.trade;
  $("#qty").value = btn.dataset.qty || "";
  selectSymbol(btn.dataset.trade);
  $("#qty").focus();
  $("#trade-form").scrollIntoView({ behavior: "smooth", block: "nearest" });
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
      <td><span class="side ${t.side === "Buy" ? "up" : "down"}">${t.side.toUpperCase()}</span></td>
      <td class="sym">${esc(t.symbol)}</td>
      <td class="num">${shares.format(t.quantity)}</td>
      <td class="num">${usd.format(t.price)}</td>
      <td class="num">${usd.format(t.quantity * t.price)}</td>
      <td class="num">${t.realizedPnl == null ? `<span class="muted">—</span>` : pnl(t.realizedPnl)}</td>
    </tr>`).join("");
}

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

function updateTicket() {
  const s = side();
  const submit = $("#submit");
  submit.textContent = s;
  submit.classList.toggle("sell", s === "Sell");

  const q = state.quote;
  const qty = parseFloat($("#qty").value);
  $("#est").textContent = q && qty > 0 ? usd.format(q.price * qty) : "—";

  const held = q ? heldQty(q.symbol) : 0;
  $("#held").textContent = q
    ? s === "Sell" ? `You hold ${shares.format(held)}` : `Cash ${usd.format(state.portfolio?.cash ?? 0)}`
    : "";
  submit.disabled = !state.configured;
}

$("#qty").addEventListener("input", updateTicket);

form.querySelector("[data-max]").addEventListener("click", () => {
  const q = state.quote;
  if (!q) return;
  $("#qty").value = side() === "Sell"
    ? heldQty(q.symbol)
    : Math.floor((state.portfolio?.cash ?? 0) / q.price);
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
    state.quote = q;
    $("#quote").hidden = false;
    $("#q-sym").textContent = q.symbol;
    $("#q-price").textContent = usd.format(q.price) + (q.stale ? " (stale)" : "");
    $("#q-change").innerHTML = pnl(q.change, q.changePercent) + ` <span class="muted">today</span>`;
    msg.textContent = "";
  } catch (e) {
    state.quote = null;
    $("#quote").hidden = true;
    msg.className = "small msg down";
    msg.textContent = e.message;
  }
  updateTicket();
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

  const submit = $("#submit");
  submit.disabled = true;
  try {
    const t = await api("/api/trades", { method: "POST", body: JSON.stringify({ symbol, side: side(), quantity }) });
    msg.className = "small msg up";
    msg.textContent = `${t.side === "Buy" ? "Bought" : "Sold"} ${shares.format(t.quantity)} ${t.symbol} at ${usd.format(t.price)}` +
      (t.realizedPnl != null ? ` · realized ${signed(t.realizedPnl)}` : "");
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
