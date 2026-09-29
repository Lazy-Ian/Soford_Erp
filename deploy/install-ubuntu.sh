#!/usr/bin/env bash
# Installs or upgrades Soford ERP on Ubuntu 22.04. Safe to re-run for every release.
#   bash install-soford-erp.sh [domain] [letsencrypt-email]
# With an email and no certificate yet, a Let's Encrypt certificate is requested automatically.
set -euo pipefail

DOMAIN="${1:-erp.soford.cn}"
EMAIL="${2:-}"
APP_ROOT="/opt/soford-erp"
RELEASE_DIR="$HOME/soford-release"
ZIP_FILE="$HOME/soford-erp-server.zip"
ENV_FILE="$APP_ROOT/env/soford-api.env"
CERT_DIR="/etc/letsencrypt/live/$DOMAIN"

echo "Installing Soford ERP for domain: ${DOMAIN}"

if [ ! -f "$ZIP_FILE" ]; then
  echo "Missing $ZIP_FILE"
  exit 1
fi

sudo apt-get update -q
sudo apt-get install -y -q nginx unzip rsync curl wget certbot

if ! dpkg -s aspnetcore-runtime-8.0 >/dev/null 2>&1; then
  if ! apt-cache show aspnetcore-runtime-8.0 >/dev/null 2>&1; then
    wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    sudo dpkg -i /tmp/packages-microsoft-prod.deb
    sudo apt-get update -q
  fi
  sudo apt-get install -y -q aspnetcore-runtime-8.0
fi

rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"
set +e
unzip -o -q "$ZIP_FILE" -d "$RELEASE_DIR"
unzip_status=$?
set -e
if [ "$unzip_status" -gt 1 ]; then
  echo "unzip failed with status $unzip_status"
  exit "$unzip_status"
fi

sudo mkdir -p "$APP_ROOT/api" "$APP_ROOT/web" "$APP_ROOT/app_data" "$APP_ROOT/env" "$APP_ROOT/bin" "$APP_ROOT/backups"

# Stop the API first so the backup sees a quiet data directory, then back up before replacing binaries.
if systemctl is-active --quiet soford-erp-api; then
  sudo systemctl stop soford-erp-api
fi
sudo install -m 750 "$RELEASE_DIR/deploy/backup/soford-erp-backup.sh" "$APP_ROOT/bin/soford-erp-backup.sh"
if [ -n "$(sudo ls -A "$APP_ROOT/app_data" 2>/dev/null)" ]; then
  if ! sudo "$APP_ROOT/bin/soford-erp-backup.sh"; then
    echo "Backup failed; restarting the existing version and aborting the upgrade."
    sudo systemctl start soford-erp-api || true
    exit 1
  fi
fi
sudo rsync -a --delete "$RELEASE_DIR/api/" "$APP_ROOT/api/"
sudo rsync -a --delete "$RELEASE_DIR/web/" "$APP_ROOT/web/"

if [ ! -f "$ENV_FILE" ]; then
  sudo cp "$RELEASE_DIR/deploy/env/soford-api.env.example" "$ENV_FILE"
  sudo sed -i "s/erp.soford.cn/${DOMAIN}/g" "$ENV_FILE"
  echo "Created $ENV_FILE from the template."
else
  echo "Keeping existing $ENV_FILE"
fi

sudo chown -R www-data:www-data "$APP_ROOT/api" "$APP_ROOT/web" "$APP_ROOT/app_data"
sudo chown root:www-data "$ENV_FILE"
sudo chmod 640 "$ENV_FILE"

sudo cp "$RELEASE_DIR/deploy/systemd/soford-erp-api.service" /etc/systemd/system/soford-erp-api.service
sudo cp "$RELEASE_DIR/deploy/systemd/soford-erp-backup.service" /etc/systemd/system/soford-erp-backup.service
sudo cp "$RELEASE_DIR/deploy/systemd/soford-erp-backup.timer" /etc/systemd/system/soford-erp-backup.timer
sudo systemctl daemon-reload
sudo systemctl enable --now soford-erp-backup.timer

install_nginx_site() {
  local template="$1"
  local tmp="/tmp/soford-erp.conf"
  cp "$RELEASE_DIR/deploy/nginx/$template" "$tmp"
  sed -i "s/erp.soford.cn/${DOMAIN}/g" "$tmp"
  sudo cp "$tmp" /etc/nginx/sites-available/soford-erp.conf
  sudo ln -sf /etc/nginx/sites-available/soford-erp.conf /etc/nginx/sites-enabled/soford-erp.conf
  sudo mkdir -p /var/www/html
  sudo nginx -t
  sudo systemctl reload nginx
}

if sudo test -f "$CERT_DIR/fullchain.pem"; then
  install_nginx_site soford-erp.https.conf
else
  install_nginx_site soford-erp.http.conf
  if [ -n "$EMAIL" ]; then
    echo "Requesting Let's Encrypt certificate for $DOMAIN ..."
    if sudo certbot certonly --webroot -w /var/www/html -d "$DOMAIN" -m "$EMAIL" --agree-tos -n --deploy-hook "systemctl reload nginx"; then
      install_nginx_site soford-erp.https.conf
    else
      echo "Certificate request failed (is DNS for $DOMAIN pointing to this server?). Site stays on HTTP."
    fi
  fi
fi

# Refuse to start with template secrets; the API would reject them anyway and crash-loop.
needs_config=""
# Mirror the API's own rule (LocalAdminAuth): at least 10 characters and not a known placeholder.
admin_password="$(sudo sed -n 's/^Auth__AdminPassword=//p' "$ENV_FILE" | tail -n 1)"
case "${admin_password,,}" in
  change-this-password|change-this-before-running-outside-development|admin123|password|admin) weak_password=1 ;;
  *) weak_password=0 ;;
esac
if [ "${#admin_password}" -lt 10 ] || [ "$weak_password" -eq 1 ]; then needs_config="Auth__AdminPassword $needs_config"; fi
if sudo grep -Eq '^Alibaba__AppKey=$' "$ENV_FILE"; then needs_config="Alibaba__AppKey $needs_config"; fi
if sudo grep -Eq '^Alibaba__AppSecret=$' "$ENV_FILE"; then needs_config="Alibaba__AppSecret $needs_config"; fi

sudo systemctl enable soford-erp-api >/dev/null
echo ""
if echo "$needs_config" | grep -q "Auth__AdminPassword"; then
  echo "NOT STARTED: set a strong Auth__AdminPassword (10+ characters) first:"
  echo "  sudo nano $ENV_FILE"
  echo "  sudo systemctl restart soford-erp-api"
else
  sudo systemctl restart soford-erp-api
  for _ in $(seq 1 30); do
    if curl -fs http://127.0.0.1:5153/api/health >/dev/null; then break; fi
    sleep 1
  done
  if curl -fs http://127.0.0.1:5153/api/health >/dev/null; then
    echo "API is running."
  else
    echo "API did not become healthy. Check: sudo journalctl -u soford-erp-api -n 50"
    exit 1
  fi
  if [ -n "$needs_config" ]; then
    echo "Still to configure: $needs_config(edit $ENV_FILE, then sudo systemctl restart soford-erp-api)"
  fi
fi

echo ""
if sudo test -f "$CERT_DIR/fullchain.pem"; then
  echo "Open https://${DOMAIN}"
else
  echo "Open http://${DOMAIN}  (HTTPS not enabled yet; re-run with an email to request a certificate:"
  echo "  bash ~/install-soford-erp.sh ${DOMAIN} you@example.com)"
fi
echo "Backups: $APP_ROOT/backups (daily 03:30, newest 14 kept)"
