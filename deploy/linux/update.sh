#!/usr/bin/env bash
# Aktualizacja ZdrowieInfo (z panelu albo install.sh --upgrade).
#
# Uruchamia go jako root zdrowieinfo-update.service, gdy serwer zapisze /opt/zdrowieinfo/data/updates/request.conf
# (pilnuje tego zdrowieinfo-update.path). install.sh instaluje ten skrypt jako /opt/zdrowieinfo/bin/update.sh (root).
# Nigdy nie jest uruchamiany z instalowanej paczki i nic z paczki nie działa jako root: migracje działają jako
# użytkownik zdrowieinfo.
#
# Zlecenie (zapisuje serwer, klucz=wartość, czytane bez eval; każda wartość sprawdzana):
#   format=1  run_id=<guid>  version=<x.y.z>  from_version=<x.y.z>  arch=x64|arm64
#   file=ZdrowieInfo.Server-<wersja>-linux-<arch>.tar.gz  sha256=<64 hex>
# Paczka i SHA256SUMS.txt leżą w /opt/zdrowieinfo/data/updates/<wersja>/.
# Wynik (czyta serwer przy starcie): /opt/zdrowieinfo/data/updates/result.conf, zapisywany przez użytkownika usługi
# (root nigdy nie otwiera do zapisu ścieżki w jego katalogach)
#   format=1  run_id  version  from_version  state=Succeeded|RolledBack|Failed  message  finished_at
# Kopia bazy przed aktualizacją: /opt/zdrowieinfo/backups/pre-update/ (root:root 700).
#
# Potrzebuje /opt/zdrowieinfo/config/update.env (root:root 600, zapisuje install.sh), linie klucz=wartość:
#   PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE  (pg_dump / pg_restore)
#   HEALTH_URL (np. http://127.0.0.1:5180/healthz), opcjonalnie PG_BIN (katalog pg_dump zgodnego z serwerem)
# oraz /opt/zdrowieinfo/config/zdrowieinfo.env (root:root 600) – konfiguracja serwera dla migracji.
#
# Użycie: update.sh              wykonuje oczekujące zlecenie (to uruchamia jednostka systemd)
#         update.sh --self-test  wbudowane testy funkcji sprawdzających (bez roota)
set -Eeuo pipefail

ZI_ROOT="${ZI_ROOT:-/opt/zdrowieinfo}"
ZI_USER="${ZI_USER:-zdrowieinfo}"
ZI_SERVICE="zdrowieinfo.service"
HEALTH_TIMEOUT_SECONDS=120
KEEP_RELEASES=3
MAX_REQUEST_BYTES=4096

# ------------------------------------------------------------------------------------------------ functions
# Funkcje poniżej nie mają skutków ubocznych (chyba że opisano inaczej), więc testy mogą wczytać ten plik (main działa
# tylko przy uruchomieniu, nie przy source). Wspólne z install.sh i backup.sh.

zi_log() { printf 'update.sh: %s\n' "$*" >&2; }

# Tylko zwykłe x.y.z (ta sama reguła co AppVersion.IsPlainTriplet).
zi_is_plain_version() { [[ "${1-}" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]]; }

# zi_version_cmp A B -> wypisuje -1, 0 albo 1. Obie muszą być zwykłymi wersjami.
zi_version_cmp() {
    local a="$1" b="$2" i
    zi_is_plain_version "$a" && zi_is_plain_version "$b" || return 2
    local -a pa pb
    IFS=. read -r -a pa <<<"$a"
    IFS=. read -r -a pb <<<"$b"
    for i in 0 1 2; do
        if ((10#${pa[i]} > 10#${pb[i]})); then echo 1; return 0; fi
        if ((10#${pa[i]} < 10#${pb[i]})); then echo -1; return 0; fi
    done
    echo 0
}

# Prawda, gdy A jest nowsza niż B.
zi_version_gt() { local c; c="$(zi_version_cmp "$1" "$2")" || return 1; [[ "$c" == 1 ]]; }

zi_is_guid() { [[ "${1-}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; }
zi_is_sha256() { [[ "${1-}" =~ ^[0-9a-f]{64}$ ]]; }
zi_is_arch() { [[ "${1-}" == x64 || "${1-}" == arm64 ]]; }

zi_machine_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo x64 ;;
        aarch64|arm64) echo arm64 ;;
        *) return 1 ;;
    esac
}

zi_tarball_name() { printf 'ZdrowieInfo.Server-%s-linux-%s.tar.gz' "$1" "$2"; }

# zi_parse_request PLIK: ustawia REQ_FORMAT REQ_RUN_ID REQ_VERSION REQ_FROM REQ_ARCH REQ_FILE REQ_SHA256.
# Nieznane klucze pomijane, nic nie jest wykonywane. Zwraca 1, gdy wartości brak albo jest niepoprawna.
zi_parse_request() {
    local file="$1" key value
    REQ_FORMAT="" REQ_RUN_ID="" REQ_VERSION="" REQ_FROM="" REQ_ARCH="" REQ_FILE="" REQ_SHA256=""
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* ]] && continue
        [[ "$line" == *=* ]] || continue
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            format) REQ_FORMAT="$value" ;;
            run_id) REQ_RUN_ID="$value" ;;
            version) REQ_VERSION="$value" ;;
            from_version) REQ_FROM="$value" ;;
            arch) REQ_ARCH="$value" ;;
            file) REQ_FILE="$value" ;;
            sha256) REQ_SHA256="$value" ;;
        esac
    done <"$file"
    [[ "$REQ_FORMAT" == 1 ]] || { ZI_ERROR="nieobsługiwany format zlecenia"; return 1; }
    zi_is_guid "$REQ_RUN_ID" || { REQ_RUN_ID=""; ZI_ERROR="niepoprawne run_id"; return 1; }
    zi_is_plain_version "$REQ_VERSION" || { REQ_VERSION=""; ZI_ERROR="wersja nie ma postaci x.y.z"; return 1; }
    [[ -z "$REQ_FROM" ]] || zi_is_plain_version "$REQ_FROM" || { REQ_FROM=""; ZI_ERROR="niepoprawne from_version"; return 1; }
    zi_is_arch "$REQ_ARCH" || { ZI_ERROR="niepoprawna architektura"; return 1; }
    zi_is_sha256 "$REQ_SHA256" || { ZI_ERROR="niepoprawny sha256"; return 1; }
    # Nazwa pliku wynika z wersji i architektury; zlecenie musi się z nią zgadzać.
    [[ "$REQ_FILE" == "$(zi_tarball_name "$REQ_VERSION" "$REQ_ARCH")" ]] || { ZI_ERROR="nieoczekiwana nazwa pliku"; return 1; }
    return 0
}

# zi_read_env_file PLIK: linie klucz=wartość z update.env do zmiennych (tylko znane klucze, bez eval).
zi_read_env_file() {
    local file="$1" key value
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && continue
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE) export "$key=$value" ;;
            HEALTH_URL) HEALTH_URL="$value" ;;
            PG_BIN) PG_BIN="$value" ;;
        esac
    done <"$file"
}

# zi_sha256 PLIK -> hex małymi literami
zi_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum -- "$1" | cut -d' ' -f1
    else shasum -a 256 -- "$1" | cut -d' ' -f1; fi
}

# zi_sums_lookup SUMY NAZWA -> skrót NAZWY z pliku w formacie sha256sum ("<hex>  nazwa" albo "<hex> *nazwa").
zi_sums_lookup() {
    local sums="$1" name="$2" hash rest
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ "$line" =~ ^([0-9a-fA-F]{64})\ [\ \*](.+)$ ]] || continue
        hash="${BASH_REMATCH[1]}"
        rest="${BASH_REMATCH[2]}"
        rest="${rest#./}"
        if [[ "$rest" == "$name" ]]; then printf '%s\n' "$hash" | tr 'A-F' 'a-f'; return 0; fi
    done <"$sums"
    return 1
}

# zi_check_member_names: nazwy wpisów archiwum (wynik tar -t) na stdin. Odrzuca ścieżki bezwzględne, każdy składnik
# ".." i puste nazwy. Nazwa z nową linią to dwie linie – każda jest sprawdzana, więc taki trik kończy się odmową.
zi_check_member_names() {
    local name n=0
    while IFS= read -r name || [[ -n "$name" ]]; do
        n=$((n + 1))
        if [[ -z "$name" ]]; then ZI_ERROR="pusta nazwa w archiwum"; return 1; fi
        if [[ "$name" == /* ]]; then ZI_ERROR="ścieżka bezwzględna w paczce: $name"; return 1; fi
        case "/$name/" in
            */../*) ZI_ERROR="'..' w ścieżce paczki: $name"; return 1 ;;
        esac
        if [[ "$name" == *\\* ]]; then ZI_ERROR="ukośnik wsteczny w ścieżce paczki: $name"; return 1; fi
    done
    [[ $n -gt 0 ]] || { ZI_ERROR="pusta paczka"; return 1; }
}

# zi_check_member_types: wynik "tar -tv" na stdin. Dozwolone tylko zwykłe pliki, katalogi i dowiązania symboliczne
# (sprawdzane po rozpakowaniu): bez twardych dowiązań, urządzeń, FIFO i gniazd.
zi_check_member_types() {
    local line n=0
    while IFS= read -r line || [[ -n "$line" ]]; do
        n=$((n + 1))
        case "${line:0:1}" in
            -|d|l) ;;
            *) ZI_ERROR="nieobsługiwany typ wpisu w paczce: ${line:0:1}"; return 1 ;;
        esac
    done
    [[ $n -gt 0 ]] || { ZI_ERROR="pusta paczka"; return 1; }
}

# zi_check_member_modes: wynik "tar -tv" na stdin. Odrzuca wpisy z setuid i setgid (kopia ich nie potrzebuje,
# a root odtworzyłby je tak, jak są).
zi_check_member_modes() {
    local line perm
    while IFS= read -r line || [[ -n "$line" ]]; do
        perm="${line%% *}"
        case "${perm:3:1}${perm:6:1}" in
            *s*|*S*) ZI_ERROR="wpis z setuid albo setgid: ${line##* }"; return 1 ;;
        esac
    done
    return 0
}

# zi_normalize_relpath ŚCIEŻKA: rozwija "." i ".." ścieżki względnej (tekstowo). Wypisuje wynik (pusty dla samego
# korzenia). Błąd, gdy ścieżka wychodzi ponad korzeń.
zi_normalize_relpath() {
    local path="$1" part out=() parts=()
    IFS=/ read -r -a parts <<<"$path"
    for part in "${parts[@]}"; do
        case "$part" in
            ''|.) ;;
            ..) [[ ${#out[@]} -gt 0 ]] || return 1; unset 'out[${#out[@]}-1]' ;;
            *) out+=("$part") ;;
        esac
    done
    local IFS=/
    printf '%s' "${out[*]}"
}

# zi_check_tree KATALOG: po rozpakowaniu. Tylko pliki, katalogi i dowiązania; bez twardych dowiązań; każde dowiązanie
# jest względne i zostaje w KATALOGU: sprawdzane na tekście celu i jeszcze raz przez "realpath -m" względem
# prawdziwej ścieżki KATALOGU, gdzie jest GNU realpath (podwójna ochrona; macOS nie ma -m).
zi_check_tree() {
    local dir="$1" link rel target linkdir real="" resolved
    if [[ -n "$(find "$dir" ! -type f ! -type d ! -type l -print -quit)" ]]; then
        ZI_ERROR="pliki specjalne w paczce"; return 1
    fi
    if [[ -n "$(find "$dir" -type f -links +1 -print -quit)" ]]; then
        ZI_ERROR="twarde dowiązania w paczce"; return 1
    fi
    if realpath -m / >/dev/null 2>&1; then
        real="$(realpath -e -- "$dir")" || { ZI_ERROR="nie można ustalić ścieżki $dir"; return 1; }
    fi
    while IFS= read -r -d '' link; do
        target="$(readlink "$link")"
        rel="${link#"$dir"/}"
        if [[ -z "$target" || "$target" == /* ]]; then ZI_ERROR="dowiązanie z celem bezwzględnym: $rel"; return 1; fi
        linkdir="$(dirname "$rel")"
        [[ "$linkdir" == . ]] && linkdir=""
        if ! zi_normalize_relpath "${linkdir:+$linkdir/}$target" >/dev/null; then
            ZI_ERROR="dowiązanie poza paczkę: $rel"; return 1
        fi
        if [[ -n "$real" ]]; then
            resolved="$(realpath -m -- "$link" 2>/dev/null)" || { ZI_ERROR="nie można ustalić celu dowiązania: $rel"; return 1; }
            [[ "$resolved" == "$real" || "$resolved" == "$real"/* ]] || { ZI_ERROR="dowiązanie poza paczkę: $rel"; return 1; }
        fi
    done < <(find "$dir" -type l -print0)
    return 0
}

# zi_safe_unpack PACZKA CEL: sprawdza i rozpakowuje do CELU (nie może istnieć). Jako root – pliki należą do roota.
zi_safe_unpack() {
    local tarball="$1" dest="$2" listing
    [[ ! -e "$dest" && ! -L "$dest" ]] || { ZI_ERROR="$dest już istnieje"; return 1; }
    listing="$(tar -tzf "$tarball")" || { ZI_ERROR="nie można odczytać paczki"; return 1; }
    zi_check_member_names <<<"$listing" || return 1
    listing="$(tar -tvzf "$tarball")" || { ZI_ERROR="nie można odczytać paczki"; return 1; }
    zi_check_member_types <<<"$listing" || return 1
    mkdir -m 700 -- "$dest" || { ZI_ERROR="nie można utworzyć $dest"; return 1; }
    if ! tar --no-same-owner --no-same-permissions -xzf "$tarball" -C "$dest"; then
        ZI_ERROR="rozpakowanie nieudane"; return 1
    fi
    zi_check_tree "$dest" || return 1
    if [[ $EUID -eq 0 ]]; then
        chown -hR root:root -- "$dest" || { ZI_ERROR="chown nieudany"; return 1; }
    fi
    # Dowiązania sprawdzone wyżej; chmod -R nie podąża za nimi poniżej katalogu głównego.
    chmod -R ug-s,u+rwX,go+rX,go-w "$dest" || { ZI_ERROR="chmod nieudany"; return 1; }
    chmod 755 "$dest"
}

# zi_releases_to_prune KATALOG ILE BIEŻĄCA [INNE...]: wypisuje katalogi wersji do usunięcia. Zostawia ILE najnowszych
# oraz zawsze BIEŻĄCĄ i INNE. Katalogi, które nie są wersjami, zostają.
zi_releases_to_prune() {
    local dir="$1" keep="$2"
    shift 2
    local -a protect=("$@") versions=()
    local v p skip
    for v in "$dir"/*; do
        [[ -d "$v" && ! -L "$v" ]] || continue
        v="$(basename "$v")"
        zi_is_plain_version "$v" && versions+=("$v")
    done
    [[ ${#versions[@]} -gt 0 ]] || return 0
    local sorted
    sorted="$(printf '%s\n' "${versions[@]}" | sort -t. -k1,1nr -k2,2nr -k3,3nr)"
    local i=0
    while IFS= read -r v; do
        i=$((i + 1))
        [[ $i -le $keep ]] && continue
        skip=0
        for p in "${protect[@]}"; do [[ "$p" == "$v" ]] && skip=1; done
        [[ $skip -eq 1 ]] || printf '%s\n' "$v"
    done <<<"$sorted"
}

zi_one_line() { printf '%s' "$*" | tr '\r\n\t' '   ' | cut -c1-1500; }

# zi_trusted_dir KATALOG: ustawia ZI_TRUSTED_DIR na prawdziwą ścieżkę KATALOGU. Błąd, chyba że to katalog (nie
# dowiązanie), który może zmieniać tylko root: on i każdy katalog wyżej należą do roota i nie są zapisywalne dla grupy
# ani innych (katalog ze sticky bitem, np. /tmp, jest dozwolony wyżej, nigdy jako KATALOG). Root może wtedy tworzyć
# w nim pliki bez wyścigów. Wymaga GNU stat.
# shellcheck disable=SC2034 # ZI_TRUSTED_DIR czytają wywołujący (backup.sh)
zi_trusted_dir() {
    local dir="$1" path cur uid mode first=1
    ZI_TRUSTED_DIR=""
    while [[ "$dir" == */ && "$dir" != / ]]; do dir="${dir%/}"; done
    [[ -d "$dir" && ! -L "$dir" ]] || { ZI_ERROR="$dir nie jest katalogiem (albo jest dowiązaniem)"; return 1; }
    path="$(realpath -e -- "$dir")" || { ZI_ERROR="nie można ustalić ścieżki $dir"; return 1; }
    cur="$path"
    while true; do
        read -r uid mode < <(stat -c '%u %a' -- "$cur") || { ZI_ERROR="nie można odczytać właściciela $cur"; return 1; }
        [[ "$uid" == 0 ]] || { ZI_ERROR="$cur nie należy do roota"; return 1; }
        if (((8#$mode & 8#022) != 0)) && { ((first)) || (((8#$mode & 8#1000) == 0)); }; then
            ZI_ERROR="$cur może zapisywać ktoś inny niż root"; return 1
        fi
        [[ "$cur" == / ]] && break
        cur="$(dirname -- "$cur")"
        first=0
    done
    ZI_TRUSTED_DIR="$path"
}

# zi_root_dir KATALOG [replace-link]: KATALOG jako root:root 700 (tworzony, gdy go brak). Jego rodzica może już zmieniać
# tylko root. Dowiązanie w miejscu KATALOGU – odmowa, a z replace-link usunięcie i zastąpienie katalogiem. Zmieniany jest
# tylko sam KATALOG; dowiązania nigdy nie są śledzone.
zi_root_dir() {
    local dir="$1"
    if [[ -L "$dir" ]]; then
        [[ "${2-}" == replace-link ]] || { ZI_ERROR="$dir jest dowiązaniem; zamień je na katalog"; return 1; }
        rm -f -- "$dir" || { ZI_ERROR="nie można usunąć dowiązania $dir"; return 1; }
    fi
    if [[ ! -e "$dir" ]]; then mkdir -m 700 -- "$dir" || { ZI_ERROR="nie można utworzyć $dir"; return 1; }; fi
    [[ -d "$dir" && ! -L "$dir" ]] || { ZI_ERROR="$dir nie jest katalogiem"; return 1; }
    { chown -h root:root -- "$dir" && chmod 700 -- "$dir"; } || { ZI_ERROR="nie można ustawić właściciela $dir"; return 1; }
    [[ ! -L "$dir" && "$(stat -c '%u %a' -- "$dir")" == "0 700" ]] || { ZI_ERROR="$dir nie jest root:root 700"; return 1; }
}

# ------------------------------------------------------------------------------------------------ główna część (root)

zi_write_result() {
    # data/updates należy do użytkownika usługi, więc to on zapisuje plik (plik tymczasowy + zmiana nazwy): root nigdy
    # nie otwiera tam ścieżki do zapisu, a podłożone dowiązanie albo FIFO sięga tylko tam, gdzie ten użytkownik.
    local state="$1" message="$2" dir="$ZI_ROOT/data/updates"
    local now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    # shellcheck disable=SC2016 # $1 i $t rozwija wewnętrzna powłoka
    if ! printf 'format=1\nrun_id=%s\nversion=%s\nfrom_version=%s\nstate=%s\nmessage=%s\nfinished_at=%s\n' \
            "${REQ_RUN_ID-}" "${REQ_VERSION-}" "${CURRENT_VERSION:-${REQ_FROM-}}" "$state" "$(zi_one_line "$message")" "$now" |
        runuser -u "$ZI_USER" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 /bin/sh -c \
            'umask 077; t="$(mktemp "$1/.result.XXXXXX")" || exit 1; if cat >"$t" && mv -fT -- "$t" "$1/result.conf"; then exit 0; fi; rm -f -- "$t"; exit 1' \
            sh "$dir"; then
        zi_log "nie można zapisać $dir/result.conf"
        return 0
    fi
    zi_log "wynik: $state – $message"
}

zi_health_ok() {
    # Działa = /healthz odpowiada i podaje oczekiwaną wersję.
    local expected="$1" body
    body="$(curl -fsk --max-time 5 -- "$HEALTH_URL" 2>/dev/null)" || return 1
    [[ "$body" == *"\"version\":\"$expected\""* ]]
}

zi_wait_healthy() {
    local expected="$1" timeout="$2" deadline
    deadline=$((SECONDS + timeout))
    while ((SECONDS < deadline)); do
        if zi_health_ok "$expected"; then return 0; fi
        sleep 3
    done
    return 1
}

zi_pg() { local tool="$1"; shift; if [[ -n "${PG_BIN-}" && -x "$PG_BIN/$tool" ]]; then "$PG_BIN/$tool" "$@"; else "$tool" "$@"; fi; }

# zi_prepare_backup_dir: backups/ i backups/pre-update/ jako root:root 700 – pisze tam tylko root, a zrzut powstaje bez
# wyścigów. Dowiązanie w miejscu backups/ – odmowa (mógł je tam umieścić tylko root); w miejscu pre-update/ – zastąpione.
zi_prepare_backup_dir() {
    local top="$ZI_ROOT/backups"
    BACKUP_DIR="$top/pre-update"
    zi_root_dir "$top" || return 1
    zi_root_dir "$BACKUP_DIR" replace-link
}

zi_backup_db() {
    # Do katalogu tylko dla roota z zi_prepare_backup_dir; nazwa nie może jeszcze istnieć, nawet jako dowiązanie.
    local stamp name
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    name="zdrowieinfo-${CURRENT_VERSION}-do-${REQ_VERSION}-$stamp.dump"
    BACKUP_FILE="$BACKUP_DIR/$name"
    [[ -n "$BACKUP_DIR" && -d "$BACKUP_DIR" && ! -L "$BACKUP_DIR" && "$(stat -c %u -- "$BACKUP_DIR")" == 0 ]] || return 1
    [[ ! -e "$BACKUP_FILE" && ! -L "$BACKUP_FILE" ]] || return 1
    ( set -o noclobber; umask 077; zi_pg pg_dump --format=custom --no-password >"$BACKUP_FILE" )
}

zi_restore_db() {
    [[ -n "${BACKUP_FILE-}" && -f "$BACKUP_FILE" && ! -L "$BACKUP_FILE" ]] || return 1
    # Rola jest właścicielem bazy (sprawdza install.sh), więc może usunąć i odtworzyć schemat public. Rozszerzenie unaccent
    # i konfiguracja pl_unaccent są w tym schemacie – wracają z kopii (pg_restore odtwarza CREATE EXTENSION).
    zi_pg psql --no-password -v ON_ERROR_STOP=1 -q -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' &&
        zi_pg pg_restore --no-password --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" "$BACKUP_FILE"
}

zi_switch_current() {
    local target="$1" tmp="$ZI_ROOT/.current.$$"
    rm -f -- "$tmp"
    ln -s -- "$target" "$tmp"
    mv -Tf -- "$tmp" "$ZI_ROOT/current"
}

# zi_server_env FILE: KEY=VALUE z pliku konfiguracji (zdrowieinfo.env) do tablicy ZI_SERVER_ENV, linia po linii, bez
# eval – wartości ze spacjami i znakami specjalnymi zostają dosłowne. Komentarze i puste linie pomijane.
zi_server_env() {
    local file="$1" line
    ZI_SERVER_ENV=()
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* ]] && continue
        [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || continue
        ZI_SERVER_ENV+=("$line")
    done <"$file"
}

zi_run_as_service_user() {
    # Program serwera danej wersji jako użytkownik usługi, z katalogu wersji, z konfiguracją produkcyjną.
    local release="$1"
    shift
    zi_server_env "$ZI_ROOT/config/zdrowieinfo.env"
    (cd "$release" && runuser -u "$ZI_USER" -- env -i \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        HOME=/nonexistent LANG=C.UTF-8 \
        ASPNETCORE_ENVIRONMENT=Production \
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        "${ZI_SERVER_ENV[@]}" \
        "$release/ZdrowieInfo.Server" "$@")
}

zi_rollback() {
    local why="$1" state=RolledBack note=""
    zi_log "wycofywanie: $why"
    systemctl stop "$ZI_SERVICE" || true
    if [[ "$PHASE" == migrated || "$PHASE" == switched ]]; then
        if zi_restore_db; then note="baza przywrócona z $(basename "$BACKUP_FILE")"
        else state=Failed; note="PRZYWRÓCENIE BAZY NIEUDANE, kopia: ${BACKUP_FILE-brak}"; fi
    fi
    if [[ -n "${PREVIOUS_TARGET-}" ]]; then zi_switch_current "$PREVIOUS_TARGET"; fi
    # Wynik przed startem starej wersji – znajdzie go przy uruchomieniu.
    zi_write_result "$state" "$why; przywrócono wersję $CURRENT_VERSION. $note"
    systemctl start --no-block "$ZI_SERVICE" || true
    if ! zi_wait_healthy "$CURRENT_VERSION" 60; then zi_log "poprzednia wersja $CURRENT_VERSION też nie odpowiada"; fi
    PHASE="done"
}

zi_fail() {
    # Przed zatrzymaniem usługi nic się nie zmieniło – tylko wynik. Po – wycofanie (w miarę możliwości).
    local why="$1"
    trap - ERR
    set +e
    if [[ "$PHASE" == stopped || "$PHASE" == migrated || "$PHASE" == switched ]]; then
        zi_rollback "$why"
    elif [[ "$PHASE" != "done" ]]; then
        zi_write_result Failed "$why"
        if [[ "$PHASE" == prepared ]]; then systemctl start --no-block "$ZI_SERVICE" 2>/dev/null || true; fi
        PHASE="done"
    fi
    exit 1
}

zi_on_error() { local line="$1"; trap - ERR; zi_fail "nieoczekiwany błąd update.sh w linii $line"; }

zi_main() {
    PHASE=init
    REQ_RUN_ID="" REQ_VERSION="" REQ_FROM="" CURRENT_VERSION="" PREVIOUS_TARGET="" BACKUP_FILE="" BACKUP_DIR=""
    [[ $EUID -eq 0 ]] || { zi_log "uruchom jako root"; exit 1; }
    umask 022

    local updates="$ZI_ROOT/data/updates" request="$ZI_ROOT/data/updates/request.conf"
    exec 9>/run/zdrowieinfo-update.lock
    flock -n 9 || { zi_log "trwa inna aktualizacja"; exit 0; }
    if [[ ! -e "$request" && ! -L "$request" ]]; then zi_log "brak zlecenia aktualizacji"; exit 0; fi

    # Katalog roboczy na dysku, w releases/ roota (/run to mały tmpfs; paczka ma ok. 60 MB).
    [[ -d "$ZI_ROOT/releases" && ! -L "$ZI_ROOT/releases" ]] || { zi_log "brak $ZI_ROOT/releases"; exit 1; }
    rm -rf -- "$ZI_ROOT"/releases/.work.*
    WORK="$(mktemp -d "$ZI_ROOT/releases/.work.XXXXXX")"
    trap 'rm -rf -- "$WORK"' EXIT
    trap 'zi_on_error $LINENO' ERR

    # Najpierw zabrać zlecenie: jednostka path reaguje ponownie, dopóki request.conf istnieje.
    local size
    # Dowiązanie w miejscu data/updates jest usuwane samo (unlink nie podąża za nim), nigdy to, na co wskazuje.
    if [[ -L "$updates" || ! -d "$updates" ]]; then [[ ! -L "$updates" ]] || rm -f -- "$updates"; zi_fail "$updates nie jest katalogiem"; fi
    if [[ -L "$request" || ! -f "$request" ]]; then rm -f -- "$request"; zi_fail "zlecenie nie jest zwykłym plikiem"; fi
    size="$(stat -c %s -- "$request")"
    if ((size > MAX_REQUEST_BYTES)); then rm -f -- "$request"; zi_fail "plik zlecenia jest za duży"; fi
    cp -- "$request" "$WORK/request.conf"
    rm -f -- "$request"

    ZI_ERROR=""
    zi_parse_request "$WORK/request.conf" || zi_fail "niepoprawne zlecenie: $ZI_ERROR"

    local env_file="$ZI_ROOT/config/update.env"
    [[ -f "$env_file" && ! -L "$env_file" ]] || zi_fail "brak $env_file (uruchom install.sh --repair)"
    [[ "$(stat -c %u -- "$env_file")" == 0 ]] || zi_fail "$env_file nie należy do roota"
    HEALTH_URL="" PG_BIN=""
    zi_read_env_file "$env_file"
    [[ -n "$HEALTH_URL" && -n "${PGDATABASE-}" ]] || zi_fail "$env_file nie ma HEALTH_URL albo PGDATABASE"
    # Konfiguracja serwera jest potrzebna do migracji – sprawdzana przed zatrzymaniem usługi.
    local server_env="$ZI_ROOT/config/zdrowieinfo.env"
    [[ -f "$server_env" && ! -L "$server_env" && "$(stat -c %u -- "$server_env")" == 0 ]] ||
        zi_fail "brak $server_env albo nie należy do roota (uruchom install.sh --repair)"

    # Wersje: zwykłe x.y.z i tylko nowsza niż działająca.
    PREVIOUS_TARGET="$(readlink -- "$ZI_ROOT/current")" || zi_fail "$ZI_ROOT/current nie jest dowiązaniem"
    CURRENT_VERSION="$(basename -- "$PREVIOUS_TARGET")"
    zi_is_plain_version "$CURRENT_VERSION" || zi_fail "nie można ustalić działającej wersji z $ZI_ROOT/current"
    zi_version_gt "$REQ_VERSION" "$CURRENT_VERSION" || zi_fail "wersja $REQ_VERSION nie jest nowsza niż $CURRENT_VERSION"

    local arch
    arch="$(zi_machine_arch)" || zi_fail "nieobsługiwana architektura procesora $(uname -m)"
    [[ "$arch" == "$REQ_ARCH" ]] || zi_fail "paczka jest dla $REQ_ARCH, ten serwer to $arch"

    # Paczka i sumy kopiowane z katalogu użytkownika usługi przed sprawdzeniem (bez sztuczek z dowiązaniami i podmianą).
    local stage_dir="$updates/$REQ_VERSION" name
    name="$(zi_tarball_name "$REQ_VERSION" "$arch")"
    [[ -d "$stage_dir" && ! -L "$stage_dir" ]] || zi_fail "brak $stage_dir"
    [[ -f "$stage_dir/$name" && ! -L "$stage_dir/$name" ]] || zi_fail "brak paczki $name"
    [[ -f "$stage_dir/SHA256SUMS.txt" && ! -L "$stage_dir/SHA256SUMS.txt" ]] || zi_fail "brak SHA256SUMS.txt"
    cp -- "$stage_dir/$name" "$WORK/$name"
    cp -- "$stage_dir/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt"

    # Skrót jeszcze raz (wykrywa uszkodzenie; plik sum leży w katalogu, w którym pisze użytkownik usługi).
    local actual expected
    actual="$(zi_sha256 "$WORK/$name")"
    expected="$(zi_sums_lookup "$WORK/SHA256SUMS.txt" "$name")" || zi_fail "SHA256SUMS.txt nie ma wpisu dla $name"
    [[ "$actual" == "$expected" && "$actual" == "$REQ_SHA256" ]] || zi_fail "niezgodny SHA-256 pliku $name"

    # Bezpieczne rozpakowanie do releases/<wersja>.
    local releases="$ZI_ROOT/releases" release="$ZI_ROOT/releases/$REQ_VERSION" staging="$ZI_ROOT/releases/.staging-$REQ_VERSION"
    rm -rf -- "$staging"
    ZI_ERROR=""
    zi_safe_unpack "$WORK/$name" "$staging" || { rm -rf -- "$staging"; zi_fail "niebezpieczna albo uszkodzona paczka: $ZI_ERROR"; }
    [[ -f "$staging/ZdrowieInfo.Server" && -x "$staging/ZdrowieInfo.Server" ]] || { rm -rf -- "$staging"; zi_fail "paczka nie ma ZdrowieInfo.Server"; }
    rm -rf -- "$release"
    mv -T -- "$staging" "$release"
    zi_log "rozpakowano $REQ_VERSION do $release"

    # Zatrzymanie, kopia, migracje jako użytkownik usługi, przełączenie, start. Katalog kopii sprawdzany przed zatrzymaniem.
    ZI_ERROR=""
    zi_prepare_backup_dir || zi_fail "katalog kopii: $ZI_ERROR (uruchom install.sh --repair)"
    PHASE=prepared
    systemctl stop "$ZI_SERVICE"
    PHASE=stopped
    zi_backup_db || zi_fail "pg_dump nieudany"
    zi_log "kopia bazy: $BACKUP_FILE"
    PHASE=migrated # od tego miejsca baza mogła się zmienić
    zi_run_as_service_user "$release" --migrate-only || zi_fail "migracje wersji $REQ_VERSION nieudane"
    zi_switch_current "$release"
    PHASE=switched
    # --no-block: przy Type=notify blokujący start czeka do TimeoutStartSec; decyduje 2-minutowe sprawdzanie /healthz.
    systemctl start --no-block "$ZI_SERVICE" || zi_fail "$ZI_SERVICE nie wystartował"
    zi_wait_healthy "$REQ_VERSION" "$HEALTH_TIMEOUT_SECONDS" || zi_fail "wersja $REQ_VERSION nie odpowiedziała w ciągu $HEALTH_TIMEOUT_SECONDS s"
    PHASE="done"
    trap - ERR

    zi_write_result Succeeded "Zaktualizowano z $CURRENT_VERSION do $REQ_VERSION. Kopia bazy: $(basename "$BACKUP_FILE")"

    # Ostatnie wersje zostają do wycofania (nigdy działająca ani poprzednia).
    local old
    while IFS= read -r old; do
        [[ -n "$old" ]] || continue
        zi_log "usuwanie starej wersji $old"
        rm -rf -- "${releases:?}/$old"
    done < <(zi_releases_to_prune "$releases" "$KEEP_RELEASES" "$REQ_VERSION" "$CURRENT_VERSION")
    rm -rf -- "$releases"/.staging-*
    return 0
}

# ------------------------------------------------------------------------------------------------ testy wbudowane

zi_self_test() {
    local fails=0
    t() { if "$@" >/dev/null; then :; else echo "FAIL: $*"; fails=$((fails + 1)); fi; }
    f() { if "$@" >/dev/null 2>&1; then echo "FAIL (expected false): $*"; fails=$((fails + 1)); fi; }
    t zi_is_plain_version 1.2.3
    t zi_is_plain_version 10.20.30
    f zi_is_plain_version 1.2
    f zi_is_plain_version 1.2.3.4
    f zi_is_plain_version 1.2.3-beta
    f zi_is_plain_version v1.2.3
    f zi_is_plain_version "1.2.3 "
    f zi_is_plain_version "1.2.3;rm"
    f zi_is_plain_version ""
    t zi_version_gt 1.2.10 1.2.9
    t zi_version_gt 2.0.0 1.99.99
    f zi_version_gt 1.2.3 1.2.3
    f zi_version_gt 1.2.2 1.2.3
    f zi_version_gt 1.2.3-rc 1.0.0
    t zi_is_guid 0f8fad5b-d9cb-469f-a165-70867728950e
    f zi_is_guid ../../etc
    t zi_is_sha256 "$(printf 'a%.0s' {1..64})"
    f zi_is_sha256 "$(printf 'A%.0s' {1..64})"
    t zi_check_member_names <<<$'./\n./ZdrowieInfo.Server\n./wwwroot/app.css'
    f zi_check_member_names <<<$'./ok\n/etc/passwd'
    f zi_check_member_names <<<$'./ok\n../evil'
    f zi_check_member_names <<<$'./a/../../evil'
    f zi_check_member_names <<<$'a/..'
    t zi_check_member_names <<<$'./a..b/c..'
    t zi_check_member_types <<<$'drwxr-xr-x root/root 0 x ./\n-rw-r--r-- root/root 1 x ./a\nlrwxrwxrwx root/root 0 x ./b -> a'
    f zi_check_member_types <<<$'-rw-r--r-- root/root 1 x ./a\nhrw-r--r-- root/root 0 x ./b link to ./a'
    f zi_check_member_types <<<$'crw-r--r-- root/root 1,3 x ./null'
    t zi_check_member_modes <<<$'drwxr-xr-x root/root 0 x ./\n-rwxr-xr-x root/root 1 x ./a\ndrwxrwxrwt root/root 0 x ./tmp'
    f zi_check_member_modes <<<$'-rwsr-xr-x root/root 1 x ./a'
    f zi_check_member_modes <<<$'-rwxr-Sr-x root/root 1 x ./a'
    f zi_check_member_modes <<<$'drwxr-sr-x root/root 0 x ./d'
    t zi_normalize_relpath a/b/../c
    f zi_normalize_relpath a/../../b
    f zi_normalize_relpath ..
    [[ "$(zi_normalize_relpath ./a/./b/../c)" == a/c ]] || { echo "FAIL: normalize ./a/./b/../c"; fails=$((fails + 1)); }
    if [[ $fails -eq 0 ]]; then echo "self-test: OK"; return 0; fi
    echo "self-test: błędów: $fails"
    return 1
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    case "${1-}" in
        --self-test) zi_self_test ;;
        "") zi_main ;;
        -h|--help) sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//' ;;
        *) zi_log "nieznany argument: $1"; exit 2 ;;
    esac
fi
