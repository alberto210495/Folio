// A page that guides you: trading comes first, refusals come with a one-click fix, a split doesn't pass for a crash,
// and the questions people ask (locked out? forgot the passphrase?) are answered on screen.

import assert from "node:assert/strict";
import { afterEach, describe, it } from "node:test";
import { FakeApi } from "./fake-api.js";
import { openPage } from "./harness.js";

let page;
afterEach(() => page?.close());

const fixButton = () => page.document.querySelector("#trade-msg .fix");
const before = (a, b) => Boolean(page.$(a).compareDocumentPosition(page.$(b)) & page.window.Node.DOCUMENT_POSITION_FOLLOWING);

describe("layout", () => {
  it("puts the order form and holdings first, then orders and the watchlist, then the charts", async () => {
    page = await openPage();
    // In the page's own order, so the keyboard (Tab) follows what you see too.
    const order = ["#trade-h", "#hold-h", "#orders-h", "#watch-h", "#hist-h", "#perf-h", "#alloc-h", "#trades-h"];
    for (let i = 1; i < order.length; i++) assert.ok(before(order[i - 1], order[i]), `${order[i - 1]} comes before ${order[i]}`);
    assert.ok(page.$("#trade-h").closest(".grid").contains(page.$("#hold-h")), "the order form and holdings sit side by side");
  });
});

describe("refusals with a fix", () => {
  it("offers a limit order while the market is closed", async () => {
    const api = new FakeApi();
    api.market = { isOpen: false, closedReason: "Weekend", closesAt: null, nextOpen: "2026-11-23T14:30:00Z", earlyClose: false, enforced: true };
    page = await openPage({ api });
    await page.choose("otype", "Market");
    await page.pickSymbol("AAPL");

    const offer = page.$("#order-hint .fix");
    assert.equal(offer.textContent, "Use a limit order");
    offer.click();
    await page.idle();

    assert.equal(page.$('input[name="otype"][value="Limit"]').checked, true);
    assert.equal(page.$("#price").value, "200.00", "starts at the current price");
    assert.equal(page.$("#submit").disabled, false);
    assert.match(page.message, /^Switched to a limit order at \$200\.00, the current price\. .*press Place buy limit\.$/);
    assert.equal(page.api.posts("/api/orders").length, 0, "nothing is placed for you");
  });

  it("offers a limit order when the market closed while the form was open", async () => {
    page = await openPage();
    page.api.once("POST", "/api/trades", () => ({ status: 400, body: { error: "The market is closed. It opens Monday at 9:30 AM ET. You can place a limit order now; it's checked once the market opens." } }));
    await page.placeOrder({ symbol: "AAPL", quantity: 5 });

    assert.match(page.message, /^The market is closed/);
    assert.equal(fixButton().textContent, "Use a limit order");
    fixButton().click();
    await page.idle();
    assert.equal(page.$('input[name="otype"][value="Limit"]').checked, true);
    assert.equal(page.$("#qty").value, "5", "your quantity stays");
  });

  it("offers the most you can buy when cash runs short", async () => {
    page = await openPage();
    await page.placeOrder({ symbol: "AAPL", quantity: 1000 }); // $200,000 with $100,000 cash

    assert.equal(fixButton().textContent, "Buy the most I can");
    fixButton().click();
    await page.idle();
    assert.equal(page.$("#qty").value, "500");
    assert.equal(page.message, "Set to 500 shares, the most your cash covers. Press Buy to place it.");
    assert.equal(page.api.trades.length, 0, "nothing is bought for you");

    await page.submit();
    assert.equal(page.message, "Bought 500 AAPL at $200.00");
  });

  it("offers to sell everything when you ask for more than you hold", async () => {
    const api = new FakeApi();
    api.positions.set("AAPL", { quantity: 10, avgCost: 150 });
    page = await openPage({ api });
    await page.placeOrder({ symbol: "AAPL", side: "Sell", quantity: 25 });

    assert.match(page.message, /^You only hold 10 shares/);
    assert.equal(fixButton().textContent, "Sell all 10");
    fixButton().click();
    await page.idle();
    assert.equal(page.$("#qty").value, "10");
    assert.equal(page.message, "Set to all 10 shares you hold. Press Sell to place it.");
  });

  it("offers nothing for refusals the form can't fix", async () => {
    page = await openPage();
    page.api.once("POST", "/api/trades", () => ({ status: 503, body: { error: "Finnhub took too long to answer." } }));
    await page.placeOrder({ symbol: "AAPL", quantity: 1 });
    assert.equal(page.message, "Finnhub took too long to answer.");
    assert.equal(fixButton(), null);
  });
});

describe("split hint", () => {
  async function holding(price, previousClose) {
    const api = new FakeApi();
    api.prices.NVDA = price;
    api.previousCloses.NVDA = previousClose;
    api.positions.set("NVDA", { quantity: 10, avgCost: previousClose });
    page = await openPage({ api });
    return page.document.querySelector('#holdings tr[data-sym="NVDA"] .split-hint');
  }

  it("asks whether a big drop was a split, and opens the dialog at the likely ratio", async () => {
    const hint = await holding(100, 400); // down 75%: looks like 4-for-1
    assert.equal(hint.textContent, "Down 75.0% today. Was it a split?");
    hint.click();
    await page.idle();

    assert.equal(page.$("#split-dlg").open, true);
    assert.equal(page.text("#split-sym"), "NVDA");
    assert.equal(page.$("#split-to").value, "4");
    assert.equal(page.$("#split-from").value, "1");
    assert.match(page.text("#split-preview"), /10 shares at \$400\.00 become 40 at \$100\.00/);
  });

  it("asks about a reverse split after a big jump", async () => {
    const hint = await holding(300, 30); // up 900%: looks like 1-for-10
    assert.equal(hint.textContent, "Up 900.0% today. Was it a reverse split?");
    hint.click();
    await page.idle();
    assert.equal(page.$("#split-to").value, "1");
    assert.equal(page.$("#split-from").value, "10");
  });

  it("stays quiet for ordinary moves", async () => {
    assert.equal(await holding(170, 200), null); // down 15%
    page.close();
    assert.equal(await holding(330, 200), null); // up 65%
  });

  it("leaves the plain Split button working as before", async () => {
    await holding(200, 199);
    page.$('#holdings [data-split="NVDA"]').click();
    await page.idle();
    assert.equal(page.$("#split-to").value, "2");
    assert.equal(page.$("#split-from").value, "1");
  });
});

describe("answers on screen", () => {
  it("explains a locked page: why, and how to get back in", async () => {
    const api = new FakeApi();
    api.locked = true;
    page = await openPage({ api });
    const text = page.text("#banner");
    assert.match(text, /Open it from the Folio shortcut/);
    assert.match(text, /after 400 days/);
  });

  it("says what happens if you forget the backup passphrase", async () => {
    page = await openPage();
    const text = page.text("#backup-enc");
    assert.match(text, /Forgot it\? Nothing on this PC is lost/);
    assert.match(text, /Older off-PC copies still need the old one/);
  });
});
