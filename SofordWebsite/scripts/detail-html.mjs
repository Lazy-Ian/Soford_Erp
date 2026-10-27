// Turns an Alibaba listing description into HTML that can be embedded on the public site.

const DROPPED = "script|style|iframe|object|embed|link|meta|form|input|button|textarea|select|svg|video|audio|noscript";

export function sanitizeDetail(html) {
  let source = String(html ?? "").replace(/<!--[\s\S]*?-->/g, "");
  const block = new RegExp(`<(?:${DROPPED})\\b[^>]*>[\\s\\S]*?<\\/(?:${DROPPED})>`, "gi");
  let previous;
  do {
    previous = source;
    source = source.replace(block, "");
  } while (source !== previous);
  source = source.replace(new RegExp(`<(?:${DROPPED})\\b[^>]*\\/?>`, "gi"), "");
  source = source.replace(/\s+on[a-z]+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)/gi, "");
  source = source.replace(/\s+(?:href|src)\s*=\s*("|\')\s*(?:javascript:|data:)[^"']*\1/gi, "");
  source = source.replace(/(\s(?:src|href)\s*=\s*["'])\/\//gi, "$1https://");
  return source.trim();
}

export function detailScript(html) {
  const json = JSON.stringify(sanitizeDetail(html))
    .replace(/</g, "\\u003c")
    .replace(/\u2028/g, "\\u2028")
    .replace(/\u2029/g, "\\u2029");
  return `window.SOFORD_DETAIL = ${json};\n`;
}
