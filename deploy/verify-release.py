"""Read-only deployment assertions, plus a honeypot POST that never creates an inquiry."""
import json
import re
import sys
import urllib.request

revision = sys.argv[1]
assert re.fullmatch(r"[a-f0-9]{40}", revision)
base = "https://stepnex.cn"


def read(path, payload=None):
    request = urllib.request.Request(
        base + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Origin": base, "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        assert response.status == 200, (path, response.status)
        return response.read()


for path in ("/erp/", "/soford/", "/soford/products.html", "/soford/product.html"):
    assert b"<!doctype html>" in read(path).lower(), path
for path in ("/erp/release.json", "/soford/release.json"):
    assert json.loads(read(path))["Release"]["Revision"] == revision, path
health = json.loads(read("/erp/api/health"))
assert health["status"] == "ok" and health["revision"] == revision
products = json.loads(read("/soford/public/products"))
assert isinstance(products, list) and products, "Expected the existing catalog"
assert all("currency" in product and "leadTimeDays" in product for product in products)
detail = json.loads(read("/soford/public/products/" + products[0]["id"]))
assert isinstance(detail["description"], str)
profile = json.loads(read("/soford/public/site"))
assert isinstance(profile["contacts"], list) and isinstance(profile["revision"], int)
assert json.loads(read("/soford/public/inquiries", {"website": "deployment-honeypot"}))["received"] is True
print(json.dumps({"revision": revision, "products": len(products), "contacts": len(profile["contacts"]), "publicEndpoints": "passed"}))
