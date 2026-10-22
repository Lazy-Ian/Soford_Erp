// Contact details shown across the site. Empty optional fields are hidden (see data-site-optional).
// Do not ship placeholder phones/numbers — fill from docs/06-官网上线资料清单.md before go-live.
const SITE = {
  company: "Soford",
  email: "sales@soford.cn", // confirm with business before treating as canonical
  phone: "", // e.g. "+86 532 …"; empty hides phone links
  whatsapp: "", // digits only with country code, e.g. "8613800000000"; empty hides the button
  address: "Shandong, China",
  alibabaStore: "", // e.g. "https://soford.en.alibaba.com"; empty hides the link
  showPrices: true,
  // When false, homepage softens unverified SLA copy (24h reply etc.).
  claimsVerified: false,
  inquiryEndpoint: ["localhost", "127.0.0.1"].includes(location.hostname)
    ? "http://localhost:5153/public/inquiries" : "/public/inquiries",
};

/** Reject known placeholders so they never render as real contact details. */
function siteDisplayValue(key, value) {
  if (value == null) return "";
  const text = String(value).trim();
  if (!text) return "";
  if (key === "phone" && /(?:^|\s)0{3,}[\s-]*0{3,}/.test(text)) return "";
  if (key === "whatsapp" && /^0+$/.test(text)) return "";
  return text;
}

const CATEGORIES = [
  {
    id: "gym-tiles",
    name: "Rubber Gym Tiles",
    blurb: "High-density 15–50 mm tiles for free-weight zones, CrossFit boxes and commercial gyms.",
  },
  {
    id: "rubber-rolls",
    name: "Rubber Flooring Rolls",
    blurb: "Seamless 4–10 mm rolls in 1–1.25 m widths, cut to length for large fitness areas.",
  },
  {
    id: "outdoor-playground",
    name: "Outdoor & Playground",
    blurb: "UV-stable EPDM tiles, dog-bone pavers and safety surfaces for parks and play areas.",
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

const PRODUCTS = window.SOFORD_PRODUCTS ?? [];
const byId = new Map(PRODUCTS.map((p) => [p.id, p]));
const categoryOf = (id) => CATEGORIES.find((c) => c.id === id);
const inCategory = (id) => PRODUCTS.filter((p) => p.category === id);

const escapeHtml = (s) =>
  String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const unitLabel = (unit) => (unit === "Square Meter" ? "m²" : unit === "Piece" ? "pc" : unit);
const money = (n) => `$${n.toFixed(2).replace(/\.00$/, "")}`;
const priceText = (p) =>
  p.priceMin === p.priceMax ? money(p.priceMin) : `${money(p.priceMin)}–${money(p.priceMax)}`;
const spec = (p, label) => p.specs.find(([k]) => k === label)?.[1];

function cardHtml(p) {
  const cat = categoryOf(p.category);
  const thickness = spec(p, "Thickness");
  return `
    <button class="card" type="button" data-product="${escapeHtml(p.id)}">
      <div class="card-img"><img src="${escapeHtml(p.images[0])}" alt="" loading="lazy" referrerpolicy="no-referrer"></div>
      <div class="card-body">
        <span class="card-tag">${escapeHtml(cat.name)}${thickness ? ` · ${escapeHtml(thickness)}` : ""}</span>
        <h3>${escapeHtml(p.title)}</h3>
        <div class="card-meta">
          ${SITE.showPrices ? `<span class="price">${priceText(p)} <small>/ ${unitLabel(p.unit)}</small></span>` : ""}
          <span>MOQ ${p.moq} ${unitLabel(p.unit)}</span>
        </div>
      </div>
    </button>`;
}

/* ---------- Product dialog ---------- */

const dialog = document.createElement("dialog");
dialog.className = "product";
document.body.append(dialog);
dialog.addEventListener("click", (e) => {
  if (e.target === dialog) dialog.close();
});
dialog.addEventListener("close", () => {
  if (location.hash.startsWith("#p=")) history.replaceState(null, "", location.pathname + location.search);
});

function openProduct(id) {
  const p = byId.get(id);
  if (!p) return;
  const cat = categoryOf(p.category);
  const rows = p.specs.map(([k, v]) => `<tr><th>${escapeHtml(k)}</th><td>${escapeHtml(v)}</td></tr>`).join("");
  dialog.innerHTML = `
    <button class="pd-close" type="button" aria-label="Close">×</button>
    <div class="pd">
      <div class="pd-gallery">
        <div class="pd-main"><img src="${escapeHtml(p.images[0])}" alt="${escapeHtml(p.title)}" referrerpolicy="no-referrer"></div>
        <div class="pd-thumbs">
          ${p.images
            .map(
              (src, i) =>
                `<button type="button" data-src="${escapeHtml(src)}" aria-current="${i === 0}"><img src="${escapeHtml(src)}" alt="" referrerpolicy="no-referrer"></button>`,
            )
            .join("")}
        </div>
      </div>
      <div class="pd-info">
        <span class="eyebrow">${escapeHtml(cat.name)}</span>
        <h2>${escapeHtml(p.title)}</h2>
        <div class="pd-buy">
          ${SITE.showPrices ? `<div><small>FOB price</small><b>${priceText(p)}</b><small>per ${unitLabel(p.unit)}</small></div>` : ""}
          <div><small>Minimum order</small><b>${p.moq}</b><small>${unitLabel(p.unit)}</small></div>
          <div><small>Lead time</small><b>${escapeHtml((spec(p, "Delivery Time") ?? "7-20 Days").replace(/\s*days?/i, "").replace("-", "–"))}</b><small>days</small></div>
        </div>
        ${rows ? `<table class="specs"><tbody>${rows}</tbody></table>` : ""}
        ${p.keywords.length ? `<div class="chips">${p.keywords.map((k) => `<span class="chip">${escapeHtml(k)}</span>`).join("")}</div>` : ""}
        <div class="pd-actions">
          <a class="btn btn-primary" href="index.html?product=${encodeURIComponent(p.id)}#contact">Request a quote</a>
          <a class="btn btn-ghost" href="index.html?product=${encodeURIComponent(p.id)}&sample=1#contact">Ask for a sample</a>
        </div>
      </div>
    </div>`;
  dialog.querySelector(".pd-close").addEventListener("click", () => dialog.close());
  const main = dialog.querySelector(".pd-main img");
  dialog.querySelectorAll(".pd-thumbs button").forEach((btn) =>
    btn.addEventListener("click", () => {
      main.src = btn.dataset.src;
      dialog.querySelectorAll(".pd-thumbs button").forEach((b) => b.setAttribute("aria-current", b === btn));
    }),
  );
  history.replaceState(null, "", `#p=${encodeURIComponent(id)}`);
  if (!dialog.open) dialog.showModal();
}

document.addEventListener("click", (e) => {
  const card = e.target.closest("[data-product]");
  if (card) openProduct(card.dataset.product);
});

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

document.querySelectorAll("[data-site]").forEach((el) => {
  const key = el.dataset.site;
  const value = siteDisplayValue(key, SITE[key]);
  if (!value) {
    (el.closest("[data-site-optional]") ?? el.closest("li") ?? el).remove();
    return;
  }
  if (key === "email") el.href = `mailto:${value}`;
  if (key === "phone") el.href = `tel:${value.replace(/[^\d+]/g, "")}`;
  if (key === "whatsapp") el.href = `https://wa.me/${value}`;
  if (key === "alibabaStore") el.href = value;
  if (!el.dataset.keepText) el.textContent = key === "whatsapp" ? `+${value}` : value;
});
document.querySelectorAll("[data-year]").forEach((el) => (el.textContent = new Date().getFullYear()));
document.querySelectorAll("[data-footer-cats]").forEach((ul) => {
  ul.innerHTML = CATEGORIES.map((c) => `<li><a href="products.html?cat=${c.id}">${c.name}</a></li>`).join("");
});
if (!SITE.claimsVerified) {
  document.querySelectorAll("[data-claim='reply-sla']").forEach((el) => {
    el.textContent = "Get a quote — we reply as soon as we can.";
  });
}

/* ---------- Home page ---------- */

function renderHome() {
  // Lowest price in the category's main selling unit, so a few per-piece listings don't skew it.
  const fromPrice = (items) => {
    const counts = {};
    items.forEach((p) => (counts[p.unit] = (counts[p.unit] ?? 0) + 1));
    const unit = Object.keys(counts).sort((a, b) => counts[b] - counts[a])[0];
    const min = Math.min(...items.filter((p) => p.unit === unit).map((p) => p.priceMin));
    return `${money(min)} / ${unitLabel(unit)}`;
  };

  const stats = {
    products: `${Math.floor(PRODUCTS.length / 50) * 50}+`,
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

  document.querySelector("[data-cats]").innerHTML = CATEGORIES.map((c) => {
    const items = inCategory(c.id);
    if (!items.length) return "";
    return `
      <a class="cat" href="products.html?cat=${c.id}">
        <img src="${escapeHtml(items[0].images[0])}" alt="" loading="lazy" referrerpolicy="no-referrer">
        <span class="count">${items.length} models</span>
        <h3>${c.name}</h3>
        <p>${c.blurb}</p>
        <span class="from">${SITE.showPrices ? `From ${fromPrice(items)}` : "View range"}</span>
      </a>`;
  }).join("");

  const featuredPlan = { "gym-tiles": 2, "rubber-rolls": 2, "outdoor-playground": 2, "artificial-grass": 1, "eva-mats": 1 };
  const featured = Object.entries(featuredPlan).flatMap(([id, n]) => inCategory(id).slice(0, n));
  document.querySelector("[data-featured]").innerHTML = featured.map(cardHtml).join("");

  // Inquiry form, optionally prefilled from a product dialog link.
  const form = document.querySelector("#inquiry");
  const params = new URLSearchParams(location.search);
  const product = byId.get(params.get("product"));
  const productSelect = form.elements.product;
  productSelect.innerHTML =
    `<option value="">General inquiry</option>` +
    CATEGORIES.map((c) => `<option>${c.name}</option>`).join("") +
    (product ? `<option selected>${escapeHtml(product.title)}</option>` : "");
  if (product) {
    form.elements.message.value = params.get("sample")
      ? `Hello, I would like to request a sample of "${product.title}". Please let me know the cost and shipping time.`
      : `Hello, I am interested in "${product.title}". Please quote for ___ ${unitLabel(product.unit)}, delivered to ___.`;
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
    button.textContent = "Sending…";
    form.setAttribute("aria-busy", "true");
    note.textContent = "Sending your inquiry…";
    const f = form.elements;
    const body = Object.fromEntries(["name", "company", "email", "country", "product", "quantity", "message", "website"]
      .map((key) => [key, f[key].value.trim()]));
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
          ? "Too many inquiries. Please try again later, or contact us by email."
          : problem.detail || problem.title || "Unable to send. Please try again, or contact us by email.");
      }
      const result = await response.json();
      if (result.received !== true) throw new Error("Unable to confirm delivery. Please contact us by email.");
      form.reset();
      note.textContent = "Thank you! Your inquiry has been received. Our team will contact you by email.";
    } catch (error) {
      note.textContent = error.name === "TimeoutError"
        ? "Delivery could not be confirmed. Please contact us by email before sending again."
        : error instanceof TypeError ? "Unable to connect. Please check your connection or contact us by email." : error.message;
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
  const state = {
    cat: categoryOf(params.get("cat")) ? params.get("cat") : "all",
    q: params.get("q") ?? "",
    sort: "featured",
    shown: PAGE,
  };

  const list = document.querySelector("[data-cat-filter]");
  const grid = document.querySelector("[data-grid]");
  const count = document.querySelector("[data-count]");
  const more = document.querySelector("[data-more]");
  const search = document.querySelector("[data-search]");
  const sort = document.querySelector("[data-sort]");
  const heading = document.querySelector("[data-heading]");
  const sub = document.querySelector("[data-sub]");
  search.value = state.q;

  list.innerHTML = [{ id: "all", name: "All products" }, ...CATEGORIES]
    .map((c) => {
      const n = c.id === "all" ? PRODUCTS.length : inCategory(c.id).length;
      return `<button type="button" data-cat="${c.id}">${c.name}<span>${n}</span></button>`;
    })
    .join("");

  function results() {
    const terms = state.q.toLowerCase().split(/\s+/).filter(Boolean);
    let items = PRODUCTS.filter((p) => state.cat === "all" || p.category === state.cat);
    if (terms.length) {
      items = items.filter((p) => {
        const hay = `${p.title} ${p.keywords.join(" ")} ${p.specs.map((s) => s[1]).join(" ")}`.toLowerCase();
        return terms.every((t) => hay.includes(t));
      });
    }
    if (state.sort === "price-asc") items = [...items].sort((a, b) => a.priceMin - b.priceMin);
    if (state.sort === "price-desc") items = [...items].sort((a, b) => b.priceMin - a.priceMin);
    if (state.sort === "moq") items = [...items].sort((a, b) => a.moq - b.moq);
    return items;
  }

  function render() {
    const items = results();
    const cat = categoryOf(state.cat);
    heading.textContent = cat ? cat.name : "All products";
    sub.textContent = cat
      ? cat.blurb
      : "Rubber flooring, sports surfaces, artificial turf and EVA mats — factory direct from Shandong, China.";
    list.querySelectorAll("button").forEach((b) => b.setAttribute("aria-pressed", b.dataset.cat === state.cat));
    count.textContent = `${items.length} product${items.length === 1 ? "" : "s"}`;
    grid.innerHTML = items.length
      ? items.slice(0, state.shown).map(cardHtml).join("")
      : `<div class="empty">No products match “${escapeHtml(state.q)}”. Try a different keyword or <a href="index.html#contact">ask us directly</a>.</div>`;
    more.hidden = items.length <= state.shown;

    const url = new URL(location.href);
    url.searchParams.delete("cat");
    url.searchParams.delete("q");
    if (state.cat !== "all") url.searchParams.set("cat", state.cat);
    if (state.q) url.searchParams.set("q", state.q);
    history.replaceState(null, "", url);
  }

  list.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-cat]");
    if (!btn) return;
    Object.assign(state, { cat: btn.dataset.cat, shown: PAGE });
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

if (document.body.dataset.page === "home") renderHome();
if (document.body.dataset.page === "catalog") renderCatalog();

let deepLink = "";
try { deepLink = decodeURIComponent(location.hash.match(/^#p=(.+)$/)?.[1] ?? ""); } catch { /* Ignore malformed links. */ }
if (deepLink) openProduct(deepLink);
