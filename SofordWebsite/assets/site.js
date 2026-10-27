// Offline fallback when /public/site cannot be loaded. The ERP「官网」page is the live source
// for company, address, Alibaba store, contacts and FAQ wording.
const SITE = {
  company: "Qingdao SoFord Import And Export Co., Ltd.",
  contactName: "Wendy Wang",
  contactTitle: "Trade Manager",
  email: "Wendy91715@foxmail.com",
  phone: "+86 185 6280 9257",
  whatsapp: "8618562809257", // digits only, country code included
  wechat: "+86 185 6280 9257",
  qq: "2929004105",
  address: "Qingdao, Shandong, China",
  alibabaStore: "", // e.g. "https://soford.en.alibaba.com"; empty hides the link
  showPrices: true,
  // Confirmed terms are in the copy. Quotation is after verification, not a 24-hour promise.
  claimsVerified: true,
  // Local: hit the API directly. Path host (stepnex.cn/soford/): same-prefix proxy.
  // Dedicated domain later: same-origin /public/inquiries.
  inquiryEndpoint: (() => {
    const host = location.hostname || "";
    const path = location.pathname || "/";
    if (host === "localhost" || host === "127.0.0.1") return "http://localhost:5153/public/inquiries";
    if (path === "/soford" || path.startsWith("/soford/")) return "/soford/public/inquiries";
    return "/public/inquiries";
  })(),
  // Same product store as the ERP (products.json), served by the API.
  catalogEndpoint: (() => {
    const host = location.hostname || "";
    const path = location.pathname || "/";
    if (host === "localhost" || host === "127.0.0.1") return "http://localhost:5153/public/products";
    if (path === "/soford" || path.startsWith("/soford/")) return "/soford/public/products";
    return "/public/products";
  })(),
};

const profileEndpoint = (() => {
  const host = location.hostname || "";
  const path = location.pathname || "/";
  if (host === "localhost" || host === "127.0.0.1") return "http://localhost:5153/public/site";
  if (path === "/soford" || path.startsWith("/soford/")) return "/soford/public/site";
  return "/public/site";
})();

const fallbackContacts = [{
  name: SITE.contactName,
  title: SITE.contactTitle,
  email: SITE.email,
  phone: SITE.phone,
  whatsapp: SITE.whatsapp,
  wechat: SITE.wechat,
  qq: SITE.qq,
}];
let publicProfile = null;

/** Reject known placeholders so they never render as real contact details. */
function t(key, fallback) {
  return window.sofordT ? window.sofordT(key, fallback) : (fallback ?? key);
}
function tf(key, fallback, vars) {
  let text = t(key, fallback);
  if (vars) {
    for (const [name, value] of Object.entries(vars)) text = text.split(`{${name}}`).join(String(value));
  }
  return text;
}
function catName(category) { return t(`cat.${category.id}.name`, category.name); }
function catBlurb(category) { return publicProfile?.terms?.warranty && category.id !== "eva-mats" ? publicProfile.terms.warranty : t(`cat.${category.id}.blurb`, category.blurb); }

function siteDisplayValue(key, value) {
  if (value == null) return "";
  const text = String(value).trim();
  if (!text) return "";
  if (key === "phone" && /(?:^|\s)0{3,}[\s-]*0{3,}/.test(text)) return "";
  if ((key === "whatsapp" || key === "wechat") && /^0+$/.test(text.replace(/\D/g, ""))) return "";
  if (key === "qq" && !/^\d{5,12}$/.test(text)) return "";
  return text;
}

function formatPhone(value) {
  const digits = String(value).replace(/\D/g, "");
  if (digits.startsWith("86") && digits.length === 13) {
    return `+86 ${digits.slice(2, 5)} ${digits.slice(5, 9)} ${digits.slice(9)}`;
  }
  const text = String(value).trim();
  if (text.startsWith("+")) return text;
  return digits ? `+${digits}` : text;
}

/** WeChat IDs are shown as typed. Phone-shaped values are spaced like a phone number. */
function formatWechat(value) {
  const text = String(value).trim();
  return /^\+?[\d][\d\s-]*$/.test(text) ? formatPhone(text) : text;
}

const CATEGORIES = [
  {
    id: "gym-tiles",
    name: "Rubber Gym Tiles",
    blurb: "High-density 15–50 mm tiles for free-weight zones, CrossFit boxes and commercial gyms. 6–8 year warranty.",
  },
  {
    id: "rubber-rolls",
    name: "Rubber Flooring Rolls",
    blurb: "Seamless 4–10 mm rolls in 1–1.25 m widths, cut to length for large fitness areas. 6–8 year warranty.",
  },
  {
    id: "outdoor-playground",
    name: "Outdoor & Playground",
    blurb: "UV-stable EPDM tiles, dog-bone pavers and safety surfaces for parks and play areas. 6–8 year warranty.",
  },
  {
    id: "artificial-grass",
    name: "Artificial Grass",
    blurb: "PP+PE turf for football pitches, landscaping and gardens, with 6–8 year warranty.",
  },
  {
    id: "eva-mats",
    name: "EVA Foam Mats",
    blurb: "Interlocking tatami and puzzle mats for martial arts, kids' rooms and home gyms.",
  },
];

let PRODUCTS = [];
let byId = new Map();
let catalogError = "";
const categoryOf = (id) => CATEGORIES.find((c) => c.id === id);
const inCategory = (id) => PRODUCTS.filter((p) => p.category === id);

const escapeHtml = (s) =>
  String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const unitLabel = (unit) => (unit === "Square Meter" ? "m²" : unit === "Piece" ? "pc" : unit);
const currencyOf = (p) => /^[A-Z]{3}$/.test(p.currency || "") ? p.currency : "USD";
const money = (n, currency = "USD") => `${currency === "USD" ? "$" : `${currency} `}${n.toFixed(2).replace(/\.00$/, "")}`;
const priceText = (p) =>
  p.priceMin === p.priceMax ? money(p.priceMin, currencyOf(p)) : `${money(p.priceMin, currencyOf(p))}–${money(p.priceMax, currencyOf(p))}`;
const spec = (p, label) => p.specs.find(([k]) => k === label)?.[1];
const leadTimeText = (p) => p.leadTimeDays > 0 ? String(p.leadTimeDays) : t("pd.confirmLead", "Please confirm");
const comparePrice = (a, b) => currencyOf(a).localeCompare(currencyOf(b)) || a.unit.localeCompare(b.unit) || a.priceMin - b.priceMin;

function thicknessNumbers(raw) {
  const text = String(raw || "");
  if (!/mm/i.test(text)) return [];
  return [...text.matchAll(/(\d+(?:\.\d+)?)/g)].map((m) => Number(m[1])).filter((n) => n > 0 && n <= 80);
}

function matchesThick(raw, bucket) {
  if (bucket === "all") return true;
  const nums = thicknessNumbers(raw);
  if (!nums.length) return false;
  if (bucket === "10") return nums.some((n) => n <= 10);
  if (bucket === "25") return nums.some((n) => n > 10 && n < 30);
  if (bucket === "50") return nums.some((n) => n >= 30 && n <= 80);
  return true;
}

function productThickRaw(p) {
  return spec(p, "Thickness") || spec(p, "Pile height") || "";
}

function rangeLabel(nums) {
  if (!nums.length) return "";
  const min = Math.min(...nums);
  const max = Math.max(...nums);
  return min === max ? `${min} mm` : `${min}–${max} mm`;
}

function shortTitle(title) {
  const clean = String(title || "").replace(/\s+/g, " ").trim();
  if (clean.length <= 60) return clean;
  return `${clean.slice(0, 57).trimEnd()}…`;
}

function fromPrice(items) {
  if (!items.length) return "";
  const counts = {};
  items.forEach((p) => { const key = `${currencyOf(p)}:${p.unit}`; counts[key] = (counts[key] ?? 0) + 1; });
  const group = Object.keys(counts).sort((a, b) => counts[b] - counts[a])[0];
  const comparable = items.filter((p) => `${currencyOf(p)}:${p.unit}` === group);
  const first = comparable[0];
  const min = Math.min(...comparable.map((p) => p.priceMin));
  return `${money(min, currencyOf(first))} / ${unitLabel(first.unit)}`;
}

function cardHtml(p) {
  const cat = categoryOf(p.category);
  const bits = [catName(cat)];
  const thick = rangeLabel(thicknessNumbers(productThickRaw(p)));
  if (thick) bits.push(thick);
  const color = (spec(p, "Color") || "").split(/[,/]/)[0].trim();
  if (color && color.length <= 18) bits.push(color);
  const quoteHref = `index.html?product=${encodeURIComponent(p.id)}#contact`;
  const sampleHref = `index.html?product=${encodeURIComponent(p.id)}&sample=1#contact`;
  return `
    <article class="card">
      <a class="card-open" href="product.html?id=${encodeURIComponent(p.id)}" target="_blank" rel="noopener noreferrer">
        <div class="card-img"><img src="${escapeHtml(p.images[0])}" alt="" loading="lazy" referrerpolicy="no-referrer"></div>
        <div class="card-body">
          <span class="card-tag">${escapeHtml(bits.join(" · "))}</span>
          <h3>${escapeHtml(shortTitle(p.title))}</h3>
          <div class="card-meta">
            ${SITE.showPrices ? `<span class="price">${escapeHtml(priceText(p))} <small>/ ${escapeHtml(unitLabel(p.unit))}</small></span>` : ""}
            <span>${escapeHtml(tf("card.moq", "MOQ {n} {unit}", { n: p.moq, unit: unitLabel(p.unit) }))}</span>
          </div>
        </div>
      </a>
      <div class="card-actions">
        <a class="quote" href="${quoteHref}">${escapeHtml(t("card.quote", "Quote"))}</a>
        <a href="${sampleHref}">${escapeHtml(t("card.sample", "Sample"))}</a>
      </div>
    </article>`;
}

/* ---------- Product page ---------- */

function loadDetail(id) {
  if (!/^[\w-]+$/.test(id)) return Promise.resolve("");
  return fetch(`${SITE.catalogEndpoint}/${encodeURIComponent(id)}`, {
    credentials: "omit",
    signal: AbortSignal.timeout(20_000),
  }).then(async (response) => {
    if (!response.ok) return "";
    const body = await response.json().catch(() => ({}));
    return typeof body.description === "string" ? body.description : "";
  }).catch(() => "");
}

async function loadCatalog() {
  const response = await fetch(SITE.catalogEndpoint, { credentials: "omit", signal: AbortSignal.timeout(20_000) });
  if (!response.ok) throw new Error(String(response.status));
  const items = await response.json();
  if (!Array.isArray(items)) throw new Error("catalog");
  PRODUCTS = items;
  byId = new Map(PRODUCTS.map((p) => [String(p.id), p]));
}

function sanitizeDetail(html) {
  const box = document.createElement("template");
  if (!("innerHTML" in box) || !box.content) return "";
  box.innerHTML = String(html || "");
  const allowed = new Set(["DIV", "SPAN", "P", "BR", "STRONG", "B", "EM", "I", "U", "UL", "OL", "LI", "H1", "H2", "H3", "H4", "TABLE", "THEAD", "TBODY", "TR", "TH", "TD", "IMG", "A", "SUP", "SUB", "HR", "FIGURE", "FIGCAPTION", "COLGROUP", "COL"]);
  const dropped = new Set(["SCRIPT", "STYLE", "IFRAME", "OBJECT", "EMBED", "FORM", "INPUT", "BUTTON", "TEXTAREA", "SELECT", "SVG", "VIDEO", "AUDIO", "NOSCRIPT", "LINK", "META"]);
  const walk = (node) => {
    for (const child of [...node.childNodes]) {
      if (child.nodeType === 1) walk(child);
      else if (child.nodeType !== 3) child.remove();
    }
    if (node.nodeType !== 1 || node.tagName === undefined) return;
    if (!allowed.has(node.tagName)) {
      const parent = node.parentNode;
      if (!parent) return;
      if (dropped.has(node.tagName)) node.remove();
      else {
        while (node.firstChild) parent.insertBefore(node.firstChild, node);
        node.remove();
      }
      return;
    }
    for (const attr of [...node.attributes]) {
      const name = attr.name.toLowerCase();
      const value = attr.value.trim();
      if (name.startsWith("on") || name === "id" || name.startsWith("data-")) node.removeAttribute(attr.name);
      else if (name === "href" || name === "src") {
        if (/^(javascript|data):/i.test(value)) node.removeAttribute(attr.name);
        else if (value.startsWith("//")) node.setAttribute(name, `https:${value}`);
        else if (!/^https?:\/\//i.test(value)) node.removeAttribute(attr.name);
      } else if (name === "style") {
        if (/expression|javascript|@import/i.test(value)) node.removeAttribute(attr.name);
      } else if (!["colspan", "rowspan", "alt", "title", "width", "height", "align", "valign", "border", "cellpadding", "cellspacing"].includes(name)) {
        node.removeAttribute(attr.name);
      }
    }
    if (node.tagName === "A" && node.getAttribute("href")) {
      node.setAttribute("rel", "noopener noreferrer");
      node.setAttribute("target", "_blank");
    }
    if (node.tagName === "IMG") node.setAttribute("referrerpolicy", "no-referrer");
  };
  walk(box.content);
  return box.innerHTML;
}

function detailBlank(node) {
  if (node.nodeType === 3) return !node.textContent.replace(/\u00a0/g, " ").trim();
  if (node.nodeType !== 1) return true;
  if (node.tagName === "IMG" || node.querySelector("img")) return false;
  return !(node.textContent || "").replace(/\u00a0/g, " ").trim();
}

function tidyDetail(html) {
  const box = document.createElement("template");
  if (!("innerHTML" in box) || !box.content) return html;
  box.innerHTML = html;
  // Imported Alibaba commercial sections may contain old sales contacts and terms.
  // Keep the technical description; current company terms are shown separately.
  const headings = [...box.content.querySelectorAll("p, h1, h2, h3, h4, strong, b, span")];
  for (const heading of headings) {
    if (!/^(FAQ|Contact\s*Us|Contact\s*Information)$/i.test((heading.textContent || "").trim())) continue;
    let current = heading;
    while (current.parentElement && current.parentElement.textContent.trim() === heading.textContent.trim()) current = current.parentElement;
    let parent = current.parentNode;
    while (current) {
      while (current.nextSibling) current.nextSibling.remove();
      if (current === heading || current.textContent.trim() === heading.textContent.trim()) current.remove();
      if (!parent || parent === box.content) break;
      current = parent;
      parent = current.parentNode;
    }
    break;
  }
  box.content.querySelectorAll("tr").forEach((row) => {
    if ([...row.querySelectorAll("td, th")].some((cell) => /^(?:pa\s*)?(payment\s*(term[s]?)?|delivery\s*time|lead\s*time|warranty|port)$/i.test(cell.textContent.replace(/\s+/g, " ").trim()))) row.remove();
  });
  box.content.querySelectorAll("p").forEach((p) => {
    const text = (p.textContent || "").replace(/\u00a0/g, " ").trim();
    if (!text && !p.querySelector("img")) p.remove();
  });
  // Alibaba puts &nbsp; or an empty cell beside photos, which becomes a blank column.
  box.content.querySelectorAll("p, div, td").forEach((el) => {
    if (![...el.childNodes].some((node) => node.nodeType === 1 && node.tagName === "IMG")) return;
    for (const node of [...el.childNodes]) {
      if (detailBlank(node)) node.remove();
    }
  });
  box.content.querySelectorAll("table").forEach((table) => {
    const imgs = [...table.querySelectorAll("img")];
    if (!imgs.length) return;
    if ((table.textContent || "").replace(/\u00a0/g, " ").trim()) return;
    const wrap = document.createElement("div");
    wrap.className = "pd-photos";
    imgs.forEach((img) => wrap.append(img));
    table.replaceWith(wrap);
  });
  box.content.querySelectorAll("div").forEach((div) => {
    const style = div.getAttribute("style") || "";
    if (/border-bottom/i.test(style) && div.querySelector(":scope > span")) div.classList.add("pd-ali-title");
  });
  return box.innerHTML;
}

function highlightCells(p) {
  const slots = [
    [spec(p, "Thickness"), "pd.thickness", "Thickness"],
    [spec(p, "Pile height"), "pd.pile", "Pile height"],
    [spec(p, "Size"), "pd.size", "Size"],
    [spec(p, "Width"), "pd.width", "Width"],
    [spec(p, "Material"), "pd.material", "Material"],
  ];
  const cells = [];
  for (const [value, key, fallback] of slots) {
    if (!value) continue;
    if (key === "pd.pile" && spec(p, "Thickness")) continue;
    if (key === "pd.width" && spec(p, "Size")) continue;
    cells.push(`<div><small>${escapeHtml(t(key, fallback))}</small><b>${escapeHtml(value)}</b></div>`);
    if (cells.length === 3) break;
  }
  return cells.join("");
}

function inquiryPanel(product) {
  const roles = ["distributor", "contractor", "project"];
  return `
    <form class="form pd-form" data-pd-form>
      <div class="pd-form-head">
        <h3>${escapeHtml(t("pd.quote", "Request a quote"))}</h3>
      </div>
      <div class="field full buyer-type">
        <span>${escapeHtml(t("form.buyer", "I am a"))}</span>
        <div class="segments">
          ${roles.map((id) => `<label><input type="radio" name="buyerType" value="${id}"> <span>${escapeHtml(t(`form.buyer.${id}`, id))}</span></label>`).join("")}
        </div>
      </div>
      <div class="field"><label>${escapeHtml(t("form.name", "Your name *"))}</label><input name="name" required maxlength="100" autocomplete="name"></div>
      <div class="field"><label>${escapeHtml(t("form.email", "Email *"))}</label><input name="email" type="email" required maxlength="200" autocomplete="email"></div>
      <div class="field"><label>${escapeHtml(t("form.company", "Company"))}</label><input name="company" maxlength="200" autocomplete="organization"></div>
      <div class="field"><label>${escapeHtml(t("form.country", "Country"))}</label><input name="country" maxlength="100" autocomplete="country-name"></div>
      <div class="field full"><label>${escapeHtml(t("form.product", "Product"))}</label><input name="product" readonly value="${escapeHtml(product.title)}"></div>
      <div class="field"><label>${escapeHtml(t("form.qty", "Quantity"))}</label><input name="quantity" maxlength="100" placeholder="${escapeHtml(t("form.qtyPh", ""))}"></div>
      <div class="field full"><label>${escapeHtml(t("form.msg", "Message *"))}</label><textarea name="message" required minlength="10" maxlength="5000"></textarea></div>
      <div hidden aria-hidden="true"><label>Website</label><input name="website" tabindex="-1" autocomplete="off"></div>
      <div class="form-confirm" data-confirm>
        <h3>${escapeHtml(t("form.sentTitle", "Inquiry received"))}</h3>
        <p data-confirm-copy></p>
        <p data-confirm-email></p>
      </div>
      <div class="form-actions full">
        <p class="form-note" role="status">${escapeHtml(t("form.privacy", "We never share your details."))}</p>
        <button class="btn btn-primary" type="submit">${escapeHtml(t("form.send", "Send inquiry"))}</button>
      </div>
    </form>`;
}

async function renderProduct() {
  const root = document.querySelector("[data-product-root]");
  if (!root) return;
  if (catalogError) {
    const failedCrumbs = document.querySelector("[data-crumbs]");
    if (failedCrumbs) failedCrumbs.hidden = true;
    root.innerHTML = `<div class="empty"><p>${escapeHtml(catalogError)}</p></div>`;
    return;
  }
  const id = new URLSearchParams(location.search).get("id") || "";
  const p = byId.get(id);
  const crumbs = document.querySelector("[data-crumbs]");
  if (!p) {
    if (crumbs) crumbs.hidden = true;
    root.innerHTML = `<div class="empty"><p>${escapeHtml(t("pd.missing", "This product is no longer listed."))}</p><p><a class="btn btn-dark" href="products.html">${escapeHtml(t("pd.backCatalog", "Back to products"))}</a></p></div>`;
    return;
  }
  const cat = categoryOf(p.category);
  if (crumbs) {
    crumbs.innerHTML = `<a href="index.html">${escapeHtml(t("catalog.home", "Home"))}</a><span aria-hidden="true">/</span><a href="products.html">${escapeHtml(t("nav.products", "Products"))}</a><span aria-hidden="true">/</span><a href="products.html?cat=${encodeURIComponent(p.category)}">${escapeHtml(catName(cat))}</a>`;
  }
  document.title = `${p.title} — Soford`;
  const detailText = tidyDetail(sanitizeDetail(await loadDetail(id))).trim();
  const excerpt = detailText.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
  document.querySelector('meta[name="description"]')?.setAttribute("content", excerpt.slice(0, 160) || p.title);
  const rows = p.specs.filter(([k]) => k !== "Delivery Time").map(([k, v]) => `<tr><th>${escapeHtml(k)}</th><td>${escapeHtml(v)}</td></tr>`).join("");
  const highlights = highlightCells(p);
  const related = inCategory(p.category).filter((item) => item.id !== p.id).slice(0, 3);
  const quoteMsg = tf("form.quoteMsg", "Hello, I am interested in \"{title}\". Please quote for ___ {unit}, delivered to ___.", { title: p.title, unit: unitLabel(p.unit) });
  const sampleMsg = tf("form.sampleMsg", "Hello, I would like to request a sample of \"{title}\". Please let me know the cost and shipping time.", { title: p.title });
  root.innerHTML = `
    <div class="pd">
      <div class="pd-gallery">
        <div class="pd-main">
          <button class="pd-zoom" type="button" aria-label="${escapeHtml(t("pd.zoom", "Enlarge photo"))}">
            <img src="${escapeHtml(p.images[0])}" alt="${escapeHtml(p.title)}" referrerpolicy="no-referrer">
          </button>
        </div>
        <div class="pd-thumbs">
          ${p.images
            .map(
              (src, i) =>
                `<button type="button" data-src="${escapeHtml(src)}"${i === 0 ? ' aria-current="true"' : ""}><img src="${escapeHtml(src)}" alt="" referrerpolicy="no-referrer"></button>`,
            )
            .join("")}
        </div>
      </div>
      <div class="pd-info">
        <span class="eyebrow">${escapeHtml(catName(cat))}</span>
        <h1>${escapeHtml(p.title)}</h1>
        ${highlights ? `<div class="pd-highlight">${highlights}</div>` : ""}
        <div class="pd-buy">
          ${SITE.showPrices ? `<div><small data-price-label>${escapeHtml(publicProfile?.terms?.tradeTerms ? t("pd.price", "Price") : t("pd.fob", "FOB price"))}</small><b>${escapeHtml(priceText(p))}</b><small>${escapeHtml(tf("pd.per", "per {unit}", { unit: unitLabel(p.unit) }))}</small></div>` : ""}
          <div><small>${escapeHtml(t("pd.min", "Minimum order"))}</small><b>${p.moq}</b><small>${escapeHtml(unitLabel(p.unit))}</small></div>
          <div><small>${escapeHtml(t("pd.lead", "Lead time"))}</small><b>${escapeHtml(leadTimeText(p))}</b>${p.leadTimeDays > 0 ? `<small>${escapeHtml(t("pd.days", "days"))}</small>` : ""}</div>
        </div>
        ${rows ? `<table class="specs"><tbody>${rows}</tbody></table>` : ""}
        ${p.keywords.length ? `<div class="chips">${p.keywords.map((k) => `<span class="chip">${escapeHtml(k)}</span>`).join("")}</div>` : ""}
        <div class="pd-actions">
          <a class="btn btn-primary" href="#inquiry">${escapeHtml(t("pd.quote", "Request a quote"))}</a>
          <button class="btn btn-line" type="button" data-pd-sample>${escapeHtml(t("pd.sample", "Ask for a sample"))}</button>
        </div>
      </div>
    </div>
    <section class="pd-detail">
      <h2>${escapeHtml(t("pd.detail", "Product details"))}</h2>
      ${detailText ? `<div class="pd-html">${detailText}</div>` : `<p class="pd-empty">${escapeHtml(t("pd.noDetail", "The full description is not available here. Send an inquiry and we will share the data sheet."))}</p>`}
    </section>
    ${related.length ? `<section class="pd-related"><h2>${escapeHtml(t("pd.related", "More in this line"))}</h2><div class="grid">${related.map(cardHtml).join("")}</div></section>` : ""}
    <section class="pd-inquiry-block" id="inquiry">${inquiryPanel(p)}</section>`;
  const main = root.querySelector(".pd-main img");
  const mainWrap = root.querySelector(".pd-main");
  root.querySelector(".pd-zoom").addEventListener("click", () => mainWrap.classList.toggle("is-zoom"));
  root.querySelectorAll(".pd-thumbs button").forEach((btn) =>
    btn.addEventListener("click", () => {
      main.src = btn.dataset.src;
      mainWrap.classList.remove("is-zoom");
      root.querySelectorAll(".pd-thumbs button").forEach((b) => {
        if (b === btn) b.setAttribute("aria-current", "true");
        else b.removeAttribute("aria-current");
      });
    }),
  );
  const form = root.querySelector("[data-pd-form]");
  root.querySelector("[data-pd-sample]").addEventListener("click", () => {
    form.classList.remove("is-sent");
    form.elements.message.value = sampleMsg;
    root.querySelector(".pd-form-head h3").textContent = t("pd.sample", "Ask for a sample");
    form.elements.name.focus();
  });
  form.elements.message.value = quoteMsg;
  bindInquiryForm(form, p);
}

/* ---------- Header ---------- */

const header = document.querySelector(".site-header");
const onScroll = () => header?.classList.toggle("scrolled", scrollY > 8);
addEventListener("scroll", onScroll, { passive: true });
onScroll();
const toggle = document.querySelector(".menu-toggle");
const nav = document.querySelector(".nav");
toggle?.addEventListener("click", () => {
  const open = nav.classList.toggle("open");
  toggle.setAttribute("aria-expanded", open);
});
nav?.addEventListener("click", (e) => {
  if (e.target.closest("a")) {
    nav.classList.remove("open");
    toggle?.setAttribute("aria-expanded", "false");
  }
});

/* ---------- Contact details & footer ---------- */

const CONTACT_ICONS = {
  person: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="8" r="3"/><path d="M5 19c1.4-3 3.6-4.5 7-4.5s5.6 1.5 7 4.5"/></svg>',
  mail: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 7l9 6 9-6"/></svg>',
  phone: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M5 4h4l2 5-2.5 1.5a11 11 0 005 5L15 13l5 2v4a2 2 0 01-2 2A16 16 0 013 6a2 2 0 012-2"/></svg>',
  chat: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 21l1.6-4.8A9 9 0 1112 21a9 9 0 01-4.2-1z"/></svg>',
  wechat: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 7h10a3 3 0 013 3v3a3 3 0 01-3 3H8l-3 2v-2a3 3 0 01-1-2v-4a3 3 0 013-3z"/><path d="M11 12h8a2 2 0 012 2v2a2 2 0 01-2 2h-1v2l-2-2"/></svg>',
  qq: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="8"/><path d="M9 9.5c.6-1 1.6-1.5 3-1.5 2 0 3 1 3 2.2 0 2.2-6 1.2-6 4.3 0 1.2 1 2.2 3 2.2 1.5 0 2.6-.6 3.2-1.6"/></svg>',
};

function safeHttpUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:" ? url.href : "";
  } catch {
    return "";
  }
}

function safeEmail(value) {
  const text = String(value || "").trim();
  return /^[^\s<>"']+@[^\s<>"']+\.[^\s<>"']+$/.test(text) ? text : "";
}

function normalizePublicContact(raw) {
  const source = raw || {};
  return {
    name: siteDisplayValue("name", source.name),
    title: siteDisplayValue("title", source.title),
    email: safeEmail(siteDisplayValue("email", source.email)),
    phone: siteDisplayValue("phone", source.phone),
    whatsapp: siteDisplayValue("whatsapp", source.whatsapp),
    wechat: siteDisplayValue("wechat", source.wechat),
    qq: siteDisplayValue("qq", source.qq),
  };
}

function contactHasDetail(person) {
  return Object.values(person).some((value) => value);
}

function contactLine(icon, label, inner) {
  return `<div class="contact-line">${icon}<div><small>${escapeHtml(label)}</small>${inner}</div></div>`;
}

function contactCard(person) {
  const rows = [];
  if (person.name || person.title) {
    rows.push(contactLine(CONTACT_ICONS.person, t("contact.person", "Contact"),
      `${person.name ? `<span>${escapeHtml(person.name)}</span>` : ""}${person.title ? `<small class="contact-role">${escapeHtml(person.title)}</small>` : ""}`));
  }
  if (person.email) rows.push(contactLine(CONTACT_ICONS.mail, t("contact.email", "Email"), `<a href="mailto:${escapeHtml(person.email)}">${escapeHtml(person.email)}</a>`));
  if (person.phone) rows.push(contactLine(CONTACT_ICONS.phone, t("contact.phone", "Phone"), `<a href="tel:${person.phone.replace(/[^\d+]/g, "")}">${escapeHtml(formatPhone(person.phone))}</a>`));
  if (person.whatsapp) rows.push(contactLine(CONTACT_ICONS.chat, t("contact.whatsapp", "WhatsApp"), `<a href="https://wa.me/${person.whatsapp.replace(/\D/g, "")}" target="_blank" rel="noopener">${escapeHtml(formatPhone(person.whatsapp))}</a>`));
  if (person.wechat) rows.push(contactLine(CONTACT_ICONS.wechat, t("contact.wechat", "WeChat"), `<span>${escapeHtml(formatWechat(person.wechat))}</span>`));
  if (person.qq) rows.push(contactLine(CONTACT_ICONS.qq, t("contact.qq", "QQ"), `<a href="https://wpa.qq.com/msgrd?v=3&uin=${encodeURIComponent(person.qq.replace(/\D/g, ""))}&site=qq&menu=yes" target="_blank" rel="noopener">${escapeHtml(person.qq)}</a>`));
  return rows.length ? `<div class="contact-person">${rows.join("")}</div>` : "";
}

function footerContactRows(person, gap) {
  const rows = [];
  const gapAttr = gap ? ' class="footer-contact-gap"' : "";
  if (person.name || person.title) {
    rows.push(`<li${gapAttr} data-contact-row><span class="footer-contact-name">${escapeHtml(person.name)}</span>${person.title ? `<span class="footer-contact-role">${escapeHtml(person.title)}</span>` : ""}</li>`);
  }
  if (person.email) rows.push(`<li data-contact-row><a href="mailto:${escapeHtml(person.email)}">${escapeHtml(person.email)}</a></li>`);
  if (person.phone) rows.push(`<li data-contact-row><a href="tel:${person.phone.replace(/[^\d+]/g, "")}">${escapeHtml(formatPhone(person.phone))}</a></li>`);
  if (person.whatsapp) rows.push(`<li data-contact-row><a href="https://wa.me/${person.whatsapp.replace(/\D/g, "")}" target="_blank" rel="noopener">WhatsApp ${escapeHtml(formatPhone(person.whatsapp))}</a></li>`);
  if (person.wechat) rows.push(`<li data-contact-row><span>WeChat ${escapeHtml(formatWechat(person.wechat))}</span></li>`);
  if (person.qq) rows.push(`<li data-contact-row><a href="https://wpa.qq.com/msgrd?v=3&uin=${encodeURIComponent(person.qq.replace(/\D/g, ""))}&site=qq&menu=yes" target="_blank" rel="noopener">QQ ${escapeHtml(person.qq)}</a></li>`);
  return rows.join("");
}

function renderContacts(contacts) {
  const people = contacts.map(normalizePublicContact).filter(contactHasDetail);
  document.querySelectorAll("[data-contacts]").forEach((root) => {
    root.innerHTML = people.map(contactCard).join("");
  });
  document.querySelectorAll("[data-footer-contacts]").forEach((ul) => {
    ul.querySelectorAll("[data-contact-row]").forEach((row) => row.remove());
    const html = people.map((person, index) => footerContactRows(person, index > 0)).join("");
    const inquiry = ul.querySelector("[data-footer-inquiry]");
    if (inquiry) inquiry.insertAdjacentHTML("beforebegin", html);
    else ul.insertAdjacentHTML("afterbegin", html);
  });
}

function applySiteFields() {
  document.querySelectorAll("[data-site]").forEach((el) => {
    const key = el.dataset.site;
    let value = siteDisplayValue(key, SITE[key]);
    if (key === "alibabaStore") value = safeHttpUrl(value);
    const slot = el.closest("[data-site-optional]") ?? el.closest("li") ?? el;
    if (!value) {
      slot.hidden = true;
      return;
    }
    slot.hidden = false;
    if (key === "email") el.href = `mailto:${value}`;
    if (key === "phone") el.href = `tel:${value.replace(/[^\d+]/g, "")}`;
    if (key === "whatsapp") el.href = `https://wa.me/${value.replace(/\D/g, "")}`;
    if (key === "qq") el.href = `https://wpa.qq.com/msgrd?v=3&uin=${encodeURIComponent(value.replace(/\D/g, ""))}&site=qq&menu=yes`;
    if (key === "alibabaStore") el.href = value;
    if (!el.dataset.keepText) {
      const shown = key === "phone" || key === "whatsapp" ? formatPhone(value) : key === "wechat" ? formatWechat(value) : value;
      el.textContent = `${el.dataset.sitePrefix || ""}${shown}`;
    }
  });
}

function applyTerms(terms) {
  if (!terms || typeof terms !== "object") return;
  const set = (key, text) => {
    const value = String(text || "").trim();
    if (!value) return;
    document.querySelectorAll(`[data-term="${key}"]`).forEach((el) => {
      el.textContent = value;
      el.removeAttribute("data-i18n");
    });
  };
  set("moq", terms.moq);
  set("sample", terms.sample);
  set("warranty", terms.warranty);
  set("payment", terms.payment);
  set("leadTime", terms.leadTime);
  set("tradeTerms", terms.tradeTerms);
  const lead = String(terms.leadTime || "").trim();
  const trade = String(terms.tradeTerms || "").trim();
  if (lead || trade) set("lead", [lead, trade].filter(Boolean).join(" "));
  const replaceCopy = (key, value) => {
    if (!value) return;
    document.querySelectorAll(`[data-i18n="${key}"]`).forEach((el) => { el.textContent = value; el.removeAttribute("data-i18n"); });
  };
  replaceCopy("why.reports", terms.warranty);
  replaceCopy("why.sample.body", terms.sample);
  replaceCopy("step.2.body", terms.sample);
  replaceCopy("step.4.body", lead);
  replaceCopy("step.4.need", lead);
  replaceCopy("step.6.body", trade);
  replaceCopy("step.6.need", trade);
  if (lead) document.querySelectorAll('.step [data-i18n="step.4.need"], .step:nth-child(4) .step-need').forEach((el) => { el.hidden = true; });
  if (trade) document.querySelectorAll('.step [data-i18n="step.6.need"], .step:nth-child(6) .step-need').forEach((el) => { el.hidden = true; });
  if (terms.warranty) {
    document.querySelectorAll('.cert-row').forEach((el) => { el.hidden = true; });
    replaceCopy("app.garden.metric", terms.warranty);
    replaceCopy("stats.tested", t("stats.warranty", "Certification & warranty"));
  }
  if (terms.sample || trade) replaceCopy("hero.proof", [trade || t("step.6.body", ""), terms.sample || t("faq.3.a", "")].filter(Boolean).join(" · "));
  if (lead) replaceCopy("stats.lead", t("pd.lead", "Lead time"));
  if (terms.moq) replaceCopy("stats.moq", t("pd.min", "Minimum order"));
}

function applyProfile(profile) {
  if (!profile || typeof profile !== "object") return;
  publicProfile = profile;
  if (typeof profile.company === "string") SITE.company = profile.company;
  if (typeof profile.address === "string") SITE.address = profile.address;
  if (typeof profile.alibabaStore === "string") SITE.alibabaStore = profile.alibabaStore;
  renderContacts(Array.isArray(profile.contacts) ? profile.contacts : fallbackContacts);
  applySiteFields();
  applyTerms(profile.terms);
  if (profile.terms?.tradeTerms) document.querySelectorAll('[data-price-label]').forEach((el) => { el.textContent = t("pd.price", "Price"); });
  document.querySelectorAll('[data-i18n="footer.about"], [data-i18n="footer.rights"]').forEach((el) => {
    const template = el.dataset.companyTemplate || el.textContent;
    el.dataset.companyTemplate = template;
    el.textContent = template.replace("Qingdao SoFord Import And Export Co., Ltd.", SITE.company);
  });
  const category = categoryOf(new URLSearchParams(location.search).get("cat"));
  const categoryCopy = document.querySelector('[data-sub]');
  if (category && categoryCopy) categoryCopy.textContent = catBlurb(category);
}

async function loadPublicSite() {
  try {
    const response = await fetch(profileEndpoint, { credentials: "omit", signal: AbortSignal.timeout(8000) });
    if (!response.ok) return;
    applyProfile(await response.json());
  } catch {
    // Keep the offline fallback already painted.
  }
}

renderContacts(fallbackContacts);
applySiteFields();
document.querySelectorAll("[data-year]").forEach((el) => (el.textContent = new Date().getFullYear()));
document.querySelectorAll("[data-footer-cats]").forEach((ul) => {
  ul.innerHTML = CATEGORIES.map((c) => `<li><a href="products.html?cat=${c.id}">${escapeHtml(catName(c))}</a></li>`).join("");
});
if (typeof fetch === "function" && document.body?.dataset?.page) void loadPublicSite();
if (!SITE.claimsVerified) {
  document.querySelectorAll("[data-claim='reply-sla']").forEach((el) => {
    el.textContent = t("contact.slaSoft", "Get a quote — we reply as soon as we can.");
  });
}

/* ---------- Home page ---------- */

function renderHome() {
  const stats = {
    products: catalogError ? "—" : `${Math.floor(PRODUCTS.length / 50) * 50}+`,
    lines: CATEGORIES.length,
  };
  document.querySelectorAll("[data-stat]").forEach((el) => (el.textContent = stats[el.dataset.stat]));

  // Hand-picked hero and category images; fall back to the first product of the category.
  const heroPicks = ["gym-tiles", "artificial-grass", "outdoor-playground"];
  const heroImgs = document.querySelectorAll(".hero-visual img");
  heroPicks.forEach((id, i) => {
    const items = inCategory(id);
    const pick = items[i === 0 ? 1 : 0] ?? items[0];
    if (heroImgs[i] && pick) heroImgs[i].src = pick.images[0];
  });

  const scenePick = {
    gym: ["gym-tiles", 0],
    home: ["gym-tiles", 4],
    play: ["outdoor-playground", 0],
    field: ["artificial-grass", 0],
    garden: ["artificial-grass", 3],
    martial: ["eva-mats", 0],
  };
  document.querySelectorAll("[data-scene]").forEach((el) => {
    const [cat, index] = scenePick[el.dataset.scene] || [];
    const items = inCategory(cat);
    const pick = items[index] || items[0];
    const img = el.querySelector("img");
    if (img && pick) img.src = pick.images[0];
  });

  document.querySelector("[data-cats]").innerHTML = CATEGORIES.map((c) => {
    const items = inCategory(c.id);
    if (!items.length) return "";
    const range = rangeLabel(items.flatMap((item) => thicknessNumbers(productThickRaw(item))));
    const priceBit = SITE.showPrices
      ? tf("catalog.from", "From {price}", { price: fromPrice(items) })
      : t("catalog.view", "View range");
    return `
      <a class="cat" href="products.html?cat=${c.id}">
        <img src="${escapeHtml(items[0].images[0])}" alt="" loading="lazy" referrerpolicy="no-referrer">
        <div class="cat-body">
          <span class="count">${escapeHtml(tf("catalog.models", "{n} models", { n: items.length }))}</span>
          <h3>${escapeHtml(catName(c))}</h3>
          ${range ? `<span class="range">${escapeHtml(range)}</span>` : ""}
        </div>
        <span class="from">${escapeHtml(t("catalog.viewModels", "View models"))} · ${escapeHtml(priceBit)}</span>
      </a>`;
  }).join("");

  const featuredPlan = { "gym-tiles": 2, "rubber-rolls": 2, "outdoor-playground": 2, "artificial-grass": 1, "eva-mats": 1 };
  const featured = Object.entries(featuredPlan).flatMap(([id, n]) => inCategory(id).slice(0, n));
  document.querySelector("[data-featured]").innerHTML = catalogError
    ? `<div class="empty"><p>${escapeHtml(catalogError)}</p></div>`
    : featured.map(cardHtml).join("");

  // Inquiry form, optionally prefilled from a product dialog link.
  const form = document.querySelector("#inquiry");
  const params = new URLSearchParams(location.search);
  const product = byId.get(params.get("product"));
  const productSelect = form.elements.product;
  productSelect.innerHTML = product
    ? `<option selected>${escapeHtml(product.title)}</option>`
    : `<option value="">${escapeHtml(t("form.general", "General inquiry"))}</option>` +
      CATEGORIES.map((c) => `<option>${escapeHtml(catName(c))}</option>`).join("");
  if (product) {
    productSelect.disabled = true;
    form.elements.message.value = params.get("sample")
      ? tf("form.sampleMsg", "Hello, I would like to request a sample of \"{title}\". Please let me know the cost and shipping time.", { title: product.title })
      : tf("form.quoteMsg", "Hello, I am interested in \"{title}\". Please quote for ___ {unit}, delivered to ___.", { title: product.title, unit: unitLabel(product.unit) });
  }

  bindInquiryForm(form, product);
}

function bindInquiryForm(form, product) {
  const button = form.querySelector('[type="submit"]');
  const note = form.querySelector(".form-note");
  const label = button.textContent;
  let sending = false;
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    if (sending || !form.reportValidity()) return;
    sending = true;
    button.disabled = true;
    button.textContent = t("form.sending", "Sending…");
    form.setAttribute("aria-busy", "true");
    note.textContent = t("form.sendingNote", "Sending your inquiry…");
    const f = form.elements;
    const body = Object.fromEntries(["name", "company", "email", "country", "product", "quantity", "message", "website"]
      .map((key) => [key, f[key].value.trim()]));
    const selectedBuyer = form.querySelector('input[name="buyerType"]:checked');
    const buyerType = selectedBuyer && typeof selectedBuyer.closest === "function"
      ? (selectedBuyer.closest("label")?.textContent || "").replace(/\s+/g, " ").trim()
      : "";
    if (buyerType) body.message = `[${buyerType}] ${body.message}`;
    body.productId = product && body.product === product.title ? product.id : "";
    body.pageUrl = location.href;
    try {
      const response = await fetch(SITE.inquiryEndpoint, {
        method: "POST", credentials: "omit", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body), signal: AbortSignal.timeout(20_000),
      });
      if (!response.ok) {
        const problem = await response.json().catch(() => ({}));
        throw new Error(response.status === 429
          ? t("form.rate", "Too many inquiries. Please try again later, or contact us by email.")
          : problem.detail || problem.title || t("form.fail", "Unable to send. Please try again, or contact us by email."));
      }
      const result = await response.json();
      if (result.received !== true) throw new Error(t("form.unconfirmed", "Unable to confirm delivery. Please contact us by email."));
      const sent = t("form.sent", "Thank you! Your inquiry has been received. Our team will contact you by email.");
      const emailLine = tf("form.sentEmail", "We will reply to {email}.", { email: body.email });
      form.reset();
      form.classList?.add("is-sent");
      const confirm = form.querySelector("[data-confirm]");
      if (confirm && confirm !== button) {
        const copy = confirm.querySelector("[data-confirm-copy]");
        const emailEl = confirm.querySelector("[data-confirm-email]");
        if (copy) copy.textContent = sent;
        if (emailEl) emailEl.textContent = emailLine;
        confirm.hidden = false;
      }
      note.textContent = sent;
    } catch (error) {
      note.textContent = error.name === "TimeoutError"
        ? t("form.timeout", "Delivery could not be confirmed. Please contact us by email before sending again.")
        : error instanceof TypeError ? t("form.network", "Unable to connect. Please check your connection or contact us by email.") : error.message;
    } finally {
      sending = false;
      button.disabled = false;
      button.textContent = label;
      form.setAttribute("aria-busy", "false");
    }
  });
}

/* ---------- Catalog page ---------- */

function renderCatalog() {
  const PAGE = 24;
  const params = new URLSearchParams(location.search);
  const sorts = ["featured", "price-asc", "price-desc", "moq"];
  const thicks = ["all", "10", "25", "50"];
  const state = {
    cat: categoryOf(params.get("cat")) ? params.get("cat") : "all",
    q: params.get("q") ?? "",
    sort: sorts.includes(params.get("sort")) ? params.get("sort") : "featured",
    thick: thicks.includes(params.get("thick")) ? params.get("thick") : "all",
    shown: PAGE,
  };

  const list = document.querySelector("[data-cat-filter]");
  const thickList = document.querySelector("[data-thick-filter]");
  const chips = document.querySelector("[data-chips]");
  const grid = document.querySelector("[data-grid]");
  const count = document.querySelector("[data-count]");
  const more = document.querySelector("[data-more]");
  const search = document.querySelector("[data-search]");
  const sort = document.querySelector("[data-sort]");
  const heading = document.querySelector("[data-heading]");
  const sub = document.querySelector("[data-sub]");
  search.value = state.q;
  sort.value = state.sort;
  if (catalogError) {
    heading.textContent = t("catalog.all", "All products");
    grid.innerHTML = `<div class="empty"><p>${escapeHtml(catalogError)}</p></div>`;
    return;
  }
  const thickOptions = [
    ["all", t("catalog.thickAny", "Any thickness")],
    ["10", t("catalog.thick10", "≤10 mm")],
    ["25", t("catalog.thick25", "15–25 mm")],
    ["50", t("catalog.thick50", "30–50 mm")],
  ];
  thickList.innerHTML = thickOptions
    .map(([id, label]) => `<button type="button" data-thick="${id}">${escapeHtml(label)}</button>`)
    .join("");

  list.innerHTML = [{ id: "all", name: "All products" }, ...CATEGORIES]
    .map((c) => {
      const n = c.id === "all" ? PRODUCTS.length : inCategory(c.id).length;
      return `<button type="button" data-cat="${c.id}">${escapeHtml(c.id === "all" ? t("catalog.all", c.name) : catName(c))}<span>${n}</span></button>`;
    })
    .join("");

  function results() {
    const terms = state.q.toLowerCase().split(/\s+/).filter(Boolean);
    let items = PRODUCTS.filter((p) => (state.cat === "all" || p.category === state.cat) && matchesThick(productThickRaw(p), state.thick));
    if (terms.length) {
      items = items.filter((p) => {
        const hay = `${p.title} ${p.keywords.join(" ")} ${p.specs.map((s) => s[1]).join(" ")}`.toLowerCase();
        return terms.every((t) => hay.includes(t));
      });
    }
    if (state.sort === "price-asc") items = [...items].sort(comparePrice);
    if (state.sort === "price-desc") items = [...items].sort((a, b) => currencyOf(a).localeCompare(currencyOf(b)) || a.unit.localeCompare(b.unit) || b.priceMin - a.priceMin);
    if (state.sort === "moq") items = [...items].sort((a, b) => a.moq - b.moq);
    return items;
  }

  function render() {
    const items = results();
    const cat = categoryOf(state.cat);
    heading.textContent = cat ? catName(cat) : t("catalog.all", "All products");
    sub.textContent = cat
      ? catBlurb(cat)
      : t("catalog.subAll", "Rubber flooring, sports surfaces, artificial turf and EVA mats — factory direct from Shandong, China.");
    list.querySelectorAll("button").forEach((b) => b.setAttribute("aria-pressed", b.dataset.cat === state.cat));
    thickList.querySelectorAll("button").forEach((b) => b.setAttribute("aria-pressed", b.dataset.thick === state.thick));
    const active = [];
    if (state.cat !== "all") active.push(["cat", catName(categoryOf(state.cat))]);
    if (state.thick !== "all") active.push(["thick", thickOptions.find(([id]) => id === state.thick)[1]]);
    if (state.q) active.push(["q", state.q]);
    chips.innerHTML = active.map(([key, label]) =>
      `<button type="button" class="chip chip-x" data-clear="${key}" aria-label="${escapeHtml(t("catalog.remove", "Remove filter"))}: ${escapeHtml(label)}">${escapeHtml(label)} <span aria-hidden="true">×</span></button>`).join("");
    count.textContent = tf(items.length === 1 ? "catalog.count1" : "catalog.count", items.length === 1 ? "{n} product" : "{n} products", { n: items.length });
    grid.innerHTML = items.length
      ? items.slice(0, state.shown).map(cardHtml).join("")
      : `<div class="empty">${tf("catalog.empty", "No products match “{q}”. Try a different keyword or <a href=\"index.html#contact\">ask us directly</a>.", { q: escapeHtml(state.q) })}</div>`;
    more.hidden = items.length <= state.shown;

    const url = new URL(location.href);
    for (const key of ["cat", "q", "sort", "thick"]) url.searchParams.delete(key);
    if (state.cat !== "all") url.searchParams.set("cat", state.cat);
    if (state.q) url.searchParams.set("q", state.q);
    if (state.sort !== "featured") url.searchParams.set("sort", state.sort);
    if (state.thick !== "all") url.searchParams.set("thick", state.thick);
    history.replaceState(null, "", url);
  }

  list.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-cat]");
    if (!btn) return;
    Object.assign(state, { cat: btn.dataset.cat, shown: PAGE });
    render();
  });
  thickList.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-thick]");
    if (!btn) return;
    Object.assign(state, { thick: btn.dataset.thick, shown: PAGE });
    render();
  });
  chips.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-clear]");
    if (!btn) return;
    if (btn.dataset.clear === "cat") state.cat = "all";
    if (btn.dataset.clear === "thick") state.thick = "all";
    if (btn.dataset.clear === "q") {
      state.q = "";
      search.value = "";
    }
    state.shown = PAGE;
    render();
  });
  let timer;
  search.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      Object.assign(state, { q: search.value.trim(), shown: PAGE });
      render();
    }, 150);
  });
  sort.addEventListener("change", () => {
    Object.assign(state, { sort: sort.value, shown: PAGE });
    render();
  });
  more.querySelector("button").addEventListener("click", () => {
    state.shown += PAGE;
    render();
  });
  render();
}

function renderMega() {
  document.querySelectorAll("[data-mega]").forEach((mega) => {
    const lines = CATEGORIES.map((c) => {
      const items = inCategory(c.id);
      if (!items.length) return "";
      const price = SITE.showPrices ? tf("catalog.from", "From {price}", { price: fromPrice(items) }) : "";
      return `<a class="mega-line" href="products.html?cat=${c.id}">
        <img src="${escapeHtml(items[0].images[0])}" alt="" loading="lazy" referrerpolicy="no-referrer">
        <span><b>${escapeHtml(catName(c))}</b><small>${escapeHtml(tf("catalog.models", "{n} models", { n: items.length }))}${price ? ` · ${escapeHtml(price)}` : ""}</small></span>
      </a>`;
    }).join("");
    mega.innerHTML = `<div class="mega-lines">${lines}</div><a class="mega-all" href="products.html">${escapeHtml(t("catalog.all", "All products"))}</a>`;
  });
}

async function boot() {
  const loading = `<p class="empty">${escapeHtml(t("catalog.loading", "Loading products…"))}</p>`;
  for (const sel of ["[data-featured]", "[data-grid]", "[data-product-root]"]) {
    const el = document.querySelector(sel);
    if (el) el.innerHTML = loading;
  }
  try {
    await loadCatalog();
  } catch {
    catalogError = t("catalog.unavailable", "Products are temporarily unavailable. Please try again, or contact us by email.");
  }
  renderMega();
  if (document.body.dataset.page === "home") renderHome();
  if (document.body.dataset.page === "catalog") renderCatalog();
  if (document.body.dataset.page === "product") await renderProduct();
  if (publicProfile) applyProfile(publicProfile);
}

if (document.body?.dataset?.page) boot();

let deepLink = "";
try { deepLink = decodeURIComponent(location.hash.match(/^#p=(.+)$/)?.[1] ?? ""); } catch { /* Ignore malformed links. */ }
if (deepLink && document.body.dataset.page !== "product" && typeof window.open === "function") {
  window.open(`product.html?id=${encodeURIComponent(deepLink)}`, "_blank", "noopener,noreferrer");
}
