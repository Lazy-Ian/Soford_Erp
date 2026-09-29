# Soford ERP Ubuntu Deployment Without Docker

Target server:

- Ubuntu 22.04 x64
- Public IP: `39.106.188.160`
- Recommended domain: `erp.soford.cn`

## 1. Build Package On Windows

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-server.ps1
```

This creates:

```text
soford-erp-server.zip
```

Upload the zip to the server.

## 2. Install Server Runtime

SSH into the server, then install Nginx, unzip, Certbot, and the ASP.NET Core 8 runtime.

```bash
sudo apt update
sudo apt install -y nginx unzip certbot python3-certbot-nginx
sudo apt install -y dotnet-runtime-8.0 aspnetcore-runtime-8.0
```

If the dotnet packages are unavailable, install Microsoft's Ubuntu package feed first, then repeat the runtime install.

## 3. Create Directories

```bash
sudo mkdir -p /opt/soford-erp/api /opt/soford-erp/web /opt/soford-erp/app_data /opt/soford-erp/env
sudo chown -R www-data:www-data /opt/soford-erp
```

## 4. Extract Release

Assuming the zip is in your home directory:

```bash
mkdir -p ~/soford-release
unzip -o ~/soford-erp-server.zip -d ~/soford-release
sudo rsync -a --delete ~/soford-release/api/ /opt/soford-erp/api/
sudo rsync -a --delete ~/soford-release/web/ /opt/soford-erp/web/
sudo chown -R www-data:www-data /opt/soford-erp
```

## 5. Configure Secrets

```bash
sudo cp ~/soford-release/deploy/env/soford-api.env.example /opt/soford-erp/env/soford-api.env
sudo nano /opt/soford-erp/env/soford-api.env
sudo chmod 600 /opt/soford-erp/env/soford-api.env
sudo chown root:www-data /opt/soford-erp/env/soford-api.env
```

Set at least:

```text
Auth__AdminPassword
Alibaba__AppKey
Alibaba__AppSecret
```

Keep `Soford__DataPath=/opt/soford-erp/app_data`.

## 6. Configure API Service

```bash
sudo cp ~/soford-release/deploy/systemd/soford-erp-api.service /etc/systemd/system/soford-erp-api.service
sudo systemctl daemon-reload
sudo systemctl enable --now soford-erp-api
sudo systemctl status soford-erp-api
```

Check logs:

```bash
sudo journalctl -u soford-erp-api -f
```

## 7. Configure Nginx

```bash
sudo cp ~/soford-release/deploy/nginx/soford-erp.conf /etc/nginx/sites-available/soford-erp.conf
sudo ln -sf /etc/nginx/sites-available/soford-erp.conf /etc/nginx/sites-enabled/soford-erp.conf
sudo nginx -t
sudo systemctl reload nginx
```

Before HTTPS, make sure DNS `erp.soford.cn` points to `39.106.188.160`.

## 8. Enable HTTPS

```bash
sudo certbot --nginx -d erp.soford.cn
```

## 9. Verify

```bash
curl http://127.0.0.1:5153/api/health
curl -I https://erp.soford.cn
```

Open:

```text
https://erp.soford.cn
```

Log in with `Auth__AdminUsername` and `Auth__AdminPassword`.

## 10. Update Deployment

For each new version:

```bash
unzip -o ~/soford-erp-server.zip -d ~/soford-release
sudo systemctl stop soford-erp-api
sudo rsync -a --delete ~/soford-release/api/ /opt/soford-erp/api/
sudo rsync -a --delete ~/soford-release/web/ /opt/soford-erp/web/
sudo chown -R www-data:www-data /opt/soford-erp/api /opt/soford-erp/web
sudo systemctl start soford-erp-api
sudo systemctl reload nginx
```

Do not delete `/opt/soford-erp/app_data`; it contains products, tokens, logs, and exports.
