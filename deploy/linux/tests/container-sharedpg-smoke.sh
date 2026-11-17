#!/usr/bin/env bash
# TYLKO DO TESTÓW. install.sh --db=local na serwerze, którego PostgreSQL obsługuje już inną aplikację – w jednorazowym
# kontenerze ubuntu:24.04 (produkcja nie używa Dockera). systemctl to atrapa jak w container-smoke.sh.
#
# Przed instalacją serwer ma: PostgreSQL 16 (klaster „main” na 5432 i drugi na 5433), bazę innej aplikacji „otherapp”
# (rola „otherapp”) oraz bazę I rolę „zdrowieinfo”, które należą do tamtej aplikacji. ZdrowieInfo musi dodać własną rolę
# i bazę (zdrowieinfo_2) i nigdy nie instalować, aktualizować, restartować ani konfigurować PostgreSQL ani zmieniać
# niczego innej aplikacji – przy instalacji, kopii, przywracaniu i odinstalowaniu. Zajęta nazwa z --db-name: odmowa;
# nieudana instalacja usuwa tylko to, co sama utworzyła.
#
# Użycie (na hoście): deploy/linux/tests/container-sharedpg-smoke.sh KATALOG_PACZEK
#   (paczka z build-server.sh --repository "" – serwer w kontenerze nie łączy się z GitHubem)
set -Eeuo pipefail

if [[ "${1-}" != --inside ]]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    artifacts="$(cd "${1:?użycie: $0 KATALOG_PACZEK}" && pwd)"
    exec docker run --rm -v "$here:/src:ro" -v "$artifacts:/pkg:ro" ubuntu:24.04 bash /src/tests/container-sharedpg-smoke.sh --inside
fi

fails=0
check() { local d="$1"; shift; if "$@"; then echo "ok   - $d"; else echo "FAIL - $d"; fails=$((fails + 1)); fi; }

case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
pkg="$(ls /pkg/ZdrowieInfo.Server-*-linux-$arch.tar.gz | sort -V | tail -n1)"
echo "# paczka $pkg"

# ---- serwer przed ZdrowieInfo: PostgreSQL używany przez inną aplikację
export DEBIAN_FRONTEND=noninteractive
apt-get update -q >/dev/null
apt-get install -y -q --no-install-recommends postgresql curl ca-certificates iproute2 >/dev/null
v="$(ls /etc/postgresql | sort -V | tail -n1)"
pg_ctlcluster "$v" main start
pg_createcluster "$v" second --port 5433 --start >/dev/null
psql_pg() { runuser -u postgres -- psql -X -At -q -v ON_ERROR_STOP=1 "$@"; }
psql_pg -p 5432 -d postgres <<'SQL'
CREATE ROLE otherapp LOGIN PASSWORD 'OtherAppPass1';
CREATE DATABASE otherapp OWNER otherapp;
CREATE ROLE zdrowieinfo LOGIN PASSWORD 'TheirOwnPass1';
CREATE DATABASE zdrowieinfo OWNER zdrowieinfo;
SQL
psql_pg -p 5432 -d otherapp -c "CREATE TABLE things(x int); INSERT INTO things VALUES (42); ALTER TABLE things OWNER TO otherapp;"
psql_pg -p 5432 -d zdrowieinfo -c "CREATE TABLE theirs(x int); INSERT INTO theirs VALUES (7); ALTER TABLE theirs OWNER TO zdrowieinfo;"
psql_pg -p 5433 -d postgres -c "CREATE DATABASE secondapp;"
dpkg-query -W 'postgresql*' >/tmp/pkgs.before
pg_lsclusters --no-header | awk '{print $1, $2, $3, $4}' >/tmp/clusters.before
conf_before="$(cat /etc/postgresql/"$v"/main/postgresql.conf /etc/postgresql/"$v"/main/pg_hba.conf | sha256sum)"

intact() { # dane, role i hasła innej aplikacji, pakiety, klastry i konfiguracja PostgreSQL bez zmian
    [[ "$(psql_pg -p 5432 -d otherapp -c 'SELECT x FROM things')" == 42 ]] || { echo "     otherapp.things zmienione"; return 1; }
    [[ "$(psql_pg -p 5432 -d zdrowieinfo -c 'SELECT x FROM theirs')" == 7 ]] || { echo "     zdrowieinfo.theirs zmienione"; return 1; }
    [[ "$(psql_pg -p 5432 -d postgres -c "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'zdrowieinfo'")" == zdrowieinfo ]] ||
        { echo "     zmieniony właściciel bazy zdrowieinfo"; return 1; }
    [[ "$(PGPASSWORD=TheirOwnPass1 psql -X -At -h 127.0.0.1 -p 5432 -U zdrowieinfo -d zdrowieinfo -c 'SELECT 1')" == 1 ]] ||
        { echo "     zmienione hasło roli zdrowieinfo"; return 1; }
    [[ "$(PGPASSWORD=OtherAppPass1 psql -X -At -h 127.0.0.1 -p 5432 -U otherapp -d otherapp -c 'SELECT 1')" == 1 ]] ||
        { echo "     rola otherapp nie może się zalogować"; return 1; }
    [[ "$(psql_pg -p 5433 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname = 'secondapp'")" == 1 ]] ||
        { echo "     zmieniony drugi klaster"; return 1; }
    [[ "$(psql_pg -p 5432 -d otherapp -c "SELECT count(*) FROM pg_extension WHERE extname = 'unaccent'")" == 0 ]] ||
        { echo "     rozszerzenie w bazie innej aplikacji"; return 1; }
    dpkg-query -W "postgresql*" | cmp -s - /tmp/pkgs.before || { echo "     zmienione pakiety PostgreSQL:"; dpkg-query -W "postgresql*" | diff /tmp/pkgs.before - | sed "s/^/       /"; return 1; }
    pg_lsclusters --no-header | awk '{print $1, $2, $3, $4}' | cmp -s - /tmp/clusters.before || { echo "     zmienione klastry"; return 1; }
    [[ "$(cat /etc/postgresql/"$v"/main/postgresql.conf /etc/postgresql/"$v"/main/pg_hba.conf | sha256sum)" == "$conf_before" ]] ||
        { echo "     zmieniona konfiguracja PostgreSQL"; return 1; }
}
check "przed: inna aplikacja gotowa" intact

cat >/usr/local/bin/systemctl <<'STUB'
#!/bin/bash
exec 9>&-
echo "systemctl $*" >>/tmp/systemctl.log
args=" $* "
stop_server() { pkill -TERM -u zdrowieinfo -f '/ZdrowieInfo\.Server$' 2>/dev/null || true; for _ in $(seq 1 75); do pgrep -u zdrowieinfo -f '/ZdrowieInfo\.Server$' >/dev/null || break; sleep 0.2; done; }
start_server() {
    stop_server
    local env=() line
    while IFS= read -r line; do [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] && env+=("$line"); done </opt/zdrowieinfo/config/zdrowieinfo.env
    (cd /opt/zdrowieinfo/current && runuser -u zdrowieinfo -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
        ASPNETCORE_ENVIRONMENT=Production DOTNET_NOLOGO=1 "${env[@]}" /opt/zdrowieinfo/current/ZdrowieInfo.Server >>/tmp/server.log 2>&1 &)
}
case "$args" in
    *" zdrowieinfo.service "*)
        case "$1" in start|restart) start_server ;; stop) stop_server ;; disable) [[ "$args" == *" --now "* ]] && stop_server ;; esac ;;
esac
exit 0
STUB
chmod 755 /usr/local/bin/systemctl

# Harmonogram źródeł wyłączony przed pierwszym startem (kontener nie odpytuje stron NFZ).
cp /src/install.sh /tmp/install.sh
sed -i 's/^if \[\[ "${BASH_SOURCE\[0\]}" == "$0" \]\]; then main "$@"; fi$//' /tmp/install.sh
cat >>/tmp/install.sh <<'EOF'
eval "orig_$(declare -f write_config)"
write_config() { orig_write_config; grep -q SchedulerEnabled "$CONFIG_FILE" || echo "ZdrowieInfo__Sources__SchedulerEnabled=false" >>"$CONFIG_FILE"; }
main "$@"
EOF

export ZDROWIEINFO_ADMIN_PASSWORD='Smoke-Test-Haslo-1'
common=(--unattended --db=local --package "$pkg" --admin-user szef --public-url https://zdrowie.example.test --no-pdf)
set +eu
trap - ERR

# ---- zajęta nazwa z --db-name: odmowa, nic nie zostaje
bash /tmp/install.sh "${common[@]}" --db-name zdrowieinfo >/tmp/install-taken.log 2>&1
check "zajęta --db-name: odmowa"         [ $? -ne 0 ]
check "  z jasnym komunikatem"           grep -q "już istnieje" /tmp/install-taken.log
check "  nic nie zainstalowano"          [ ! -e /opt/zdrowieinfo/config/install.state ]
check "  nie utworzono roli ani bazy"    [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname LIKE 'zdrowieinfo_%'")" == 0 ]
check "  inna aplikacja nietknięta"      intact
rm -rf /opt/zdrowieinfo; userdel zdrowieinfo 2>/dev/null

# ---- instalacja
: >/tmp/systemctl.log
bash /tmp/install.sh "${common[@]}" >/tmp/install.log 2>&1
rc=$?
check "instalacja udana"                 [ $rc -eq 0 ]
((rc == 0)) || tail -n 30 /tmp/install.log
check "istniejący PostgreSQL współdzielony" grep -q "PostgreSQL jest już zainstalowany" /tmp/install.log
check "PostgreSQL nigdy nie (re)startowany" bash -c "! grep -q postgresql /tmp/systemctl.log"
check "własna nazwa zdrowieinfo_2"       grep -qx "DB_NAME=zdrowieinfo_2" /opt/zdrowieinfo/config/install.state
check "rola zdrowieinfo_2"               grep -qx "DB_USER=zdrowieinfo_2" /opt/zdrowieinfo/config/install.state
check "klaster 5432"                     grep -qx "DB_PORT=5432" /opt/zdrowieinfo/config/install.state
check "rola bez uprawnień specjalnych"   [ "$(psql_pg -p 5432 -d postgres -c "SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname = 'zdrowieinfo_2'")" == f ]
check "inni nie łączą się z jej bazą"    bash -c "! PGPASSWORD=OtherAppPass1 psql -X -At -h 127.0.0.1 -p 5432 -U otherapp -d zdrowieinfo_2 -c 'SELECT 1' >/dev/null 2>&1"
check "inna aplikacja nietknięta"        intact

for _ in $(seq 1 60); do curl -fs --max-time 3 http://127.0.0.1:5180/healthz >/dev/null 2>&1 && break; sleep 1; done
check "healthz odpowiada"                bash -c "curl -fs http://127.0.0.1:5180/healthz | grep -q '\"status\":\"Healthy\"'"
check "migracje w zdrowieinfo_2"         [ "$(psql_pg -p 5432 -d zdrowieinfo_2 -c "SELECT count(*) FROM pg_tables WHERE tablename = 'reports'")" == 1 ]
check "unaccent tylko w zdrowieinfo_2"   [ "$(psql_pg -p 5432 -d zdrowieinfo_2 -c "SELECT count(*) FROM pg_extension WHERE extname = 'unaccent'")" == 1 ]
check "nic nie zapisano w cudzej bazie"  [ "$(psql_pg -p 5432 -d zdrowieinfo -c "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")" == 1 ]

# ---- kopia i przywrócenie używają tylko zdrowieinfo_2
bash /opt/zdrowieinfo/bin/backup.sh create >/tmp/backup.log 2>&1
check "kopia utworzona"                  grep -q "Kopia zapisana" /tmp/backup.log
file="$(ls -t /opt/zdrowieinfo/backups/*.tar.gz | head -n1)"
check "kopia bazy zdrowieinfo_2"         bash -c "tar -xzOf '$file' manifest.txt | grep -qx database=zdrowieinfo_2"
bash /src/install.sh --restore "$file" --unattended --yes >/tmp/restore.log 2>&1
check "przywrócenie udane"               [ $? -eq 0 ]
check "stan nadal zdrowieinfo_2"         grep -qx "DB_NAME=zdrowieinfo_2" /opt/zdrowieinfo/config/install.state
check "  port i znacznik utworzenia"     bash -c "grep -qx DB_PORT=5432 /opt/zdrowieinfo/config/install.state && grep -qx DB_CREATED=1 /opt/zdrowieinfo/config/install.state"
check "inna aplikacja nietknięta"        intact

# ---- odinstalowanie usuwa tylko zdrowieinfo_2
bash /src/install.sh --uninstall --unattended --yes >/tmp/uninstall.log 2>&1
check "odinstalowanie udane"             [ $? -eq 0 ]
check "baza zdrowieinfo_2 usunięta"      [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname = 'zdrowieinfo_2'")" == 0 ]
check "rola zdrowieinfo_2 usunięta"      [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_roles WHERE rolname = 'zdrowieinfo_2'")" == 0 ]
check "inna aplikacja nietknięta"        intact
check "PostgreSQL nigdy nie (re)startowany" bash -c "! grep -q postgresql /tmp/systemctl.log"

# ---- nieudana instalacja usuwa tylko to, co sama utworzyła (błąd po kroku bazy: data/ jest dowiązaniem)
: >/tmp/systemctl.log
mkdir -p /opt/zdrowieinfo && ln -s /tmp /opt/zdrowieinfo/data
bash /tmp/install.sh "${common[@]}" >/tmp/install-fail.log 2>&1
check "nieudana instalacja"              [ $? -ne 0 ]
check "  po utworzeniu bazy"             grep -q "Tworzenie roli 'zdrowieinfo_2'" /tmp/install-fail.log
check "  jej baza usunięta"              [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname LIKE 'zdrowieinfo_%'")" == 0 ]
check "  jej rola usunięta"              [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'zdrowieinfo_%'")" == 0 ]
check "  inna aplikacja nietknięta"      intact

if ((fails)); then echo "--- install.log"; tail -n 30 /tmp/install.log; echo "--- uninstall.log"; tail -n 20 /tmp/uninstall.log; fi
echo "# błędów: $fails"
exit $((fails > 0))
