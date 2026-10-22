#!/usr/bin/env python3
"""Insert Soford website locations into the stepnex.cn blog nginx site (idempotent)."""
from pathlib import Path
import re
import sys

path = Path("/etc/nginx/sites-available/blog")
text = path.read_text()
if "stepnex-soford-website.locations.conf" in text:
    print("blog nginx already references website snippet")
    sys.exit(0)

inc = "    include /etc/nginx/snippets/stepnex-soford-website.locations.conf;\n\n"
ssl_pat = re.compile(
    r"(server \{\n    listen 443 ssl;\n    server_name stepnex\.cn www\.stepnex\.cn;.*?)"
    r"(    location / \{\n        proxy_pass http://127\.0\.0\.1:8000;)",
    re.S,
)
m = ssl_pat.search(text)
if not m:
    sys.exit("Could not find stepnex.cn SSL server insert point")
text = text[: m.start(2)] + inc + text[m.start(2) :]

http_pat = re.compile(
    r"(server \{\n    listen 80;\n    server_name stepnex\.cn www\.stepnex\.cn;.*?)"
    r"(    location /shanhai-api/ \{\n        return 308 https://stepnex\.cn\$request_uri;\n    }\n\n)",
    re.S,
)
hm = http_pat.search(text)
if not hm:
    sys.exit("Could not find stepnex.cn HTTP server insert point")
redirects = """    location = /soford {
        return 308 https://stepnex.cn/soford/;
    }

    location /soford/ {
        return 308 https://stepnex.cn$request_uri;
    }

"""
text = text[: hm.end(2)] + redirects + text[hm.end(2) :]
path.write_text(text)
print("blog nginx updated")
