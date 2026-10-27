#!/usr/bin/env bash
# Upgrade the existing /erp/ + /soford/ installation without changing unrelated virtual hosts.
# Usage: bash upgrade-stepnex.sh <40-character commit>
set -euo pipefail
revision="${1:?release commit required}"
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid revision' >&2; exit 1; }
root=/opt/soford-erp
website=/opt/soford-website
release="$root/releases/$revision"
archive="$HOME/soford-erp-server.zip"
snapshot="$root/backups/release-$(date +%Y%m%d-%H%M%S)-$revision"
test -f "$archive"
test -d "$root/api"
test -d "$website"
mkdir -p "$release" "$snapshot"
chmod 700 "$root/releases" "$release" "$snapshot"
unzip -oq "$archive" -d "$release"
python3 - "$release" "$revision" <<'PY'
import json, pathlib, sys
root, revision = pathlib.Path(sys.argv[1]), sys.argv[2]
for area in ('api', 'web', 'website'):
    assert json.loads((root / area / 'release.json').read_text())['Release']['Revision'] == revision
assert (root / 'api' / 'SofordRepApi.dll').is_file()
assert (root / 'web' / 'index.html').is_file()
assert (root / 'website' / 'product.html').is_file()
PY
nginx -t
tar -czf "$snapshot/programs.tar.gz" -C /opt soford-erp/api soford-erp/web soford-website
cp /etc/nginx/snippets/stepnex-soford-website.locations.conf "$snapshot/website.locations.conf"
rollback() {
    echo 'Upgrade failed; restoring previous program files and nginx snippet.' >&2
    systemctl stop soford-erp-api || true
    mkdir -p "$snapshot/previous"
    tar -xzf "$snapshot/programs.tar.gz" -C "$snapshot/previous"
    rsync -a --delete "$snapshot/previous/soford-erp/api/" "$root/api/"
    rsync -a --delete "$snapshot/previous/soford-erp/web/" "$root/web/"
    rsync -a --delete "$snapshot/previous/soford-website/" "$website/"
    cp "$snapshot/website.locations.conf" /etc/nginx/snippets/stepnex-soford-website.locations.conf
    nginx -t && systemctl reload nginx
    systemctl start soford-erp-api
}
trap 'rollback' ERR
systemctl stop soford-erp-api
# Back up quiet business data and configuration before replacing any executable.
tar --exclude='*.tmp' -czf "$snapshot/data.tar.gz" -C "$root" app_data env
chmod 600 "$snapshot"/*.tar.gz
tar -tzf "$snapshot/data.tar.gz" >/dev/null
rsync -a --delete "$release/api/" "$root/api/"
rsync -a --delete "$release/web/" "$root/web/"
rsync -a --delete "$release/website/" "$website/"
chown -R root:root "$root/api" "$root/web" "$website"
chmod -R u=rwX,go=rX "$root/api" "$root/web" "$website"
install -m 644 "$release/deploy/nginx/snippets/stepnex-soford-website.locations.conf" /etc/nginx/snippets/stepnex-soford-website.locations.conf
nginx -t
systemctl reload nginx
systemctl start soford-erp-api
healthy=0
for _ in $(seq 1 30); do
    if curl -fsS -H 'Host: stepnex.cn' http://127.0.0.1:5153/api/health >/dev/null 2>&1; then healthy=1; break; fi
    sleep 1
done
test "$healthy" = 1
python3 "$release/deploy/verify-release.py" "$revision"
trap - ERR
echo "Deployed revision=$revision"
echo "Rollback snapshot=$snapshot"
