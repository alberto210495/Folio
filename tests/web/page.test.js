// The page as a whole: it boots cleanly, shows what the API returns, treats data from the server as text, and
// guides you when the market is closed.

import assert from "node:assert/strict";
import { afterEach, describe, it } from "node:test";
import { FakeApi } from "./fake-api.js";
import { openPage } from "./harness.js";

let page;
afterEach(() => page?.close());

describe("page", () => {
  it("boots without errors and shows the account", async () => {
    page = await openPage();

    assert.deepEqual(page.errors, []);
    assert.deepEqual(page.api.unknown, [], "every request goes to a real route");
    assert.equal(page.$("#banner").hidden, true);
    assert.match(page.text(".kpis"), /Total value \$100,000\.00/);
    assert.match(page.text("#holdings"), /No positions yet/);
  });

  it("sends the open portfolio with every request", async () => {
    page = await openPage();
    const scoped = page.api.requests.filter((r) => r.path.startsWith("/api/") && !r.path.startsWith("/api/portfolios") && r.path !== "/api/status");
    assert.ok(scoped.length > 0);
    for (const r of scoped) assert.equal(r.query.portfolio, "1", `${r.path} names the portfolio`);
  });

  it("shows server text as text, never as HTML", async () => {
    const api = new FakeApi();
    const evil = `<img src="x" onerror="window.pwned=1">`;
    api.names.AAPL = evil;
    api.positions.set("AAPL", { quantity: 1, avgCost: 150 });
    api.watchlist = [{
      symbol: "AAPL", name: evil, logo: null, price: 200, change: 1, changePercent: 0.5, previousClose: 199,
      stale: false, heldQuantity: 1, addedAt: "2026-11-18T15:00:00Z",
    }];
    page = await openPage({ api });
    await page.pickSymbol("AAPL"); // the quote box shows the company name too

    assert.equal(page.document.querySelector('img[src="x"]'), null);
    assert.equal(page.window.pwned, undefined);
    assert.ok(page.text("#holdings").includes(evil));
    assert.ok(page.text("#watchlist").includes(evil));
    assert.equal(page.text("#q-name"), evil);
  });

  it("shows an unknown symbol's error in the order form", async () => {
    page = await openPage();
    await page.pickSymbol("NOPE");
    assert.equal(page.message, "No quote for NOPE.");
    assert.equal(page.$("#quote").hidden, true);
  });

  it("says when Folio is locked", async () => {
    const api = new FakeApi();
    api.locked = true;
    page = await openPage({ api });
    assert.equal(page.$("#banner").hidden, false);
    assert.match(page.text("#banner"), /Folio is locked\. Open it from the Folio shortcut/);
  });
});

describe("market closed", () => {
  const closed = () => {
    const api = new FakeApi();
    api.market = { isOpen: false, closedReason: "Weekend", closesAt: null, nextOpen: "2026-11-23T14:30:00Z", earlyClose: false, enforced: true };
    return api;
  };

  it("turns off market orders and says when the market opens", async () => {
    page = await openPage({ api: closed() });
    await page.choose("otype", "Market");

    assert.equal(page.$("#submit").disabled, true);
    assert.match(page.text("#order-hint"), /The market is closed until .+\. Market orders open then; you can place a limit or stop order now\./);
  });

  it("still takes limit orders, and says when they'll be checked", async () => {
    page = await openPage({ api: closed() });
    await page.choose("otype", "Limit");
    await page.pickSymbol("AAPL");

    assert.equal(page.$("#submit").disabled, false);
    assert.equal(page.$("#price").value, "200.00", "the price starts at the current quote");
    assert.match(page.text("#order-hint"), /Buys when the price is \$200\.00 or lower\..*checked from/);
  });
});

describe("order form estimate", () => {
  it("shows the cost of a buy as you type", async () => {
    page = await openPage();
    await page.choose("otype", "Market");
    await page.pickSymbol("AAPL");
    await page.type("#qty", 3);
    assert.equal(page.text("#est"), "$600.00");
    assert.equal(page.text("#held"), "Cash $100,000.00");
  });

  it("fills in the most you can buy with Max", async () => {
    page = await openPage();
    await page.pickSymbol("MSFT");
    page.$("[data-max]").click();
    assert.equal(page.$("#qty").value, "250"); // $100,000 / $400
  });
});
