// Reset and Delete live in the portfolio's Settings, out of the top bar, and only work once you've typed the
// portfolio's name: wiping a portfolio takes a deliberate act, not one misplaced click.

import assert from "node:assert/strict";
import { afterEach, describe, it } from "node:test";
import { FakeApi } from "./fake-api.js";
import { openPage } from "./harness.js";

let page;
afterEach(() => page?.close());

const resets = () => page.api.posts("/api/portfolio/reset");
const deletes = () => page.api.requests.filter((r) => r.method === "DELETE" && r.path.startsWith("/api/portfolios/"));

async function openSettings() {
  page.$("#pf-manage").click();
  await page.idle();
  assert.equal(page.$("#pf-manage-dlg").open, true);
}

describe("reset", () => {
  it("isn't in the top bar any more", async () => {
    page = await openPage();
    assert.equal(page.document.querySelector("#reset"), null);
    const topBar = page.text("header");
    assert.doesNotMatch(topBar, /Reset/);
    assert.match(topBar, /Refresh/);
  });

  it("is in Settings, and asks for the portfolio's name before it does anything", async () => {
    page = await openPage();
    await openSettings();
    page.$("#pf-reset").click();
    await page.idle();

    assert.equal(page.$("#pf-manage-dlg").open, false, "Settings closes");
    assert.equal(page.$("#reset-dlg").open, true);
    assert.equal(page.text("#reset-dlg h2"), "Reset “My portfolio”?");
    assert.equal(page.text("#reset-confirm-name"), "My portfolio");
    assert.equal(page.$("#reset-cash").value, "100000", "starts from the current starting cash");
    assert.equal(page.$("#reset-ok").disabled, true);

    await page.type("#reset-confirm", "My portfol");
    assert.equal(page.$("#reset-ok").disabled, true, "a partial name isn't enough");
    await page.type("#reset-confirm", "my portfolio");
    assert.equal(page.$("#reset-ok").disabled, true, "nor the wrong case");
    page.$("#reset-ok").click();
    await page.idle();
    assert.equal(resets().length, 0, "pressing a disabled Reset does nothing");
  });

  it("resets with the new cash once the name is typed", async () => {
    page = await openPage();
    page.api.positions.set("AAPL", { quantity: 10, avgCost: 150 });
    await openSettings();
    page.$("#pf-reset").click();
    await page.idle();

    await page.type("#reset-cash", 25000);
    await page.type("#reset-confirm", "  My portfolio "); // surrounding spaces are fine
    assert.equal(page.$("#reset-ok").disabled, false);
    page.$("#reset-ok").click();
    await page.idle();

    assert.deepEqual(resets().map((r) => r.body), [{ startingCash: 25000 }]);
    assert.equal(page.$("#reset-dlg").open, false);
    assert.match(page.text(".kpis"), /Total value \$25,000\.00/);
    assert.match(page.text("#holdings"), /No positions yet/);
    assert.equal(page.text("#toast"), "Reset “My portfolio” with $25,000.00.");
  });

  it("keeps the dialog open and shows why when the server refuses", async () => {
    page = await openPage();
    await openSettings();
    page.$("#pf-reset").click();
    await page.idle();
    await page.type("#reset-cash", 0);
    await page.type("#reset-confirm", "My portfolio");
    page.$("#reset-ok").click();
    await page.idle();

    assert.equal(page.$("#reset-dlg").open, true);
    assert.equal(page.text("#reset-msg"), "Starting cash must be between $1 and $1,000,000,000.");
  });

  it("starts with an empty name box each time", async () => {
    page = await openPage();
    await openSettings();
    page.$("#pf-reset").click();
    await page.idle();
    await page.type("#reset-confirm", "My portfolio");
    page.$('#reset-dlg button[value="cancel"]').click();
    await page.idle();

    await openSettings();
    page.$("#pf-reset").click();
    await page.idle();
    assert.equal(page.$("#reset-confirm").value, "");
    assert.equal(page.$("#reset-ok").disabled, true);
  });
});

describe("delete", () => {
  it("is turned off for your only portfolio, with the reason", async () => {
    page = await openPage();
    await openSettings();
    assert.equal(page.$("#pf-delete").disabled, true);
    assert.match(page.text("#pf-delete-note"), /only portfolio can't be deleted\. Reset it/);
  });

  it("asks for the portfolio's name, then deletes it", async () => {
    const api = new FakeApi();
    api.addPortfolio("Growth");
    page = await openPage({ api });
    await openSettings();
    assert.equal(page.$("#pf-delete").disabled, false);
    page.$("#pf-delete").click();
    await page.idle();

    assert.equal(page.$("#delete-dlg").open, true);
    assert.equal(page.text("#delete-dlg h2"), "Delete “My portfolio”?");
    assert.equal(page.$("#delete-ok").disabled, true);
    page.$("#delete-ok").click();
    await page.idle();
    assert.equal(deletes().length, 0);

    await page.type("#delete-confirm", "My portfolio");
    page.$("#delete-ok").click();
    await page.idle();

    assert.deepEqual(deletes().map((r) => r.path), ["/api/portfolios/1"]);
    assert.equal(page.$("#delete-dlg").open, false);
    assert.equal(page.text("#toast"), "Deleted “My portfolio”.");
  });

  it("names the portfolio safely, as text", async () => {
    const api = new FakeApi();
    api.addPortfolio("Growth");
    page = await openPage({ api });
    const evil = `<img src="x" onerror="window.pwned=1">`;
    // Rename the open portfolio through the fake's summary, as if the server had stored this name.
    const summary = api.summary.bind(api);
    api.summary = () => ({ ...summary(), name: evil });
    page.$("#refresh").click();
    await page.idle();

    await openSettings();
    page.$("#pf-delete").click();
    await page.idle();
    assert.equal(page.text("#delete-confirm-name"), evil);
    assert.equal(page.document.querySelector('img[src="x"]'), null);
    assert.equal(page.window.pwned, undefined);
  });
});
