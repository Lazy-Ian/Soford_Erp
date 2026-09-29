#!/usr/bin/env bash
set -euo pipefail

DOMAIN="${1:-erp.soford.cn}"
APP_ROOT="/opt/soford-erp"
RELEASE_DIR="$HOME/soford-release"
ZIP_FILE="$HOME/soford-erp-server.zip"

echo "Installing Soford ERP for domain: ${DOMAIN}"

if [ ! -f "$ZIP_FILE" ]; then
  echo "Missing $ZIP_FILE"
  exit 1
fi

sudo apt update
sudo apt install -y nginx unzip rsync curl wget certbot python3-certbot-nginx

if ! command -v dotnet >/dev/null 2>&1; then
  wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
  sudo dpkg -i /tmp/packages-microsoft-prod.deb
  sudo apt update
fi

sudo apt install -y aspnetcore-runtime-8.0

rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"
set +e
unzip -o "$ZIP_FILE" -d "$RELEASE_DIR"
unzip_status=$?
set -e
if [ "$unzip_status" -gt 1 ]; then
  echo "unzip failed with status $unzip_status"
  exit "$unzip_status"
fi

sudo mkdir -p "$APP_ROOT/api" "$APP_ROOT/web" "$APP_ROOT/app_data" "$APP_ROOT/env"

sudo rsync -a --delete "$RELEASE_DIR/api/" "$APP_ROOT/api/"
sudo rsync -a --delete "$RELEASE_DIR/web/" "$APP_ROOT/web/"

if [ ! -f "$APP_ROOT/env/soford-api.env" ]; then
  sudo cp "$RELEASE_DIR/deploy/env/soford-api.env.example" "$APP_ROOT/env/soford-api.env"
  sudo sed -i "s/erp.soford.cn/${DOMAIN}/g" "$APP_ROOT/env/soford-api.env"
  echo ""
  echo "Created $APP_ROOT/env/soford-api.env"
  echo "You must edit Auth__AdminPassword, Alibaba__AppKey, and Alibaba__AppSecret after this script finishes."
else
  echo "Keeping existing $APP_ROOT/env/soford-api.env"
fi

sudo cp "$RELEASE_DIR/deploy/systemd/soford-erp-api.service" /etc/systemd/system/soford-erp-api.service

tmp_nginx="/tmp/soford-erp.conf"
cp "$RELEASE_DIR/deploy/nginx/soford-erp.conf" "$tmp_nginx"
sed -i "s/erp.soford.cn/${DOMAIN}/g" "$tmp_nginx"
sudo cp "$tmp_nginx" /etc/nginx/sites-available/soford-erp.conf
sudo ln -sf /etc/nginx/sites-available/soford-erp.conf /etc/nginx/sites-enabled/soford-erp.conf

sudo chown -R www-data:www-data "$APP_ROOT/api" "$APP_ROOT/web" "$APP_ROOT/app_data"
sudo chown root:www-data "$APP_ROOT/env/soford-api.env"
sudo chmod 640 "$APP_ROOT/env/soford-api.env"

sudo systemctl daemon-reload
sudo systemctl enable soford-erp-api
sudo systemctl restart soford-erp-api

sudo nginx -t
sudo systemctl reload nginx

echo ""
echo "Soford ERP files are deployed."
echo ""
echo "Next required step:"
echo "  sudo nano $APP_ROOT/env/soford-api.env"
echo "  sudo systemctl restart soford-erp-api"
echo ""
echo "After DNS points ${DOMAIN} to this server, enable HTTPS:"
echo "  sudo certbot --nginx -d ${DOMAIN}"
echo ""
echo "Check status:"
echo "  sudo systemctl status soford-erp-api"
echo "  curl http://127.0.0.1:5153/api/health"
