#!/usr/bin/env bash
# Run on the server after files are uploaded to ~/SofordWebsite.upload and ~/snippets.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a

sudo mkdir -p /opt/soford-website /etc/nginx/snippets
sudo rsync -a --delete ~/SofordWebsite.upload/ /opt/soford-website/
sudo rm -rf ~/SofordWebsite.upload
sudo install -m 644 ~/stepnex-soford-website.locations.conf /etc/nginx/snippets/stepnex-soford-website.locations.conf
rm -f ~/stepnex-soford-website.locations.conf
sudo python3 ~/patch-stepnex-website-nginx.py
rm -f ~/patch-stepnex-website-nginx.py

ENV=/opt/soford-erp/env/soford-api.env
if ! sudo grep -q '^Website__AllowedOrigins__0=' "$ENV"; then
  printf '%s\n' \
    'Website__AllowedOrigins__0=https://stepnex.cn' \
    'Website__AllowedOrigins__1=https://www.stepnex.cn' \
    'Website__InquiriesPerHourPerIp=10' | sudo tee -a "$ENV" >/dev/null
  echo "Added Website__AllowedOrigins to env"
fi
if ! sudo grep -q 'www.stepnex.cn' "$ENV"; then
  sudo sed -i 's/^AllowedHosts=\(.*\)$/AllowedHosts=\1;www.stepnex.cn/' "$ENV" || true
fi

sudo nginx -t
sudo systemctl reload nginx
sudo systemctl restart soford-erp-api
sleep 2
curl -sS -o /dev/null -w "website_index=%{http_code}\n" -H "Host: stepnex.cn" --resolve stepnex.cn:443:127.0.0.1 https://stepnex.cn/soford/ || true
curl -sS -o /dev/null -w "inquiry_post=%{http_code}\n" -X POST -H "Host: stepnex.cn" -H "Origin: https://stepnex.cn" \
  -H "Content-Type: application/json" \
  --resolve stepnex.cn:443:127.0.0.1 \
  -d '{"name":"Deploy Smoke","email":"smoke@example.com","message":"Deployment smoke test inquiry.","website":""}' \
  https://stepnex.cn/soford/public/inquiries || true
