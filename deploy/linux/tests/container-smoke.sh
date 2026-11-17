#!/usr/bin/env bash
# TYLKO DO TESTÓW. Sprawdza install.sh, update.sh i backup.sh w jednorazowym kontenerze ubuntu:24.04.
# (Produkcja nie używa Dockera; kontener daje tylko czyste Ubuntu.)
#
# Kontener nie ma systemd, więc systemctl zastępuje atrapa (serwer uruchamiany bez deskryptora blokady update.sh –
# pod systemd usługa nigdy go nie dziedziczy): PostgreSQL uruchamia pg_ctlcluster, „start
# zdrowieinfo.service” uruchamia prawdziwy serwer jako użytkownik usługi (z konfiguracją z zdrowieinfo.env), „stop” go
# zatrzymuje, a „start zdrowieinfo-update.service” uruchamia /opt/zdrowieinfo/bin/update.sh. Jednostek (path, timer,
# ograniczenia z [Service]) nie da się tu sprawdzić.
#
# Zakres: pakiety apt, użytkownik systemowy, właściciele i prawa, lokalny PostgreSQL z rolą i bazą, zdrowieinfo.env
# i update.env, migracje i pierwsze konto, /healthz z wersją; update.sh: wycofanie (nowa wersja nie odpowiada – baza
# z rozszerzeniem unaccent przywrócona, dowiązanie z powrotem), odmowy i prawdziwa aktualizacja do nowszej wersji;
# install.sh --upgrade, kopia i przywrócenie, nowe hasło i reset 2FA, naprawa, odinstalowanie, stary układ.
#
# Użycie (na hoście): deploy/linux/tests/container-smoke.sh KATALOG_PACZEK KATALOG_NOWSZEJ_WERSJI
#   KATALOG_PACZEK: ZdrowieInfo.Server-<wersja>-linux-<arch>.tar.gz dla architektury hosta Dockera;
#   KATALOG_NOWSZEJ_WERSJI: to samo w wyższej wersji (np. zbudowane z kopii repozytorium z podniesioną wersją).
#   Paczki budowane przez build-server.sh --repository "" (serwer w kontenerze nie łączy się z GitHubem).
#   ZI_SMOKE_PDF=1: z TeX Live (kilkaset MB) i eksportem PDF raportu jako użytkownik usługi.
set -Eeuo pipefail

if [[ "${1-}" != --inside ]]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    artifacts="$(cd "${1:?użycie: $0 KATALOG_PACZEK KATALOG_NOWSZEJ_WERSJI}" && pwd)"
    next="$(cd "${2:?użycie: $0 KATALOG_PACZEK KATALOG_NOWSZEJ_WERSJI}" && pwd)"
    exec docker run --rm -e ZI_SMOKE_PDF="${ZI_SMOKE_PDF-}" -v "$here:/src:ro" -v "$artifacts:/pkg:ro" -v "$next:/pkg-next:ro" ubuntu:24.04 bash /src/tests/container-smoke.sh --inside
fi

# ------------------------------------------------------------------------------------------------ w kontenerze
fails=0
check() { local d="$1"; shift; if "$@"; then echo "ok   - $d"; else echo "FAIL - $d"; fails=$((fails + 1)); fi; }
mode_owner() { [[ "$(stat -c '%a %U:%G' "$1")" == "$2" ]] || { echo "     $1 ma $(stat -c '%a %U:%G' "$1"), oczekiwano $2"; return 1; }; }

case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
pkg="$(ls /pkg/ZdrowieInfo.Server-*-linux-$arch.tar.gz | sort -V | tail -n1)"
version="$(basename "$pkg" | sed -E 's/^ZdrowieInfo\.Server-([0-9.]+)-linux-.*/\1/')"
pkg_next="$(ls /pkg-next/ZdrowieInfo.Server-*-linux-$arch.tar.gz | sort -V | tail -n1)"
next="$(basename "$pkg_next" | sed -E 's/^ZdrowieInfo\.Server-([0-9.]+)-linux-.*/\1/')"
echo "# paczka $pkg ($version), nowsza $pkg_next ($next)"

cat >/usr/local/bin/systemctl <<'STUB'
#!/bin/bash
exec 9>&-   # deskryptor blokady update.sh – pod systemd usługa go nie dziedziczy
echo "systemctl $*" >>/tmp/systemctl.log
start_pg() { local v; v="$(ls /etc/postgresql | sort -V | tail -n1)"; pg_ctlcluster --skip-systemctl-redirect "$v" main start 2>/dev/null || true; }
stop_server() {
    pkill -TERM -u zdrowieinfo -f '/ZdrowieInfo\.Server$' 2>/dev/null || true
    for _ in $(seq 1 75); do pgrep -u zdrowieinfo -f '/ZdrowieInfo\.Server$' >/dev/null || break; sleep 0.2; done
}
start_server() {
    stop_server
    local env=() line
    while IFS= read -r line; do [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] && env+=("$line"); done </opt/zdrowieinfo/config/zdrowieinfo.env
    (cd /opt/zdrowieinfo/current && runuser -u zdrowieinfo -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
        ASPNETCORE_ENVIRONMENT=Production DOTNET_NOLOGO=1 "${env[@]}" \
        /opt/zdrowieinfo/current/ZdrowieInfo.Server >>/tmp/server.log 2>&1 &)
}
args=" $* "
case "$args" in
    *postgresql*) [[ "$1" == enable || "$1" == start || "$1" == restart ]] && start_pg ;;
    *" zdrowieinfo-update.service "*) [[ "$1" == start ]] && /opt/zdrowieinfo/bin/update.sh ;;
    *" zdrowieinfo.service "*)
        case "$1" in
            start|restart) start_server ;;
            stop) stop_server ;;
            disable) [[ "$args" == *" --now "* ]] && stop_server ;;
        esac ;;
esac
exit 0
STUB
chmod 755 /usr/local/bin/systemctl

R=/opt/zdrowieinfo
health() { curl -fs --max-time 5 http://127.0.0.1:5180/healthz 2>/dev/null; }
wait_health() { # wait_health WERSJA
    for _ in $(seq 1 60); do [[ "$(health)" == *"\"version\":\"$1\""* ]] && return 0; sleep 1; done
    return 1
}
db() { PGPASSWORD="$(sed -n 's/^PGPASSWORD=//p' $R/config/update.env)" psql -X -At -h 127.0.0.1 -U zdrowieinfo -d zdrowieinfo -c "$1"; }

# --- instalacja bez pytań, lokalna baza, bez TeX. Harmonogram źródeł wyłączony (kontener nie odpytuje stron NFZ).
export ZDROWIEINFO_ADMIN_PASSWORD='Smoke-Test-Haslo-1'
cp /src/install.sh /tmp/install.sh
cat >>/tmp/install.sh <<'EOF'
eval "orig_$(declare -f write_config)"
write_config() { orig_write_config; grep -q SchedulerEnabled "$CONFIG_FILE" || echo "ZdrowieInfo__Sources__SchedulerEnabled=false" >>"$CONFIG_FILE"; }
EOF
sed -i 's/^if \[\[ "${BASH_SOURCE\[0\]}" == "$0" \]\]; then main "$@"; fi$//' /tmp/install.sh
echo 'main "$@"' >>/tmp/install.sh
pdf_flag=(--no-pdf); [[ -n "${ZI_SMOKE_PDF-}" ]] && pdf_flag=()
bash /tmp/install.sh --unattended --package "$pkg" --db=local --public-url https://zdrowie.example.test --admin-user szef "${pdf_flag[@]}" \
    >/tmp/install.log 2>&1 || { tail -n 40 /tmp/install.log; echo "FAIL - instalacja"; exit 1; }
tail -n 14 /tmp/install.log
set +eu
trap - ERR
trap 'echo "# skrypt zakończony (kod $?) po linii $LINENO"' EXIT

check "current -> releases/$version"   [ "$(readlink $R/current)" == "$R/releases/$version" ]
check "wersja należy do roota"         mode_owner "$R/releases/$version" "755 root:root"
check "w wersji tylko pliki roota"     [ -z "$(find $R/releases/$version ! -user root -print -quit)" ]
check "program wykonywalny"            [ -x "$R/releases/$version/ZdrowieInfo.Server" ]
check "config 700 root"                mode_owner "$R/config" "700 root:root"
check "zdrowieinfo.env 600 root"       mode_owner "$R/config/zdrowieinfo.env" "600 root:root"
check "update.env 600 root"            mode_owner "$R/config/update.env" "600 root:root"
check "install.state 600 root"         mode_owner "$R/config/install.state" "600 root:root"
check "data 700"                       mode_owner "$R/data" "700 zdrowieinfo:zdrowieinfo"
check "data/updates 700"               mode_owner "$R/data/updates" "700 zdrowieinfo:zdrowieinfo"
check "backups tylko root"             mode_owner "$R/backups" "700 root:root"
check "backups/pre-update tylko root"  mode_owner "$R/backups/pre-update" "700 root:root"
check "bin/update.sh root 755"         mode_owner "$R/bin/update.sh" "755 root:root"
check "paczka ma ten update.sh (inaczej przebuduj)" cmp -s /src/update.sh "$R/bin/update.sh"
check "paczka ma ten backup.sh"        cmp -s /src/backup.sh "$R/bin/backup.sh"
for u in zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-update.service zdrowieinfo-backup.service zdrowieinfo-backup.timer; do
    check "jednostka $u"               mode_owner "/etc/systemd/system/$u" "644 root:root"
done
check "jednostka: zapis tylko w data/" grep -q '^ReadWritePaths=/opt/zdrowieinfo/data$' /etc/systemd/system/zdrowieinfo.service
check "użytkownik bez powłoki"         [ "$(getent passwd zdrowieinfo | cut -d: -f7)" == /usr/sbin/nologin ]
check "env: adres publiczny"           grep -qx 'ZdrowieInfo__PublicUrl=https://zdrowie.example.test' $R/config/zdrowieinfo.env
check "env: Kestrel lokalnie"          grep -qx 'ASPNETCORE_URLS=http://127.0.0.1:5180' $R/config/zdrowieinfo.env
if [[ -z "${ZI_SMOKE_PDF-}" ]]; then
    check "env: bez PDF"               grep -qx 'ZdrowieInfo__Export__Enabled=false' $R/config/zdrowieinfo.env
fi
check "serwer odpowiada wersją $version" wait_health "$version"
check "baza: rola jest właścicielem"   [ "$(db "select pg_get_userbyid(datdba) from pg_database where datname='zdrowieinfo'")" == zdrowieinfo ]
check "baza: unaccent i pl_unaccent"   [ "$(db "select to_tsvector('pl_unaccent','Źródło')::text")" == "'zrodlo':1" ]
check "konto szef jest administratorem" [ "$(db "select role from \"AspNetUsers\" where user_name='szef'")" == 1 ]
check "hasło bazy nie w logach"        bash -c '! grep -qF -- "$(sed -n "s/^PGPASSWORD=//p" '"$R"'/config/update.env)" /tmp/install.log /tmp/systemctl.log'
check "hasło konta nie w logach"       bash -c '! grep -qF -- "$ZDROWIEINFO_ADMIN_PASSWORD" /tmp/install.log /tmp/systemctl.log'

# Dane do sprawdzenia przywracania: raport (pusty „Brak zmian”, bez Claude) i znacznik.
(cd $R/current && source $R/bin/update.sh && zi_run_as_service_user $R/current --generate-report >/tmp/report.log 2>&1)
check "raport przygotowany"            [ "$(db 'select count(*) from reports')" == 1 ]
if [[ -n "${ZI_SMOKE_PDF-}" ]]; then
    (cd $R/current && source $R/bin/update.sh && zi_run_as_service_user $R/current --export-report >/tmp/export.log 2>&1)
    check "PDF raportu jako użytkownik usługi" bash -c "ls $R/data/reports/*/raport-*.pdf >/dev/null 2>&1 && head -c 5 \$(ls $R/data/reports/*/raport-*.pdf | head -n1) | grep -q '%PDF-'"
    tail -n 4 /tmp/export.log | sed 's/^/     /'
fi
db "create table smoke_marker(v text); insert into smoke_marker values ('przed')" >/dev/null

# --- update.sh: paczka przygotowana tak jak robi to serwer
stage() { # stage WERSJA PACZKA
    local v="$1" src="$2" name="ZdrowieInfo.Server-$1-linux-$arch.tar.gz" d="$R/data/updates/$1"
    install -d -o zdrowieinfo -g zdrowieinfo -m 700 "$d"
    install -o zdrowieinfo -g zdrowieinfo -m 600 "$src" "$d/$name"
    local sha; sha="$(sha256sum "$src" | cut -d' ' -f1)"
    printf '%s  %s\n' "$sha" "$name" >"$d/SHA256SUMS.txt"; chown zdrowieinfo: "$d/SHA256SUMS.txt"
    printf 'format=1\nrun_id=%s\nversion=%s\nfrom_version=%s\narch=%s\nfile=%s\nsha256=%s\n' \
        "$(cat /proc/sys/kernel/random/uuid)" "$v" "$version" "$arch" "$name" "$sha" >"$R/data/updates/request.conf"
    chown zdrowieinfo: "$R/data/updates/request.conf"
}
result() { sed -n "s/^$1=//p" "$R/data/updates/result.conf"; }
# Po wycofaniu i aktualizacji uruchomiony serwer sam czyta result.conf (i go usuwa) – wynik jest wtedy w update_runs.
last_run() {
    local out
    for _ in $(seq 1 30); do
        out="$(db "select state || '|' || coalesce(message, '') from update_runs where finished_at is not null order by finished_at desc limit 1")"
        [[ "$out" == "$1|"* ]] && { echo "$out"; return 0; }
        sleep 1
    done
    echo "$out"; return 1
}

# 1) Wycofanie: paczka udaje wersję 9.0.1, ale serwer zgłasza starą – sprawdzanie się nie udaje. Też: podłożone
#    dowiązanie result.conf do pliku roota nie może zostać użyte.
sed -i 's/^HEALTH_TIMEOUT_SECONDS=.*/HEALTH_TIMEOUT_SECONDS=20/' "$R/bin/update.sh"
echo 'ofiara' >/etc/zi-victim; chmod 644 /etc/zi-victim
runuser -u zdrowieinfo -- ln -s /etc/zi-victim "$R/data/updates/result.conf"
stage 9.0.1 "$pkg"
"$R/bin/update.sh" >/tmp/update1.log 2>&1
check "wycofanie: kod błędu"           [ $? -ne 0 ]
check "wycofanie: zlecenie zabrane"    [ ! -e "$R/data/updates/request.conf" ]
run1="$(last_run RolledBack)"
check "wycofanie: wynik RolledBack w panelu" grep -q '^RolledBack|' <<<"$run1"
check "wycofanie: komunikat po polsku" grep -qF "przywrócono wersję $version" <<<"$run1"
check "wycofanie: current z powrotem"  [ "$(readlink $R/current)" == "$R/releases/$version" ]
check "wycofanie: kopia bazy root 600" mode_owner "$(ls $R/backups/pre-update/*.dump | head -n1)" "600 root:root"
check "dowiązanie result.conf: cel nietknięty" bash -c "[ \"\$(cat /etc/zi-victim)\" == ofiara ] && [ \"\$(stat -c '%a %U' /etc/zi-victim)\" == '644 root' ]"
check "wycofanie: baza przywrócona (raport)" [ "$(db 'select count(*) from reports')" == 1 ]
check "wycofanie: unaccent po przywróceniu" [ "$(db "select to_tsvector('pl_unaccent','Źródło')::text")" == "'zrodlo':1" ]
check "wycofanie: wyszukiwanie po przywróceniu" [ "$(db "select count(*) from reports where search_vector @@ websearch_to_tsquery('pl_unaccent','zmian')")" == 1 ]
check "wycofanie: stara wersja działa"  wait_health "$version"
grep -E 'wycofywanie|wynik' /tmp/update1.log | sed 's/^/     /'

# 2) Odmowy: wersja nie nowsza i zlecenie będące dowiązaniem.
stage 0.0.1 "$pkg"
"$R/bin/update.sh" >/tmp/update2.log 2>&1
check "starsza wersja: Failed"         [ "$(result state)" == Failed ]
check "starsza wersja: powód"          grep -q 'nie jest nowsza' /tmp/update2.log
ln -s /etc/shadow "$R/data/updates/request.conf"
"$R/bin/update.sh" >/tmp/update3.log 2>&1
check "zlecenie-dowiązanie odrzucone"  grep -q 'nie jest zwykłym plikiem' /tmp/update3.log
check "/etc/shadow nietknięty"         mode_owner /etc/shadow "640 root:shadow"

# 3) Prawdziwa aktualizacja do nowszej wersji (migracje, /healthz z nową wersją, sprzątanie starych wersji).
for v in 0.0.7 0.0.8; do mkdir -p "$R/releases/$v"; done
stage "$next" "$pkg_next"
"$R/bin/update.sh" >/tmp/update4.log 2>&1
check "aktualizacja: kod 0"            [ $? -eq 0 ]
# Po udanej aktualizacji result.conf powstaje, gdy nowa wersja już działa – serwer odczyta go w ciągu 5 minut.
update_succeeded() { [ "$(result state 2>/dev/null)" == Succeeded ] || last_run Succeeded >/dev/null; }
check "aktualizacja: Succeeded"        update_succeeded
check "aktualizacja: current -> $next" [ "$(readlink $R/current)" == "$R/releases/$next" ]
check "aktualizacja: działa $next"     wait_health "$next"
check "aktualizacja: dane zostały"     [ "$(db 'select count(*) from reports')" == 1 ]
check "aktualizacja: 3 najnowsze zostają" [ "$(ls $R/releases | sort -V | tr '\n' ' ')" == "$version $next 9.0.1 " ]
tail -n 6 /tmp/update4.log | sed 's/^/     /'

# 4) install.sh --upgrade: ta sama ścieżka (przygotowanie + jednostka); starsza wersja – odmowa.
out="$(bash /src/install.sh --unattended --upgrade --package "$pkg" 2>&1)"
check "--upgrade: odmowa cofnięcia wersji" grep -q 'nie jest nowsza' <<<"$out"

# 5) Kopia i przywrócenie (na tym samym serwerze).
bash /src/install.sh --backup >/tmp/backup.log 2>&1
check "kopia: kod 0"                   [ $? -eq 0 ]
archive="$(ls $R/backups/zdrowieinfo-backup-*.tar.gz | head -n1)"
check "kopia: root 600"                mode_owner "$archive" "600 root:root"
tar -xzOf "$archive" files.tar | tar -t >/tmp/backup-files.txt
check "kopia: bez data/updates, tmp i cache TeX" bash -c "! grep -qE 'data/(updates|tmp|texmf-var|tectonic-cache)(/|\$)' /tmp/backup-files.txt"
check "kopia: z kluczami Data Protection" grep -q 'opt/zdrowieinfo/data/keys/' /tmp/backup-files.txt
check "kopia: z programem $next"       grep -q "opt/zdrowieinfo/releases/$next/ZdrowieInfo.Server" /tmp/backup-files.txt
db "delete from smoke_marker; delete from reports" >/dev/null
bash /src/install.sh --unattended --restore "$archive" >/tmp/restore0.log 2>&1
check "przywrócenie bez --yes: odmowa" grep -q -- '--yes' /tmp/restore0.log
bash /src/install.sh --unattended --yes --restore "$archive" >/tmp/restore.log 2>&1
check "przywrócenie: kod 0"            [ $? -eq 0 ]
check "przywrócenie: raport wrócił"    [ "$(db 'select count(*) from reports')" == 1 ]
check "przywrócenie: unaccent działa"  [ "$(db "select to_tsvector('pl_unaccent','Źródło')::text")" == "'zrodlo':1" ]
check "przywrócenie: działa $next"     wait_health "$next"
check "przywrócenie: kopia bezpieczeństwa" [ "$(ls $R/backups/zdrowieinfo-backup-*.tar.gz | wc -l)" -ge 2 ]
for _ in 1 2 3; do bash $R/bin/backup.sh create --keep 2 >/dev/null 2>&1; sleep 1; done
check "kopie: --keep 2 zostawia 2"     [ "$(ls $R/backups/zdrowieinfo-backup-*.tar.gz | wc -l)" == 2 ]
tail -n 3 /tmp/restore.log | sed 's/^/     /'

# 6) Nowe hasło i usunięcie 2FA.
stamp="$(db "select security_stamp from \"AspNetUsers\" where user_name='szef'")"
ZDROWIEINFO_ADMIN_PASSWORD='Nowe-Haslo-Smoke-2' bash /src/install.sh --unattended --set-password szef >/tmp/setpw.log 2>&1
check "--set-password: kod 0"          [ $? -eq 0 ]
check "--set-password: nowy stamp"     [ "$(db "select security_stamp from \"AspNetUsers\" where user_name='szef'")" != "$stamp" ]
ZDROWIEINFO_ADMIN_PASSWORD='krotkie' bash /src/install.sh --unattended --set-password szef >/tmp/setpw2.log 2>&1
check "--set-password: słabe hasło odrzucone" grep -q 'nie spełnia zasad' /tmp/setpw2.log
bash /src/install.sh --unattended --set-password nieznany >/tmp/setpw3.log 2>&1
check "--set-password: nieznany login" [ $? -ne 0 ]
db "update \"AspNetUsers\" set two_factor_enabled = true where user_name='szef'" >/dev/null
bash /src/install.sh --unattended --reset-mfa szef >/tmp/mfa.log 2>&1
check "--reset-mfa: kod 0"             [ $? -eq 0 ]
check "--reset-mfa: TOTP wyłączone"    [ "$(db "select two_factor_enabled from \"AspNetUsers\" where user_name='szef'")" == f ]

# 7) --repair: konfiguracja i dane zostają, prawa naprawione; pliki roota nigdy z current/.
cfg_sum="$(sha256sum $R/config/zdrowieinfo.env)"
chmod 644 $R/config/zdrowieinfo.env; chmod 755 $R/data
bin_sum="$(sha256sum $R/bin/update.sh | cut -c1-64)"
printf '\ntouch /tmp/zi-pwned-by-current\n' >>"$R/current/deploy/linux/update.sh"
bash /src/install.sh --unattended --repair >/tmp/repair.log 2>&1
check "repair: kod 0"                  [ $? -eq 0 ]
check "repair: konfiguracja bez zmian" [ "$(sha256sum $R/config/zdrowieinfo.env)" == "$cfg_sum" ]
check "repair: prawa konfiguracji"     mode_owner $R/config/zdrowieinfo.env "600 root:root"
check "repair: prawa data"             mode_owner $R/data "700 zdrowieinfo:zdrowieinfo"
check "repair: update.sh nie z current/" [ "$(sha256sum $R/bin/update.sh | cut -c1-64)" == "$bin_sum" ]
bash /src/install.sh --unattended --repair --package "$pkg_next" >/tmp/repair2.log 2>&1
check "repair --package: kod 0"        [ $? -eq 0 ]
check "repair --package: update.sh z paczki" cmp -s /src/update.sh "$R/bin/update.sh"
check "podmieniony update.sh nie działał" [ ! -e /tmp/zi-pwned-by-current ]
check "repair: serwer działa"          wait_health "$next"

# 8) Odinstalowanie: --keep-data zostawia konfigurację, dane i bazę; --uninstall --yes usuwa wszystko.
bash /src/install.sh --uninstall --keep-data >/tmp/uninstall.log 2>&1
check "keep-data: program usunięty"    [ ! -e "$R/releases" ]
check "keep-data: jednostki usunięte"  [ ! -e /etc/systemd/system/zdrowieinfo.service ]
check "keep-data: konfiguracja została" [ -f $R/config/zdrowieinfo.env ]
check "keep-data: baza została"        [ "$(runuser -u postgres -- psql -X -At -c "select count(*) from pg_database where datname='zdrowieinfo'")" == 1 ]
check "keep-data: serwer zatrzymany"   bash -c '! curl -fs --max-time 2 http://127.0.0.1:5180/healthz >/dev/null'
bash /src/install.sh --uninstall --unattended >/tmp/purge1.log 2>&1
check "uninstall bez --yes: nic"       [ -d $R/config ]
bash /src/install.sh --uninstall --unattended --yes >/tmp/purge2.log 2>&1
check "uninstall: katalog usunięty"    [ ! -e "$R" ]
check "uninstall: użytkownik usunięty" bash -c '! id -u zdrowieinfo >/dev/null 2>&1'
check "uninstall: baza usunięta"       [ "$(runuser -u postgres -- psql -X -At -c "select count(*) from pg_database where datname='zdrowieinfo'")" == 0 ]

# 9) Bez --db: odmowa przed zmianami; stary układ (/etc/zdrowieinfo) – odmowa.
bash /src/install.sh --unattended --package "$pkg" --public-url https://zdrowie.example.test >/tmp/missing.log 2>&1
check "bez --db: odmowa"               grep -q -- '--db=local|existing' /tmp/missing.log
check "bez --db: nic nie utworzono"    [ ! -e "$R" ]
mkdir -p /etc/zdrowieinfo && echo 'ZdrowieInfo__PublicUrl=https://x' >/etc/zdrowieinfo/zdrowieinfo.env
bash /src/install.sh --unattended --package "$pkg" --db=local --public-url https://zdrowie.example.test >/tmp/legacy.log 2>&1
check "stary układ: odmowa"            grep -q 'poprzednim instalatorem' /tmp/legacy.log
check "stary układ: nic nie utworzono" [ ! -e "$R" ]

echo "# błędów: $fails"
[[ $fails -eq 0 ]]
