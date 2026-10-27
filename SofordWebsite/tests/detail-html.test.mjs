import test from "node:test";
import assert from "node:assert/strict";
import { detailScript, sanitizeDetail } from "../scripts/detail-html.mjs";

test("strips scripts and event handlers from product detail html", () => {
  const clean = sanitizeDetail(`<p onclick="alert(1)">Hello</p><script>alert(1)</script><img src="//cdn.example/a.jpg">`);
  assert.doesNotMatch(clean, /script|onclick/i);
  assert.match(clean, /<p>Hello<\/p>/);
  assert.match(clean, /src="https:\/\/cdn\.example\/a\.jpg"/);
});

test("writes a script that cannot close the host script tag", () => {
  const file = detailScript(`<p></script><img src="javascript:alert(1)">ok</p>`);
  assert.doesNotMatch(file, /<\/script>/i);
  assert.match(file, /^window\.SOFORD_DETAIL = /);
});
