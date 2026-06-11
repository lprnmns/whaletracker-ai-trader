# WhaleTracker Ubuntu Deployment

Recommended public URL: `whalecopy.alperenmanas.app` (subdomain is the simplest setup).

## 1) Upload the project to the server

Example (from your local machine):
```
scp -r C:\Users\manas\Desktop\proje_1_final\WhaleTracker root@138.197.6.242:/opt/whaletracker
```

Ensure the repo lives at:
```
/opt/whaletracker/WhaleTracker
```

## 2) Install dependencies

Run on the server:
```
chmod +x /opt/whaletracker/WhaleTracker/deploy/ubuntu_setup.sh
/opt/whaletracker/WhaleTracker/deploy/ubuntu_setup.sh
```

## 3) Create environment file

```
cat <<EOF >/etc/whaletracker.env
WATCH_ADDRESS=0xc82b2e484b161d20eae386877d57c4e5807b5581
ZERION_API_KEY=YOUR_ZERION_KEY
EOF
```

## 4) Install systemd services

```
cp /opt/whaletracker/WhaleTracker/deploy/whaletracker-*.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable whaletracker-api whaletracker-market whaletracker-watcher
systemctl start whaletracker-api whaletracker-market whaletracker-watcher
systemctl status whaletracker-api whaletracker-market whaletracker-watcher
```

## 5) Nginx reverse proxy

```
cp /opt/whaletracker/WhaleTracker/deploy/nginx-whalecopy.conf /etc/nginx/sites-available/whalecopy.conf
ln -s /etc/nginx/sites-available/whalecopy.conf /etc/nginx/sites-enabled/whalecopy.conf
nginx -t
systemctl reload nginx
```

## 6) Cloudflare DNS

Create DNS record:
- **Type:** A
- **Name:** whalecopy
- **Target:** 138.197.6.242
- **Proxy:** DNS only (grey) for SSL issuance

After DNS propagates:

```
apt-get install -y certbot python3-certbot-nginx
certbot --nginx -d whalecopy.alperenmanas.app
```

You can enable Cloudflare proxy after SSL is issued.

## 7) Optional: Path-based URL

If you want `alperenmanas.app/whalecopy`, use a Cloudflare Worker or Vercel rewrite.
The subdomain approach is the simplest and most reliable.
