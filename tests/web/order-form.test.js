// The order form: every order carries an ID, kept until Folio gives a definite answer, so pressing the button
// again after a lost answer can't place the same order twice.

import assert from "node:assert/strict";
import { afterEach, beforeEach, describe, it } from "node:test";
import { bookThen, networkError } from "./fake-api.js";
import { openPage } from "./harness.js";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

let page;
beforeEach(async () => { page = await openPage(); });
afterEach(() => page.close());

const ids = (path = "/api/trades") => page.api.posts(path).map((r) => r.body.clientOrderId);

describe("market orders", () => {
  it("send a random order ID and report the fill", async () => {
    await page.placeOrder({ symbol: "AAPL", quantity: 10 });

    const [post] = page.api.posts("/api/trades");
    assert.deepEqual({ ...post.body, clientOrderId: undefined }, { symbol: "AAPL", side: "Buy", quantity: 10, clientOrderId: undefined });
    assert.match(post.body.clientOrderId, UUID);
    assert.equal(page.message, "Bought 10 AAPL at $200.00");
    assert.equal(page.$("#qty").value, "", "quantity is cleared for the next order");
    assert.match(page.text("#holdings"), /AAPL/);
    assert.match(page.text("#trades"), /AAPL/);
  });

  it("get a new ID for each order, even an identical one", async () => {
    await page.placeOrder({ symbol: "AAPL", quantity: 10 });
    await page.placeOrder({ symbol: "AAPL", quantity: 10 });

    const [a, b] = ids();
    assert.notEqual(a, b);
    assert.equal(page.api.trades.length, 2);
  });

  for (const failure of ["network", 500]) {
    it(`resend the same ID after a lost answer (${failure}), so the order is booked once`, async () => {
      page.api.once("POST", "/api/trades", bookThen(failure));
      await page.placeOrder({ symbol: "AAPL", quantity: 10 });

      assert.match(page.message, /may still have placed it/);
      assert.match(page.message, /won't place it twice/);
      assert.equal(page.$("#qty").value, "10", "the order stays filled in so you can press again");

      await page.submit();

      const [first, retry] = ids();
      assert.equal(retry, first);
      assert.equal(page.api.trades.length, 1, "booked once");
      assert.equal(page.message, "Bought 10 AAPL at $200.00");
    });
  }

  it("start a new ID after a refusal, since nothing was placed", async () => {
    await page.placeOrder({ symbol: "AAPL", quantity: 1000 }); // $200,000: more than the cash

    assert.match(page.message, /^Insufficient cash/);
    assert.doesNotMatch(page.message, /may still have placed it/);

    page.api.cash = 500_000;
    await page.submit();

    const [refused, retry] = ids();
    assert.notEqual(retry, refused);
    assert.equal(page.api.trades.length, 1);
  });

  it("start a new ID after Finnhub is down (503), since nothing was placed", async () => {
    page.api.once("POST", "/api/trades", () => ({ status: 503, body: { error: "Finnhub took too long to answer." } }));
    await page.placeOrder({ symbol: "AAPL", quantity: 10 });

    assert.equal(page.message, "Finnhub took too long to answer.");
    await page.submit();

    const [failed, retry] = ids();
    assert.notEqual(retry, failed);
  });

  it("start a new ID when you change the order after a lost answer", async () => {
    page.api.once("POST", "/api/trades", networkError);
    await page.placeOrder({ symbol: "AAPL", quantity: 10 });
    await page.type("#qty", 5);
    await page.submit();

    const [first, changed] = ids();
    assert.notEqual(changed, first);
  });

  it("start a new ID when you switch from buy to sell after a lost answer", async () => {
    page.api.positions.set("AAPL", { quantity: 10, avgCost: 150 });
    page.api.once("POST", "/api/trades", networkError);
    await page.placeOrder({ symbol: "AAPL", quantity: 10 });
    await page.choose("side", "Sell");
    await page.type("#qty", 10);
    await page.submit();

    const [buy, sell] = page.api.posts("/api/trades").map((r) => r.body);
    assert.equal(sell.side, "Sell");
    assert.notEqual(sell.clientOrderId, buy.clientOrderId);
  });

  it("can't be sent twice while the first is still on its way", async () => {
    let answer;
    page.api.once("POST", "/api/trades", ({ proceed }) => new Promise((resolve) => { answer = () => resolve(proceed()); }));
    await page.choose("otype", "Market");
    await page.pickSymbol("AAPL");
    await page.type("#qty", 10);

    page.$("#submit").click();
    await page.tick();
    assert.equal(page.$("#submit").disabled, true, "the button is disabled while waiting");
    page.$("#submit").click(); // a double-click
    await page.tick();

    answer();
    await page.idle();
    assert.equal(page.api.posts("/api/trades").length, 1);
    assert.equal(page.$("#submit").disabled, false, "and enabled again afterwards");
  });
});

describe("limit and stop orders", () => {
  it("send the type, price and an order ID", async () => {
    await page.placeOrder({ symbol: "AAPL", type: "Limit", quantity: 10, price: 190 });

    const [post] = page.api.posts("/api/orders");
    assert.equal(post.body.type, "Limit");
    assert.equal(post.body.price, 190);
    assert.match(post.body.clientOrderId, UUID);
    assert.match(page.message, /^Placed buy limit: 10 AAPL at \$190\.00/);
    assert.match(page.text("#orders"), /AAPL/);
  });

  it("resend the same ID after a lost answer, so one order rests", async () => {
    page.api.once("POST", "/api/orders", bookThen("network"));
    await page.placeOrder({ symbol: "AAPL", type: "Stop", quantity: 10, price: 210 });
    await page.submit();

    const [first, retry] = ids("/api/orders");
    assert.equal(retry, first);
    assert.equal(page.api.orders.length, 1);
  });

  it("start a new ID when the price changes after a lost answer", async () => {
    page.api.once("POST", "/api/orders", networkError);
    await page.placeOrder({ symbol: "AAPL", type: "Limit", quantity: 10, price: 190 });
    await page.type("#price", 185);
    await page.submit();

    const [first, changed] = ids("/api/orders");
    assert.notEqual(changed, first);
  });
});
