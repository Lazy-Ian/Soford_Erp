import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const source = readFileSync(new URL("../assets/site.js", import.meta.url), "utf8");

function setup(fetch) {
  const context = vm.createContext({
    window: {}, location: { hostname: "localhost", href: "http://localhost:5180/index.html?product=42", hash: "" },
    document: { createElement: () => ({ addEventListener() {} }), body: { dataset: {}, append() {} },
      addEventListener() {}, querySelector: () => null, querySelectorAll: () => [] },
    addEventListener() {}, fetch, AbortSignal, Error, TypeError,
  });
  vm.runInContext(source, context);
  const button = { textContent: "Send inquiry", disabled: false };
  const note = { textContent: "" };
  let submit;
  let resetCount = 0;
  const form = {
    elements: Object.fromEntries(Object.entries({ name: " Jane ", email: "buyer@example.com", company: "Gym Ltd", country: "Australia",
      product: "Gym Tiles", quantity: "500 m²", message: "Please quote these tiles.", website: "" }).map(([k, value]) => [k, { value }])),
    querySelector: (selector) => selector === '.form-note' ? note : button,
    reportValidity: () => true, setAttribute() {}, reset() { resetCount++; },
    addEventListener(event, callback) { if (event === "submit") submit = callback; },
  };
  context.bindInquiryForm(form, { id: "42", title: "Gym Tiles" });
  return { form, button, note, submit: () => submit({ preventDefault() {} }), resets: () => resetCount };
}

test("rejects placeholder phone numbers used as contact details", () => {
  const context = vm.createContext({
    window: {}, location: { hostname: "example.com", href: "https://example.com/", hash: "" },
    document: { createElement: () => ({ addEventListener() {} }), body: { dataset: {}, append() {} },
      addEventListener() {}, querySelector: () => null, querySelectorAll: () => [] },
    addEventListener() {},
  });
  vm.runInContext(source, context);
  assert.equal(context.siteDisplayValue("phone", "+86 000 0000 0000"), "");
  assert.equal(context.siteDisplayValue("phone", "+86 532 1234 5678"), "+86 532 1234 5678");
  assert.equal(context.siteDisplayValue("email", "sales@soford.cn"), "sales@soford.cn");
});

test("submits a product inquiry once while busy and resets only after acknowledged success", async () => {
  let resolve;
  const requests = [];
  const ui = setup((url, init) => {
    requests.push({ url, init });
    return new Promise((done) => { resolve = done; });
  });
  const first = ui.submit();
  assert.equal(ui.button.disabled, true);
  await ui.submit();
  assert.equal(requests.length, 1);
  const payload = JSON.parse(requests[0].init.body);
  assert.equal(payload.name, "Jane");
  assert.equal(payload.productId, "42");
  assert.equal(payload.pageUrl, "http://localhost:5180/index.html?product=42");
  assert.equal(requests[0].init.credentials, "omit");
  resolve({ ok: true, json: async () => ({ received: true }) });
  await first;
  assert.equal(ui.resets(), 1);
  assert.equal(ui.button.disabled, false);
  assert.match(ui.note.textContent, /has been received/);
});

test("does not attach a product ID when the buyer changes the product", async () => {
  let payload;
  const ui = setup(async (_, init) => {
    payload = JSON.parse(init.body);
    return { ok: true, json: async () => ({ received: true }) };
  });
  ui.form.elements.product.value = "EVA Foam Mats";
  await ui.submit();
  assert.equal(payload.productId, "");
});

for (const [name, response, pattern] of [
  ["rate limit", async () => ({ ok: false, status: 429, json: async () => ({}) }), /Too many inquiries/],
  ["validation", async () => ({ ok: false, status: 400, json: async () => ({ detail: "Please enter a valid email address." }) }), /valid email/],
  ["proxy failure", async () => ({ ok: false, status: 502, json: async () => { throw new Error("HTML"); } }), /Unable to send/],
  ["network failure", async () => { throw new TypeError("Failed to fetch"); }, /Unable to connect/],
  ["unconfirmed response", async () => ({ ok: true, json: async () => ({}) }), /Unable to confirm/],
  ["timeout", async () => { throw Object.assign(new Error("Timeout"), { name: "TimeoutError" }); }, /before sending again/],
]) {
  test(`${name} preserves input and allows another attempt`, async () => {
    const ui = setup(response);
    await ui.submit();
    assert.equal(ui.resets(), 0);
    assert.equal(ui.form.elements.message.value, "Please quote these tiles.");
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.button.textContent, "Send inquiry");
    assert.match(ui.note.textContent, pattern);
  });
}
