// Runs the real wwwroot/index.html and app.js in jsdom against a FakeApi, and drives the page the way a person
// would: typing into fields, picking radio buttons, pressing buttons. jsdom isn't a real browser (no layout, no
// CSP), so this checks the page's behaviour, not how it looks.

import { readFileSync } from "node:fs";
import { JSDOM, VirtualConsole } from "jsdom";
import { FakeApi } from "./fake-api.js";

const wwwroot = new URL("../../wwwroot/", import.meta.url);
const html = readFileSync(new URL("index.html", wwwroot), "utf8");
const appJs = readFileSync(new URL("app.js", wwwroot), "utf8");

export async function openPage({ api = new FakeApi() } = {}) {
  const errors = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("error", (...args) => errors.push(args.join(" ")));
  virtualConsole.on("jsdomError", (e) => errors.push(e.stack || String(e)));

  // The page's own <script> tag is left alone: jsdom doesn't load it ("outside-only"); we run app.js below.
  const dom = new JSDOM(html, { url: "https://localhost:7246/", runScripts: "outside-only", pretendToBeVisual: true, virtualConsole });
  const { window } = dom;
  window.addEventListener("error", (ev) => errors.push(ev.error?.stack || ev.message));

  // What a browser has and jsdom doesn't.
  window.fetch = api.fetch;
  window.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
  window.Element.prototype.scrollIntoView ??= function () {};
  const dialog = window.HTMLDialogElement.prototype;
  dialog.showModal ??= function () { this.open = true; };
  if (!("returnValue" in dialog)) {
    dialog.close = function (value) {
      if (value !== undefined) this.returnValue = value;
      this.open = false;
      this.dispatchEvent(new window.Event("close"));
    };
  }

  // A browser closes a dialog when its <form method="dialog"> is submitted (Cancel, or a button the page doesn't
  // stop), setting returnValue to the button's value. jsdom doesn't, so do it after the page's own handlers ran.
  window.addEventListener("submit", (ev) => {
    const form = ev.target;
    if (ev.defaultPrevented || form.getAttribute("method") !== "dialog") return;
    ev.preventDefault();
    form.closest("dialog")?.close(ev.submitter?.value ?? "");
  });

  // app.js is an ES module with no imports; running it as strict code behaves the same here.
  window.eval(`"use strict";\n${appJs}\n//# sourceURL=app.js`);

  const page = new Page(window, api, errors);
  await page.idle();
  return page;
}

class Page {
  constructor(window, api, errors) {
    this.window = window;
    this.document = window.document;
    this.api = api;
    this.errors = errors;
  }

  $(selector) {
    const el = this.document.querySelector(selector);
    if (!el) throw new Error(`No element matches ${selector}`);
    return el;
  }

  text(selector) {
    return this.$(selector).textContent.replace(/\s+/g, " ").trim();
  }

  /** Waits until the page has no requests in flight and nothing left to run. */
  async idle() {
    for (let calm = 0; calm < 3; ) {
      await new Promise((r) => setImmediate(r));
      calm = this.api.inflight === 0 ? calm + 1 : 0;
    }
  }

  /** Lets pending work run once, without waiting for requests to finish. */
  async tick() {
    for (let i = 0; i < 3; i++) await new Promise((r) => setImmediate(r));
  }

  /** Types a value into a field, firing the input event the page listens for. */
  async type(selector, value) {
    const el = this.$(selector);
    el.value = String(value);
    el.dispatchEvent(new this.window.Event("input", { bubbles: true }));
    await this.idle();
  }

  /** Picks a radio button by group name and value. */
  async choose(name, value) {
    this.$(`input[name="${name}"][value="${value}"]`).click();
    await this.idle();
  }

  /** Enters a symbol in the order form and presses Enter, which loads its quote. */
  async pickSymbol(symbol) {
    const input = this.$("#symbol");
    input.value = symbol;
    input.dispatchEvent(new this.window.KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    await this.idle();
  }

  /** Fills in and submits the order form. */
  async placeOrder({ symbol, side = "Buy", type = "Market", quantity, price }) {
    await this.choose("side", side);
    await this.choose("otype", type);
    await this.pickSymbol(symbol);
    if (price != null) await this.type("#price", price);
    await this.type("#qty", quantity);
    await this.submit();
  }

  /** Presses the order form's main button. */
  async submit() {
    this.$("#submit").click();
    await this.idle();
  }

  get message() {
    return this.text("#trade-msg");
  }

  close() {
    this.window.close(); // stops the page's refresh timer
  }
}
