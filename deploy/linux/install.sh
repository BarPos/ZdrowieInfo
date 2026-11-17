#!/usr/bin/env bash
# Instalacja lub aktualizacja ZdrowieInfo na Ubuntu 24.04 (x64). Uruchamiać jako root z rozpakowanej paczki:
#   sudo ./install.sh --public-url https://zdrowieinfo.example.pl     (pierwsza instalacja)
#   sudo ./install.sh                                                (aktualizacja)
set -euo pipefail

APP_DIR=/opt/zdrowieinfo
CONF_DIR=/etc/zdrowieinfo
DATA_DIR=/var/lib/zdrowieinfo
ENV_FILE=$CONF_DIR/zdrowieinfo.env
SERVICE=zdrowieinfo
USER_NAME=zdrowieinfo
HERE="$(cd "$(dirname "$0")" && pwd)"
PUBLIC_URL=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --public-url) PUBLIC_URL="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
        *) echo "Nieznany argument: $1" >&2; exit 2 ;;
    esac
done

[[ $EUID -eq 0 ]] || { echo "Uruchom jako root (sudo)." >&2; exit 1; }
[[ -x "$HERE/app/ZdrowieInfo.Server" ]] || { echo "Brak $HERE/app/ZdrowieInfo.Server – uruchom z rozpakowanej paczki." >&2; exit 1; }

FIRST_INSTALL=false
[[ -f "$ENV_FILE" ]] || FIRST_INSTALL=true

if $FIRST_INSTALL; then
    [[ "$PUBLIC_URL" =~ ^https://[^/]+/?$ ]] || { echo "Pierwsza instalacja wymaga --public-url https://<domena>" >&2; exit 2; }
    PUBLIC_URL="${PUBLIC_URL%/}"

    echo "== Pakiety systemowe"
    apt-get update -qq
    apt-get install -y -qq postgresql libicu74 tzdata >/dev/null

    echo "== Użytkownik i katalogi"
    id "$USER_NAME" &>/dev/null || useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$USER_NAME"
    install -d -o root -g root -m 755 "$APP_DIR"
    install -d -o root -g root -m 700 "$CONF_DIR"
    install -d -o "$USER_NAME" -g "$USER_NAME" -m 700 "$DATA_DIR" "$DATA_DIR/keys" "$DATA_DIR/backups"

    echo "== Baza danych"
    DB_PASSWORD="$(openssl rand -hex 24)"
    sudo -u postgres psql -v ON_ERROR_STOP=1 -q <<SQL
DO \$\$ BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$USER_NAME') THEN
    CREATE ROLE $USER_NAME LOGIN PASSWORD '$DB_PASSWORD';
  ELSE
    ALTER ROLE $USER_NAME PASSWORD '$DB_PASSWORD';
  END IF;
END \$\$;
SQL
    sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='zdrowieinfo'" | grep -q 1 \
        || sudo -u postgres createdb -O "$USER_NAME" zdrowieinfo

    sed -e "s|^ZdrowieInfo__PublicUrl=.*|ZdrowieInfo__PublicUrl=$PUBLIC_URL|" \
        -e "s|Password=ZMIEN|Password=$DB_PASSWORD|" \
        "$HERE/zdrowieinfo.env.example" > "$ENV_FILE"
    chmod 600 "$ENV_FILE"
fi

# Rozszerzenie do wyszukiwania (etap 6). Migracja tworzy je też sama (unaccent jest „trusted” od PostgreSQL 13, a usługa
# jest właścicielem bazy), ale jako postgres działa niezależnie od uprawnień roli.
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d zdrowieinfo -c "CREATE EXTENSION IF NOT EXISTS unaccent"

# Przy każdej instalacji i aktualizacji (apt pomija już zainstalowane): TeX do raportów PDF – kilkaset MB przy pierwszym razie.
echo "== TeX do raportów PDF (XeLaTeX, polski, czcionki TeX Gyre)"
apt-get install -y -qq --no-install-recommends texlive-xetex texlive-lang-polish texlive-latex-extra texlive-fonts-recommended fonts-texgyre latexmk >/dev/null

echo "== Aplikacja"
systemctl stop "$SERVICE" 2>/dev/null || true
find "$APP_DIR" -mindepth 1 -delete
cp -a "$HERE/app/." "$APP_DIR/"
chown -R root:root "$APP_DIR"
chmod 755 "$APP_DIR/ZdrowieInfo.Server"

echo "== Migracje bazy"
run_as_service() {
    # Ta sama konfiguracja co usługa (EnvironmentFile), uruchomione jako jej użytkownik.
    sudo -u "$USER_NAME" env $(grep -v '^\s*#' "$ENV_FILE" | xargs) ASPNETCORE_ENVIRONMENT=Production \
        "$APP_DIR/ZdrowieInfo.Server" "$@"
}
(cd "$APP_DIR" && run_as_service --migrate-only)

echo "== Usługa systemd i kopie zapasowe"
install -m 644 "$HERE/zdrowieinfo.service" /etc/systemd/system/zdrowieinfo.service
install -m 755 "$HERE/backup.sh" /usr/local/sbin/zdrowieinfo-backup
install -m 644 "$HERE/zdrowieinfo-backup.service" "$HERE/zdrowieinfo-backup.timer" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now "$SERVICE" zdrowieinfo-backup.timer

if $FIRST_INSTALL; then
    echo
    echo "== Konto użytkownika"
    read -rp "Login: " LOGIN
    (cd "$APP_DIR" && run_as_service --create-user "$LOGIN")
fi

echo
echo "Gotowe. Usługa nasłuchuje lokalnie (zob. ASPNETCORE_URLS w $ENV_FILE)."
echo "Skieruj reverse proxy z HTTPS na ten adres, przekazując nagłówki Host, X-Forwarded-For i X-Forwarded-Proto."
echo "Log: journalctl -u $SERVICE -f"
