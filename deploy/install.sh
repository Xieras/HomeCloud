#!/usr/bin/env bash
# Установка HomeCloud на Debian 12. Запуск: sudo bash deploy/install.sh
set -euo pipefail
[ "$EUID" -eq 0 ] || { echo "Запустите от root: sudo bash deploy/install.sh"; exit 1; }

SRC="$(cd "$(dirname "$0")/.." && pwd)"
APP=/opt/homecloud

read -rp "Домен (например cloud.example.com), либо _ для доступа по IP: " DOMAIN
DOMAIN=${DOMAIN:-_}

echo "==> Пакеты"
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-venv nginx ufw fail2ban certbot python3-certbot-nginx rsync openssl

echo "==> Пользователь и каталоги"
id homecloud &>/dev/null || useradd --system --home-dir "$APP" --shell /usr/sbin/nologin homecloud
mkdir -p "$APP" /srv/storage /srv/tmp-uploads /var/lib/homecloud /var/log/homecloud
rsync -a --exclude deploy --exclude venv --exclude .env --exclude __pycache__ "$SRC"/ "$APP"/

if [ ! -f "$APP/.env" ]; then
  cp "$SRC/.env.example" "$APP/.env"
  sed -i "s|^SECRET_KEY=.*|SECRET_KEY=$(openssl rand -hex 32)|" "$APP/.env"
fi

echo "==> Python-окружение"
[ -d "$APP/venv" ] || python3 -m venv "$APP/venv"
"$APP/venv/bin/pip" install --quiet --upgrade pip
"$APP/venv/bin/pip" install --quiet -r "$APP/requirements.txt"

chown -R homecloud:homecloud "$APP" /srv/storage /srv/tmp-uploads /var/lib/homecloud /var/log/homecloud
chmod 600 "$APP/.env"

echo "==> Создание администратора (один раз)"
sudo -u homecloud bash -c "cd $APP && ./venv/bin/python -c 'import app'"
# Пароль больше не нужен в открытом виде — он уже хранится в БД в виде хеша
sed -i '/^ADMIN_PASSWORD=/d' "$APP/.env"

echo "==> systemd"
install -m 644 "$SRC/deploy/homecloud.service" /etc/systemd/system/homecloud.service
systemctl daemon-reload
systemctl enable --now homecloud
systemctl restart homecloud

echo "==> nginx"
install -m 644 "$SRC/deploy/homecloud-proxy.conf" /etc/nginx/homecloud-proxy.conf
sed "s/__DOMAIN__/$DOMAIN/" "$SRC/deploy/nginx.conf" > /etc/nginx/sites-available/homecloud
ln -sf /etc/nginx/sites-available/homecloud /etc/nginx/sites-enabled/homecloud
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl reload nginx

echo "==> fail2ban"
install -m 644 "$SRC/deploy/fail2ban-filter.conf" /etc/fail2ban/filter.d/homecloud.conf
install -m 644 "$SRC/deploy/fail2ban-jail.local" /etc/fail2ban/jail.d/homecloud.local
touch /var/log/homecloud/auth.log && chown homecloud:homecloud /var/log/homecloud/auth.log
systemctl enable --now fail2ban
systemctl restart fail2ban

echo "==> logrotate"
cat > /etc/logrotate.d/homecloud <<'ROT'
/var/log/homecloud/auth.log {
    weekly
    rotate 8
    compress
    missingok
    notifempty
    copytruncate
}
ROT

echo "==> Файрвол (SSH 22, HTTP, HTTPS)"
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable

cat <<MSG

Готово. Сайт: http://${DOMAIN/_/IP-сервера}
Дальше:
  1) Проброс портов 80 и 443 на роутере на IP этого сервера.
  2) HTTPS:  certbot --nginx -d $DOMAIN   (для домена)
  3) Если проверяете по http://IP без HTTPS — в $APP/.env поставьте COOKIE_SECURE=0
     и выполните: systemctl restart homecloud. После настройки HTTPS верните 1.
  4) Хранилище лежит в /srv/storage — можно смонтировать туда отдельный диск.
Если SSH у вас на нестандартном порту — откройте его в ufw вручную: ufw allow ПОРТ/tcp
MSG
