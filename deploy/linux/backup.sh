#!/usr/bin/env bash
# Kopia zapasowa i przywracanie ZdrowieInfo (Ubuntu 24.04+, instalacja przez install.sh). Uruchamiać jako root.
#
#   backup.sh create [--output ŚCIEŻKA] [--keep N]
#                                      jedno archiwum: baza, konfiguracja, klucze i dane, działający program.
#                                      ŚCIEŻKA: katalog albo nazwa pliku .tar.gz (domyślnie /opt/zdrowieinfo/backups).
#                                      Katalog i wszystkie wyżej muszą należeć do roota i tylko root może w nich pisać.
#                                      --keep N: w katalogu domyślnym zostaje N najnowszych kopii (zdrowieinfo-backup.timer: 14).
#   backup.sh restore PLIK [--yes]     przywraca kopię na tym serwerze: bazę, konfigurację, klucze, dane i program
#                                      (wersja z kopii staje się działającą). Najpierw powstaje kopia stanu bieżącego.
#                                      ZdrowieInfo musi być zainstalowane (install.sh): ustawienia połączenia z bazą,
#                                      update.sh, backup.sh i jednostki systemd zostają tego serwera (nigdy z kopii),
#                                      więc kopię można też przywrócić na nowym serwerze.
#   backup.sh list                     kopie w /opt/zdrowieinfo/backups
#
# Archiwum zawiera sekrety (klucze Data Protection – bez nich zaszyfrowany klucz Claude API i klucze VAPID są nie do
# odczytania; hasło bazy): zapisywane z prawami 600, właściciel root. Trzymaj kopie także poza serwerem.
set -Eeuo pipefail

HERE="$(cd "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# Sprawdzanie archiwów jest wspólne z update.sh, zainstalowanym obok (bin/) albo leżącym obok w paczce.
if [[ ! -f "$HERE/update.sh" || -L "$HERE/update.sh" ]]; then
    printf 'BŁĄD: brak %s; uruchom install.sh --repair --package PLIK\n' "$HERE/update.sh" >&2
    exit 1
fi
# shellcheck source=update.sh
source "$HERE/update.sh"
set -Eeuo pipefail

ZI_ROOT="${ZI_ROOT:-/opt/zdrowieinfo}"
SERVICE_USER=zdrowieinfo
UNIT_DIR=/etc/systemd/system
UNITS=(zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-update.service zdrowieinfo-backup.service zdrowieinfo-backup.timer)
FORMAT=1
CONFIG="$ZI_ROOT/config/zdrowieinfo.env"
UPDATE_ENV="$ZI_ROOT/config/update.env"
STATE="$ZI_ROOT/config/install.state"
WORK="" OUT=""

info() { printf '==> %s\n' "$*"; }
warn() { printf 'UWAGA: %s\n' "$*" >&2; }
die() { printf 'BŁĄD: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
cleanup() {
    if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf -- "$WORK"; fi
    if [[ -n "$OUT" && -d "$OUT" ]]; then rm -rf -- "$OUT"; fi
}
trap cleanup EXIT

require_install() {
    [[ $EUID -eq 0 ]] || die "uruchom jako root: sudo $0"
    [[ -f "$UPDATE_ENV" && -f "$CONFIG" && -L "$ZI_ROOT/current" ]] ||
        die "ZdrowieInfo nie jest tu zainstalowane ($ZI_ROOT). Najpierw install.sh."
}

# update.env: linie KLUCZ=WARTOŚĆ zapisane przez install.sh (wartości dosłowne, nigdy wykonywane).
load_db_env() {
    local key value
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ "$line" =~ ^(PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE|PG_BIN|HEALTH_URL)=(.*)$ ]] || continue
        key="${BASH_REMATCH[1]}"; value="${BASH_REMATCH[2]}"
        printf -v "$key" '%s' "$value"
        export "${key?}"
    done <"$UPDATE_ENV"
    [[ -n "${PGDATABASE-}" && -n "${PGUSER-}" ]] || die "$UPDATE_ENV nie ma ustawień bazy"
}

pg() { local tool="$1"; shift; if [[ -n "${PG_BIN-}" && -x "$PG_BIN/$tool" ]]; then "$PG_BIN/$tool" "$@"; else "$tool" "$@"; fi; }

current_version() { basename "$(readlink -f "$ZI_ROOT/current")"; }

state_value() { # state_value PLIK KLUCZ
    sed -n "s/^$2=//p" "$1" 2>/dev/null | head -n1
}

# write_root_file CEL WŁAŚCICIEL TRYB <TREŚĆ: plik tymczasowy w tym samym katalogu roota, potem zmiana nazwy
# (nigdy zapis przez dowiązanie w miejscu CELU).
write_root_file() {
    local dest="$1" tmp
    tmp="$(mktemp "$(dirname -- "$dest")/.restore.XXXXXX")"
    cat >"$tmp"
    chown -h "$2" -- "$tmp"
    chmod "$3" -- "$tmp"
    mv -T -- "$tmp" "$dest"
}

# ------------------------------------------------------------------------------------------------ create

do_create() {
    local output="" keep=0
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --output) [[ $# -ge 2 ]] || die "--output wymaga ścieżki"; output="$2"; shift ;;
            --output=*) output="${1#*=}" ;;
            --keep) [[ $# -ge 2 && "$2" =~ ^[0-9]+$ ]] || die "--keep wymaga liczby"; keep="$2"; shift ;;
            --keep=*) keep="${1#*=}"; [[ "$keep" =~ ^[0-9]+$ ]] || die "--keep wymaga liczby" ;;
            *) die "nieznany argument: $1" ;;
        esac
        shift
    done
    require_install
    load_db_env

    local version stamp dir name file default=0
    version="$(current_version)"
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    ZI_ERROR=""
    if [[ -z "$output" || "$output" == "$ZI_ROOT/backups" || "$output" == "$ZI_ROOT/backups/" ]]; then
        zi_root_dir "$ZI_ROOT/backups" || die "$ZI_ERROR (uruchom install.sh --repair)"
        output="$ZI_ROOT/backups"
        default=1
    fi
    if [[ "$output" == *.tar.gz ]]; then
        dir="$(dirname -- "$output")"
        name="$(basename -- "$output")"
    else
        dir="$output"
        name="zdrowieinfo-backup-$version-$(hostname -s)-$stamp.tar.gz"
    fi
    if [[ ! -e "$dir" && ! -L "$dir" ]]; then (umask 077; mkdir -p -- "$dir"); fi
    # Root zapisuje archiwum do tego katalogu: odmowa, gdy może go zmienić ktoś inny (albo gdy to dowiązanie).
    zi_trusted_dir "$dir" || die "katalog kopii nie jest bezpieczny: $ZI_ERROR. Użyj katalogu, w którym pisać może tylko root"
    dir="$ZI_TRUSTED_DIR"
    file="$dir/$name"
    [[ ! -e "$file" && ! -L "$file" ]] || die "$file już istnieje"

    WORK="$(mktemp -d /var/tmp/zdrowieinfo-backup.XXXXXX)"
    chmod 700 "$WORK"
    mkdir "$WORK/backup"

    info "Zrzut bazy $PGDATABASE"
    pg pg_dump --format=custom --no-password --file="$WORK/backup/database.dump" ||
        die "pg_dump nieudany (czy klient jest tak nowy jak serwer? zob. PG_BIN w $UPDATE_ENV)"

    info "Konfiguracja, klucze, dane i program ($version)"
    # update.sh, backup.sh i jednostki nie wchodzą do kopii: przywracanie zostawia zainstalowane.
    local -a paths=("${ZI_ROOT#/}/config" "${ZI_ROOT#/}/data" "${ZI_ROOT#/}/releases/$version")
    # Bez pobranych aktualizacji, plików tymczasowych i cache TeX-a (odtwarzają się same). Serwer działa: plik w data/
    # może się zmienić w trakcie czytania – tar kończy się wtedy kodem 1, a archiwum nadal jest użyteczne.
    local rc=0
    tar --create --numeric-owner --warning=no-file-changed --warning=no-file-removed --file="$WORK/backup/files.tar" \
        -C / --exclude="${ZI_ROOT#/}/data/updates" --exclude="${ZI_ROOT#/}/data/tmp" \
        --exclude="${ZI_ROOT#/}/data/texmf-var" --exclude="${ZI_ROOT#/}/data/tectonic-cache" "${paths[@]}" || rc=$?
    ((rc <= 1)) || die "tar nieudany ($rc) przy zbieraniu plików"
    ((rc == 0)) || warn "część plików w data/ zmieniła się w trakcie czytania (serwer działa); kopia jest użyteczna"

    printf '%s\n' "format=$FORMAT" "version=$version" "created=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        "host=$(hostname -f 2>/dev/null || hostname)" "database=$PGDATABASE" >"$WORK/backup/manifest.txt"

    # Budowane w nowym katalogu tylko dla roota wewnątrz celu (ten sam system plików), potem zmiana nazwy: nic nie jest
    # otwierane ani zmieniane pod przewidywalną nazwą w katalogu docelowym.
    OUT="$(mktemp -d "$dir/.zdrowieinfo-backup.XXXXXX")"
    (umask 077; tar --create --gzip --file="$OUT/backup.tar.gz" -C "$WORK/backup" manifest.txt database.dump files.tar)
    chown -h root:root -- "$OUT/backup.tar.gz"
    chmod 600 -- "$OUT/backup.tar.gz"
    [[ ! -e "$file" && ! -L "$file" ]] || die "$file już istnieje"
    mv -T -- "$OUT/backup.tar.gz" "$file"
    rmdir -- "$OUT"
    OUT=""
    info "Kopia zapisana: $file ($(du -h "$file" | cut -f1))"
    echo "Zawiera sekrety (klucze, hasło bazy): trzymaj kopię w bezpiecznym miejscu, także poza tym serwerem."

    if ((keep > 0 && default == 1)); then
        local old
        while IFS= read -r old; do
            [[ -n "$old" && -f "$old" && ! -L "$old" ]] || continue
            info "Usuwanie starej kopii $(basename "$old")"
            rm -f -- "$old"
        done < <(find "$dir" -maxdepth 1 -type f -name 'zdrowieinfo-backup-*.tar.gz' -printf '%T@ %p\n' | sort -rn | tail -n +"$((keep + 1))" | cut -d' ' -f2-)
    fi
}

# ------------------------------------------------------------------------------------------------ restore

do_restore() {
    local file="" yes=0 safety=1
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --yes|-y) yes=1 ;;
            --no-safety-backup) safety=0 ;;
            -*) die "nieznany argument: $1" ;;
            *) [[ -z "$file" ]] || die "podaj jeden plik kopii"; file="$1" ;;
        esac
        shift
    done
    [[ -n "$file" ]] || die "użycie: $0 restore PLIK [--yes]"
    [[ -f "$file" ]] || die "nie ma pliku $file"
    require_install
    # update.sh, backup.sh i jednostki działają jako root: zostają tego serwera, nigdy z kopii.
    local u
    for u in "${UNITS[@]}"; do
        [[ -f "$UNIT_DIR/$u" ]] || die "brak $UNIT_DIR/$u: najpierw install.sh --repair --package PLIK"
    done
    [[ -f "$ZI_ROOT/bin/update.sh" ]] || warn "brak $ZI_ROOT/bin/update.sh: aktualizacje nie zadziałają do install.sh --repair --package PLIK"

    WORK="$(mktemp -d /var/tmp/zdrowieinfo-restore.XXXXXX)"
    chmod 700 "$WORK"
    local listing m
    listing="$(tar --list --verbose --gzip --file="$file")" || die "$file nie jest kopią ZdrowieInfo"
    ZI_ERROR=""
    zi_check_member_types <<<"$listing" || die "$file: $ZI_ERROR"
    tar --extract --gzip --no-same-owner --no-same-permissions --file="$file" -C "$WORK" manifest.txt database.dump files.tar ||
        die "$file nie jest kopią ZdrowieInfo"
    for m in manifest.txt database.dump files.tar; do
        [[ -f "$WORK/$m" && ! -L "$WORK/$m" ]] || die "$file: $m nie jest zwykłym plikiem"
    done
    [[ "$(state_value "$WORK/manifest.txt" format)" == "$FORMAT" ]] || die "nieobsługiwany format kopii"
    local bversion
    bversion="$(state_value "$WORK/manifest.txt" version)"
    [[ "$bversion" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "kopia nie ma poprawnej wersji"

    # Odmowa dla wszystkiego, co trafiłoby poza oczekiwane ścieżki.
    local entry
    listing="$(tar --list --file="$WORK/files.tar")" || die "files.tar w kopii jest uszkodzony"
    zi_check_member_names <<<"$listing" || die "niebezpieczna kopia: $ZI_ERROR"
    while IFS= read -r entry; do
        case "$entry" in
            "${ZI_ROOT#/}/config"|"${ZI_ROOT#/}/config/"*|"${ZI_ROOT#/}/data"|"${ZI_ROOT#/}/data/"*) ;;
            "${ZI_ROOT#/}/releases/$bversion"|"${ZI_ROOT#/}/releases/$bversion/"*) ;;
            *) die "nieoczekiwana ścieżka w kopii: $entry" ;;
        esac
        [[ "$entry" != *..* ]] || die "nieoczekiwana ścieżka w kopii: $entry"
    done <<<"$listing"
    # Tylko zwykłe pliki, katalogi i dowiązania; bez twardych dowiązań, urządzeń, FIFO, setuid i setgid.
    listing="$(tar --list --verbose --file="$WORK/files.tar")" || die "files.tar w kopii jest uszkodzony"
    zi_check_member_types <<<"$listing" || die "niebezpieczna kopia: $ZI_ERROR"
    zi_check_member_modes <<<"$listing" || die "niebezpieczna kopia: $ZI_ERROR"

    # Rozpakowanie i sprawdzenie wszystkiego, zanim serwer zostanie zmieniony.
    mkdir "$WORK/root"
    local src="$WORK/root${ZI_ROOT}"
    tar --extract --no-same-owner --preserve-permissions --file="$WORK/files.tar" -C "$WORK/root" \
        "${ZI_ROOT#/}/config" "${ZI_ROOT#/}/data" "${ZI_ROOT#/}/releases/$bversion" ||
        die "kopia nie ma config, data albo releases/$bversion"
    zi_check_tree "$WORK/root" || die "niebezpieczna kopia: $ZI_ERROR"
    for m in config data "releases/$bversion"; do
        [[ -d "$src/$m" && ! -L "$src/$m" ]] || die "$m w kopii nie jest katalogiem"
    done
    # Root przepisuje pliki w config/ po przywróceniu: tam tylko zwykłe pliki i katalogi.
    [[ -z "$(find "$src/config" ! -type f ! -type d -print -quit)" ]] || die "niebezpieczna kopia: dowiązania albo pliki specjalne w config/"
    [[ -f "$src/config/zdrowieinfo.env" ]] || die "kopia nie ma config/zdrowieinfo.env"
    [[ -f "$src/releases/$bversion/ZdrowieInfo.Server" && ! -L "$src/releases/$bversion/ZdrowieInfo.Server" ]] ||
        die "program w kopii nie ma ZdrowieInfo.Server"

    echo "Kopia:   $(basename "$file")"
    sed 's/^/         /' "$WORK/manifest.txt"
    echo "Ten serwer: ZdrowieInfo $(current_version)"
    echo
    echo "Przywrócenie ZASTĘPUJE bazę, konfigurację, klucze, dane i program tego serwera tymi z kopii."
    echo "Zostają ustawienia połączenia z bazą, update.sh, backup.sh i jednostki systemd tego serwera."
    if [[ $yes -eq 0 ]]; then
        local answer
        read -r -p "Wpisz 'przywroc', aby kontynuować: " answer </dev/tty || die "nie potwierdzono"
        [[ "$answer" == przywroc ]] || die "nie potwierdzono"
    fi

    if [[ $safety -eq 1 ]]; then
        info "Najpierw kopia stanu bieżącego"
        bash "${BASH_SOURCE[0]}" create --output "$ZI_ROOT/backups" || die "kopia bezpieczeństwa nieudana; nic nie zmieniono"
    fi

    load_db_env
    local keep_cs keep_env keep_state
    keep_cs="$(grep -m1 '^ConnectionStrings__ZdrowieInfo=' "$CONFIG" || true)"
    keep_env="$(cat "$UPDATE_ENV")"
    keep_state="$(cat "$STATE" 2>/dev/null || true)"

    info "Zatrzymywanie ZdrowieInfo"
    systemctl stop zdrowieinfo-update.path zdrowieinfo-backup.timer zdrowieinfo.service 2>/dev/null || true

    info "Przywracanie plików"
    rm -rf -- "${ZI_ROOT:?}/config" "${ZI_ROOT:?}/data"
    cp -a -- "$src/config" "$ZI_ROOT/config"
    cp -a -- "$src/data" "$ZI_ROOT/data"
    mkdir -p "$ZI_ROOT/data/updates"
    rm -rf -- "${ZI_ROOT:?}/releases/$bversion"
    mkdir -p "$ZI_ROOT/releases"
    cp -a -- "$src/releases/$bversion" "$ZI_ROOT/releases/$bversion"
    ln -sfn "releases/$bversion" "$ZI_ROOT/.current.new" && mv -T -- "$ZI_ROOT/.current.new" "$ZI_ROOT/current"

    # Ustawienia bazy tego serwera (kopia z innego serwera ma inne hasło).
    printf '%s\n' "$keep_env" | write_root_file "$UPDATE_ENV" root:root 600
    if [[ -n "$keep_cs" ]]; then
        awk -v line="$keep_cs" 'index($0, "ConnectionStrings__ZdrowieInfo=") == 1 && !done { print line; done = 1; next } { print }' "$CONFIG" |
            write_root_file "$CONFIG" root:root 600
    fi
    # install.state: wartości z kopii (adres, port), gdy są poprawne; ustawienia bazy i brakujące – tego serwera.
    local restored_state="" line k v new_state=""
    if [[ -f "$STATE" ]]; then restored_state="$(cat "$STATE")"; fi
    while IFS= read -r line; do
        [[ "$line" == *=* ]] || continue
        k="${line%%=*}"; v="${line#*=}"
        case "$k" in
            DB_MODE|DB_NAME|DB_USER|DB_PORT|DB_CREATED) continue ;;
            PORT) [[ "$v" =~ ^[0-9]{1,5}$ ]] || continue ;;
            [A-Z_]*) [[ "$k" =~ ^[A-Z_]+$ && "$v" =~ ^[A-Za-z0-9.:/_-]*$ ]] || continue ;;
            *) continue ;;
        esac
        new_state+="$line"$'\n'
    done <<<"$restored_state"
    for k in DB_MODE DB_NAME DB_USER DB_PORT DB_CREATED PORT LISTEN PUBLIC_URL; do
        [[ "$new_state" == *$'\n'"$k="* || "$new_state" == "$k="* ]] && continue
        v="$(printf '%s\n' "$keep_state" | sed -n "s/^$k=//p" | head -n1)"
        [[ -z "$v" ]] || new_state+="$k=$v"$'\n'
    done
    printf '%s' "$new_state" | write_root_file "$STATE" root:root 600
    # Sprawdzanie /healthz na porcie z przywróconej konfiguracji.
    local port
    port="$(state_value "$STATE" PORT)"
    if [[ "$port" =~ ^[0-9]{1,5}$ ]]; then
        sed "s|^HEALTH_URL=.*|HEALTH_URL=http://127.0.0.1:$port/healthz|" "$UPDATE_ENV" | write_root_file "$UPDATE_ENV" root:root 600
    fi

    # Właściciele i prawa jak po install.sh; bez setuid i setgid (dowiązania nie są śledzone).
    chown -hR root:root "$ZI_ROOT/config"; chmod 700 "$ZI_ROOT/config"
    find "$ZI_ROOT/config" -type f -exec chmod 600 {} +
    chown -hR "$SERVICE_USER:$SERVICE_USER" "$ZI_ROOT/data"; chmod 700 "$ZI_ROOT/data"
    chmod -R ug-s "$ZI_ROOT/data"
    chown -hR root:root "$ZI_ROOT/releases/$bversion"; chmod -R go-w,ug-s "$ZI_ROOT/releases/$bversion"

    info "Przywracanie bazy $PGDATABASE"
    load_db_env
    # Schemat public zawiera też rozszerzenie unaccent i konfigurację pl_unaccent – pg_restore odtwarza je z kopii.
    pg psql --no-password -X -v ON_ERROR_STOP=1 -q -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' ||
        die "nie można wyczyścić schematu bazy (rola musi być właścicielem bazy)"
    pg pg_restore --no-password --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" "$WORK/database.dump" ||
        die "pg_restore nieudany; kopia bezpieczeństwa jest w $ZI_ROOT/backups"

    info "Uruchamianie ZdrowieInfo $bversion"
    systemctl daemon-reload
    systemctl enable zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-backup.timer >/dev/null 2>&1 || true
    systemctl start zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-backup.timer
    load_db_env
    local i
    for ((i = 0; i < 40; i++)); do
        if curl -fs --max-time 5 "$HEALTH_URL" >/dev/null 2>&1; then
            info "Przywrócono. Działa ZdrowieInfo $bversion."
            return 0
        fi
        sleep 3
    done
    warn "serwer jeszcze nie odpowiada na $HEALTH_URL: journalctl -u zdrowieinfo"
}

do_list() {
    ls -lh "$ZI_ROOT"/backups/*.tar.gz 2>/dev/null || echo "Brak kopii w $ZI_ROOT/backups."
}

case "${1-}" in
    create) shift; do_create "$@" ;;
    restore) shift; do_restore "$@" ;;
    list) do_list ;;
    -h|--help|"") usage ;;
    *) die "nieznane polecenie: $1 (zob. --help)" ;;
esac
