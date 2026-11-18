// First run: without a Finnhub key, Folio asks for one on the page, so nobody needs a terminal to get started.

import assert from "node:assert/strict";
import { afterEach, describe, it } from "node:test";
import { FakeApi } from "./fake-api.js";
import { openPage } from "./harness.js";

let page;
afterEach(() => page?.close());

async function firstRun() {
  const api = new FakeApi();
  api.configured = false;
  page = await openPage({ api });
}

const setups = () => page.api.posts("/api/setup/finnhub-key");

async function saveKey(key) {
  await page.type("#setup-key", key);
  page.$("#setup-save").click();
  await page.idle();
}

describe("first-run setup", () => {
  it("opens by itself when there's no key", async () => {
    await firstRun();

    assert.equal(page.$("#setup-dlg").open, true);
    assert.equal(page.text("#setup-h"), "Connect to Finnhub");
    assert.equal(page.document.activeElement, page.$("#setup-key"), "the key field has focus, ready to paste");
    assert.match(page.text("#banner"), /No Finnhub key yet/);
    assert.doesNotMatch(page.text("#banner"), /user-secrets/, "no terminal instructions any more");
  });

  it("doesn't open when Folio already has a key", async () => {
    page = await openPage();
    assert.equal(page.$("#setup-dlg").open, false);
    assert.equal(page.$("#banner").hidden, true);
  });

  it("links to Finnhub's free sign-up in a new tab, without telling Finnhub where you came from", async () => {
    await firstRun();
    const link = page.$(".setup-steps a");
    assert.equal(link.href, "https://finnhub.io/register");
    assert.equal(link.target, "_blank");
    assert.deepEqual(link.rel.split(" ").sort(), ["noopener", "noreferrer"]);
  });

  it("can be put off, and reopened from the banner", async () => {
    await firstRun();
    page.$('#setup-dlg button[value="cancel"]').click();
    await page.idle();
    assert.equal(page.$("#setup-dlg").open, false);

    page.$("#banner [data-setup]").click();
    await page.idle();
    assert.equal(page.$("#setup-dlg").open, true);
  });

  it("asks for a key before sending anything", async () => {
    await firstRun();
    await saveKey("   ");
    assert.equal(page.text("#setup-msg"), "Paste your Finnhub key first.");
    assert.equal(setups().length, 0);
  });

  it("shows why a key was refused and lets you try again", async () => {
    await firstRun();
    await saveKey("wrongKey99999");

    assert.equal(page.$("#setup-dlg").open, true);
    assert.match(page.text("#setup-msg"), /^Finnhub didn't accept that key/);
    assert.equal(page.$("#setup-save").disabled, false);
    assert.equal(page.text("#setup-save"), "Save key");
    assert.equal(page.$("#setup-key").value, "wrongKey99999", "the key stays so you can fix a typo");
  });

  it("says it's checking while Finnhub is asked", async () => {
    await firstRun();
    let answer;
    page.api.once("POST", "/api/setup/finnhub-key", ({ proceed }) => new Promise((resolve) => { answer = () => resolve(proceed()); }));
    await page.type("#setup-key", "goodFinnhubKey123");
    page.$("#setup-save").click();
    await page.tick();

    assert.equal(page.$("#setup-save").disabled, true);
    assert.equal(page.text("#setup-save"), "Checking…");
    answer();
    await page.idle();
  });

  it("saves a good key and turns trading on, without a restart", async () => {
    await firstRun();
    await page.pickSymbol("AAPL");
    await page.type("#qty", 1);
    assert.equal(page.$("#submit").disabled, true, "no trading without a key");

    await saveKey("  goodFinnhubKey123 ");

    assert.deepEqual(setups().map((r) => r.body), [{ apiKey: "goodFinnhubKey123" }]);
    assert.equal(page.$("#setup-dlg").open, false);
    assert.equal(page.$("#setup-key").value, "", "the page doesn't keep the key");
    assert.equal(page.$("#banner").hidden, true);
    assert.equal(page.$("#submit").disabled, false, "trading is on");
    assert.match(page.text("#toast"), /^Connected to Finnhub/);

    await page.submit();
    assert.equal(page.message, "Bought 1 AAPL at $200.00");
  });
});
