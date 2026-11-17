#!/usr/bin/env bash
# Instalator ZdrowieInfo dla Ubuntu 24.04 LTS i nowszych (x64, arm64). Bez Dockera i bez .NET (program jest self-contained).
#
#   sudo bash install.sh                                   interaktywnie: pyta o wszystko
#   sudo bash install.sh --unattended --public-url URL ... bez pytań (automatyczne instalacje)
#   sudo bash install.sh --repair | --upgrade              na zainstalowanym serwerze (ponowne uruchomienie pokazuje menu)
#   sudo bash install.sh --backup [--output ŚCIEŻKA]       kopia: baza, konfiguracja, klucze, dane i program
#   sudo bash install.sh --restore PLIK                    przywraca taką kopię na tym serwerze
#   sudo bash install.sh --set-password [LOGIN]            nowe hasło konta (odblokowuje je)
#   sudo bash install.sh --reset-mfa LOGIN                 awaryjnie: usuwa aplikację uwierzytelniającą i passkey konta
#   sudo bash install.sh --uninstall                       usuwa WSZYSTKO, także bazę (proponuje kopię i pyta o
#                                                          potwierdzenie); --keep-data usuwa tylko program
#
# Skąd paczka:
#   --package PLIK        ZdrowieInfo.Server-<wersja>-linux-<arch>.tar.gz z build/build-server.sh albo z wydania
#                         (sprawdzana z SHA256SUMS.txt obok, jeśli jest)
#   --version X.Y.Z       pobiera to wydanie z GitHub (domyślnie najnowsze), z repozytorium GITHUB_REPOSITORY poniżej.
#                         Bez --package i --version pobierane jest najnowsze wydanie.
#                         --repair używa paczki tylko z jedną z tych opcji: wtedy update.sh, backup.sh i jednostki są
#                         instalowane z niej; bez – zostają zainstalowane.
# Aplikacja:
#   --public-url URL      publiczny adres https:// (bez ścieżki), pod którym działa reverse proxy – wymagany
#   --port N              port Kestrela (domyślnie 5180)
#   --listen ADRES        adres IPv4 nasłuchu (domyślnie 127.0.0.1 – reverse proxy na tym serwerze; 0.0.0.0, gdy na innym)
#   --trusted-proxy IP    adres IPv4 reverse proxy na innej maszynie (nagłówki X-Forwarded-*); można podać kilka razy
#   --contact-email ADRES adres kontaktowy w User-Agencie przy pobieraniu stron źródeł (domyślnie bez adresu)
#   --admin-user LOGIN    pierwsze konto (administrator)
#   --no-pdf              bez TeX Live (ok. 300 MB): raporty bez wersji PDF
# Baza:
#   --db=local|existing   local: PostgreSQL na tym serwerze. Instalowany, gdy go nie ma; istniejący (np. używany przez
#                         inne aplikacje) zostaje, jaki jest: ZdrowieInfo dodaje tylko własną nową rolę i bazę
#                         („zdrowieinfo”, a gdy zajęta – „zdrowieinfo_2”…), nigdy nie instaluje, nie aktualizuje, nie
#                         konfiguruje ani nie restartuje serwera i nie zmienia innych ról i baz.
#                         --db-port wybiera klaster, gdy jest kilka; --db-name ustawia nazwę (musi być wolna).
#   --db-host H --db-port P --db-name N --db-user U --db-sslmode disable|allow|prefer|require|verify-ca|verify-full
# Inne:
#   --yes                 potwierdza --uninstall / --restore bez pytań
#   --backup-to PLIK      --uninstall: najpierw kopia do PLIKU (bez pytań; interaktywnie pyta)
#
# Hasła nigdy nie są argumentami (lista procesów jest widoczna dla innych użytkowników). Źródła, po kolei:
#   --secrets-file PLIK   linie KLUCZ=WARTOŚĆ, plik z prawami 600 (albo 400), właściciel root
#   --secrets-stdin       te same linie na standardowym wejściu
#   środowisko            ZDROWIEINFO_DB_PASSWORD, ZDROWIEINFO_ADMIN_PASSWORD
#   pytanie               ukryte wpisywanie (tryb interaktywny)
# Klucze: DB_PASSWORD (istniejąca baza), ADMIN_PASSWORD (pierwsze konto albo --set-password; min. 12 znaków,
# mała i wielka litera, cyfra).
set -Eeuo pipefail

# Repozytorium GitHub, z którego wydań (workflow .github/workflows/release.yml) korzystają install.sh i aktualizacja
# w panelu (BarPos/ZdrowieInfo). build/build-server.sh wpisuje je w paczkę i w install.sh wydania; w repozytorium puste.
# Puste = bez GitHuba (tylko --package).
GITHUB_REPOSITORY=""

INSTALL_ROOT=/opt/zdrowieinfo
SERVICE_USER=zdrowieinfo
UNIT_DIR=/etc/systemd/system
UNITS=(zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-update.service zdrowieinfo-backup.service zdrowieinfo-backup.timer)
CONFIG_FILE="$INSTALL_ROOT/config/zdrowieinfo.env"
UPDATE_ENV="$INSTALL_ROOT/config/update.env"
STATE_FILE="$INSTALL_ROOT/config/install.state"
LEGACY_ENV=/etc/zdrowieinfo/zdrowieinfo.env
USER_AGENT="ZdrowieInfo-Installer"
TEX_PACKAGES=(texlive-xetex texlive-lang-polish texlive-latex-extra texlive-fonts-recommended fonts-texgyre latexmk)

MODE="" UNATTENDED=0 ASSUME_YES=0
BACKUP_OUTPUT="" RESTORE_FILE="" RESET_USER="" KEEP_DATA=0 BACKUP_TO=""
PACKAGE="" VERSION_ARG=""
DB_MODE="" DB_HOST="" DB_PORT="" DB_NAME="" DB_USER="" DB_SSLMODE="" DB_CREATED=0
PUBLIC_URL="" PORT="" LISTEN="" CONTACT_EMAIL="" ADMIN_USER="" NO_PDF=0
TRUSTED_PROXIES=()
SECRETS_FILE="" SECRETS_STDIN=0
DB_PASSWORD="" ADMIN_PASSWORD=""
ARCH="" PKG_FILE="" PKG_VERSION="" PG_BIN="" WORK=""

# ------------------------------------------------------------------------------------------------ pomocnicze

info() { printf '==> %s\n' "$*"; }
warn() { printf 'UWAGA: %s\n' "$*" >&2; }
die() { printf 'BŁĄD: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '2,46p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

INSTALL_DONE=0
cleanup() {
    if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf -- "$WORK"; fi
    # Nieudana nowa instalacja usuwa rolę i bazę, które sama utworzyła (i tylko je) – kolejna próba zaczyna od czysta,
    # zamiast przechodzić na zdrowieinfo_2.
    if [[ "${DB_CREATED:-0}" -eq 1 && $INSTALL_DONE -eq 0 && "$DB_MODE" == local ]]; then
        warn "instalacja nie skończyła się: usuwam utworzoną bazę '$DB_NAME' i rolę '$DB_USER'"
        runuser -u postgres -- psql -X -q -p "$DB_PORT" -d postgres -c "DROP DATABASE IF EXISTS \"$DB_NAME\" WITH (FORCE)" >/dev/null 2>&1 || true
        runuser -u postgres -- psql -X -q -p "$DB_PORT" -d postgres -c "DROP ROLE IF EXISTS \"$DB_USER\"" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT
trap 'die "install.sh przerwany w linii $LINENO"' ERR

tty_read() { # tty_read ZMIENNA PYTANIE [DOMYŚLNIE]
    local __var="$1" __prompt="$2" __def="${3-}" __ans
    if [[ -n "$__def" ]]; then __prompt="$__prompt [$__def]"; fi
    read -r -p "$__prompt: " __ans </dev/tty || die "brak odpowiedzi"
    printf -v "$__var" '%s' "${__ans:-$__def}"
}

tty_secret() { # tty_secret ZMIENNA PYTANIE [confirm]
    local __var="$1" __prompt="$2" __a __b
    while true; do
        read -r -s -p "$__prompt: " __a </dev/tty || die "brak odpowiedzi"; echo >/dev/tty
        if [[ "${3-}" == confirm ]]; then
            read -r -s -p "Powtórz: " __b </dev/tty || die "brak odpowiedzi"; echo >/dev/tty
            [[ "$__a" == "$__b" ]] || { echo "Wpisy się różnią, spróbuj ponownie." >/dev/tty; continue; }
        fi
        [[ -n "$__a" ]] && break
    done
    printf -v "$__var" '%s' "$__a"
}

ask() { # ask ZMIENNA PYTANIE DOMYŚLNIE: wartość z opcji zostaje; bez pytań – domyślna
    local __var="$1"
    [[ -n "${!__var}" ]] && return 0
    if [[ $UNATTENDED -eq 1 ]]; then printf -v "$__var" '%s' "${3-}"; return 0; fi
    tty_read "$__var" "$2" "${3-}"
}

ask_yes_no() { # ask_yes_no PYTANIE DOMYŚLNIE(t|n) -> status
    local ans
    if [[ $UNATTENDED -eq 1 ]]; then [[ "$2" == t ]]; return; fi
    read -r -p "$1 [$([[ "$2" == t ]] && echo T/n || echo t/N)]: " ans </dev/tty || return 1
    ans="${ans:-$2}"
    [[ "$ans" == [tTyY]* ]]
}

cs_value() { # wartość w connection stringu Npgsql; w cudzysłowie, gdy trzeba (cudzysłowy podwojone)
    local v="$1"
    if [[ "$v" == *[\;\"\'=]* || "$v" != "${v#[[:space:]]}" || "$v" != "${v%[[:space:]]}" ]]; then
        printf '"%s"' "${v//\"/\"\"}"
    else
        printf '%s' "$v"
    fi
}

random_alnum() { # random_alnum DŁUGOŚĆ
    local out=""
    while [[ ${#out} -lt $1 ]]; do out+="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9')"; done
    printf '%s' "${out:0:$1}"
}

new_uuid() { cat /proc/sys/kernel/random/uuid; }

npgsql_sslmode() {
    case "$1" in
        disable) echo Disable ;; allow) echo Allow ;; prefer) echo Prefer ;; require) echo Require ;;
        verify-ca) echo VerifyCA ;; verify-full) echo VerifyFull ;; *) return 1 ;;
    esac
}

apt_has_candidate() {
    local c
    c="$(apt-cache policy "$1" 2>/dev/null | awk '/Candidate:/ {print $2; exit}')"
    [[ -n "$c" && "$c" != "(none)" ]]
}

run_server() { # run_server KATALOG_WERSJI ARGUMENTY...: polecenie serwera jako użytkownik usługi (nigdy jako root)
    zi_run_as_service_user "$@"
}

installed_version() {
    local t
    t="$(readlink "$INSTALL_ROOT/current" 2>/dev/null)" || return 1
    basename "$t"
}

read_state() { # read_state KLUCZ
    [[ -f "$STATE_FILE" ]] || return 1
    local line
    while IFS= read -r line; do
        [[ "$line" == "$1="* ]] && { printf '%s' "${line#*=}"; return 0; }
    done <"$STATE_FILE"
    return 1
}

# Hasło konta: zasady aplikacji (min. 12 znaków, mała i wielka litera, cyfra), sprawdzane przed zmianami na serwerze.
password_ok() {
    local p="$1"
    [[ ${#p} -ge 12 && "$p" == *[a-z]* && "$p" == *[A-Z]* && "$p" == *[0-9]* && "$p" != *$'\n'* ]]
}
PASSWORD_RULE="min. 12 znaków, mała i wielka litera oraz cyfra"

# ------------------------------------------------------------------------------------------------ argumenty

parse_args() {
    local a v
    while [[ $# -gt 0 ]]; do
        a="$1"
        v=""
        if [[ "$a" == --*=* ]]; then v="${a#*=}"; a="${a%%=*}"; fi
        case "$a" in
            -h|--help) usage; exit 0 ;;
            --unattended) UNATTENDED=1 ;;
            --yes|-y) ASSUME_YES=1 ;;
            --repair) MODE=repair ;;
            --upgrade) MODE=upgrade ;;
            --uninstall|--purge) MODE=uninstall ;;
            --keep-data) KEEP_DATA=1 ;;
            --backup) MODE=backup ;;
            --no-pdf) NO_PDF=1 ;;
            --set-password)
                MODE=set-password
                if [[ -n "$v" ]]; then RESET_USER="$v"; elif [[ $# -ge 2 && "$2" != -* ]]; then RESET_USER="$2"; shift; fi ;;
            --secrets-stdin) SECRETS_STDIN=1 ;;
            --package|--version|--db|--db-host|--db-port|--db-name|--db-user|--db-sslmode|--public-url|--port|--listen|\
            --trusted-proxy|--contact-email|--admin-user|--secrets-file|--restore|--output|--backup-to|--reset-mfa)
                local val
                if [[ -n "$v" ]]; then val="$v"; else [[ $# -ge 2 ]] || die "$a wymaga wartości"; val="$2"; shift; fi
                case "$a" in
                    --package) PACKAGE="$val" ;; --version) VERSION_ARG="$val" ;; --db) DB_MODE="$val" ;;
                    --db-host) DB_HOST="$val" ;; --db-port) DB_PORT="$val" ;; --db-name) DB_NAME="$val" ;;
                    --db-user) DB_USER="$val" ;; --db-sslmode) DB_SSLMODE="$val" ;; --public-url) PUBLIC_URL="$val" ;;
                    --port) PORT="$val" ;; --listen) LISTEN="$val" ;; --trusted-proxy) TRUSTED_PROXIES+=("$val") ;;
                    --contact-email) CONTACT_EMAIL="$val" ;; --admin-user) ADMIN_USER="$val" ;; --secrets-file) SECRETS_FILE="$val" ;;
                    --restore) MODE=restore; RESTORE_FILE="$val" ;; --output) BACKUP_OUTPUT="$val" ;; --backup-to) BACKUP_TO="$val" ;;
                    --reset-mfa) MODE=reset-mfa; RESET_USER="$val" ;;
                esac ;;
            --db-password|--admin-password|--password*)
                die "$a: hasła nigdy nie są argumentami; użyj --secrets-file, --secrets-stdin albo zmiennych środowiska" ;;
            *) die "nieznany argument: $1 (zob. --help)" ;;
        esac
        shift
    done
}

load_secrets() {
    local line key value
    parse_secret_line() {
        line="${1%$'\r'}"
        [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && return 0
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            DB_PASSWORD) [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="$value" ;;
            ADMIN_PASSWORD) [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$value" ;;
            *) warn "pomijam nieznany klucz '$key'" ;;
        esac
    }
    if [[ -n "$SECRETS_FILE" ]]; then
        [[ -f "$SECRETS_FILE" && ! -L "$SECRETS_FILE" ]] || die "nie ma pliku sekretów $SECRETS_FILE"
        local mode owner
        mode="$(stat -c %a -- "$SECRETS_FILE")"
        owner="$(stat -c %u -- "$SECRETS_FILE")"
        [[ "$mode" == 600 || "$mode" == 400 ]] || die "plik sekretów $SECRETS_FILE musi mieć prawa 600 (ma $mode)"
        [[ "$owner" == 0 ]] || die "właścicielem pliku sekretów $SECRETS_FILE musi być root"
        while IFS= read -r line || [[ -n "$line" ]]; do parse_secret_line "$line"; done <"$SECRETS_FILE"
    fi
    if [[ $SECRETS_STDIN -eq 1 ]]; then
        while IFS= read -r line || [[ -n "$line" ]]; do parse_secret_line "$line"; done
    fi
    [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="${ZDROWIEINFO_DB_PASSWORD-}"
    [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="${ZDROWIEINFO_ADMIN_PASSWORD-}"
    unset ZDROWIEINFO_DB_PASSWORD ZDROWIEINFO_ADMIN_PASSWORD
    unset -f parse_secret_line
}

# ------------------------------------------------------------------------------------------------ krok 1: system

check_system() {
    [[ $EUID -eq 0 ]] || die "uruchom jako root: sudo bash $0"
    [[ -r /etc/os-release ]] || die "brak /etc/os-release"
    local id="" version=""
    while IFS='=' read -r k v; do
        v="${v%\"}"; v="${v#\"}"
        case "$k" in ID) id="$v" ;; VERSION_ID) version="$v" ;; esac
    done </etc/os-release
    [[ "$id" == ubuntu ]] || die "wymagane Ubuntu 24.04 LTS lub nowsze (jest '$id')"
    local major="${version%%.*}" minor="${version#*.}"
    [[ "$major" =~ ^[0-9]+$ && "$minor" =~ ^[0-9]+$ ]] || die "nie można odczytać wersji Ubuntu '$version'"
    if ((10#$major < 24 || (10#$major == 24 && 10#$minor < 4))); then die "wymagane Ubuntu 24.04 LTS lub nowsze (jest $version)"; fi
    case "$(uname -m)" in
        x86_64) ARCH=x64 ;;
        aarch64) ARCH=arm64 ;;
        *) die "nieobsługiwana architektura $(uname -m) (x64 albo arm64)" ;;
    esac
    command -v systemctl >/dev/null || die "wymagany systemd"
    if [[ $UNATTENDED -eq 0 && ! -r /dev/tty ]]; then die "brak terminala: użyj --unattended"; fi
}

# Instalacja starym skryptem (przed układem /opt/zdrowieinfo/releases): nie jest przenoszona automatycznie.
check_legacy() {
    [[ -f "$LEGACY_ENV" && ! -f "$STATE_FILE" ]] || return 0
    die "na tym serwerze jest ZdrowieInfo zainstalowane poprzednim instalatorem ($LEGACY_ENV, /var/lib/zdrowieinfo).
Ten instalator używa innego układu (/opt/zdrowieinfo/releases, config, data) i go nie przenosi. Zrób kopię
(/usr/local/sbin/zdrowieinfo-backup), zatrzymaj starą usługę (systemctl disable --now zdrowieinfo zdrowieinfo-backup.timer)
– zob. README, sekcja Wdrożenie."
}

# ------------------------------------------------------------------------------------------------ krok 2: pakiety

install_packages() {
    info "Instalowanie pakietów"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q
    local pkgs=(ca-certificates curl tar openssl tzdata)
    # Klient PostgreSQL jest już, gdy ten serwer ma PostgreSQL (współdzielony z innymi aplikacjami) – zostaje, jaki jest.
    # ensure_pg_client dokłada pg_dump zgodny z serwerem, gdy trzeba nowszego.
    command -v psql >/dev/null 2>&1 || pkgs+=(postgresql-client)
    local icu
    icu="$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n1 || true)"
    [[ -n "$icu" ]] || die "nie znaleziono pakietu biblioteki ICU (libicuNN)"
    pkgs+=("$icu")
    local ssl=""
    for p in libssl3t64 libssl3; do if apt_has_candidate "$p"; then ssl="$p"; break; fi; done
    [[ -n "$ssl" ]] || die "nie znaleziono pakietu OpenSSL 3"
    pkgs+=("$ssl")
    # --no-upgrade: zainstalowane już pakiety (biblioteki używane też przez inne aplikacje) zostają, jakie są.
    apt-get install -y -q --no-install-recommends --no-upgrade "${pkgs[@]}"
    if [[ $NO_PDF -eq 0 ]]; then
        # fonts-texgyre jest konieczny: bez rekomendacji TeX Gyre w OpenType się nie instaluje (zob. docs/decyzje.md, D21).
        info "TeX do raportów PDF (XeLaTeX, polski, czcionki TeX Gyre) – kilkaset MB przy pierwszej instalacji"
        apt-get install -y -q --no-install-recommends --no-upgrade "${TEX_PACKAGES[@]}"
    fi
}

# ------------------------------------------------------------------------------------------------ krok 3: paczka

sha256_of() { sha256sum -- "$1" | cut -d' ' -f1; }

verify_against_sums() { # verify_against_sums PLIK SUMY (zanim cokolwiek z paczki zostanie użyte)
    local name expected actual
    name="$(basename "$1")"
    expected="$(awk -v n="$name" '{ f = $2; sub(/^\*/, "", f); sub(/^\.\//, "", f)
        if (f == n && length($1) == 64 && $1 ~ /^[0-9a-fA-F]+$/) { print tolower($1); exit } }' "$2")"
    [[ -n "$expected" ]] || die "$(basename "$2") nie ma wpisu dla $name"
    actual="$(sha256_of "$1")"
    [[ "$actual" == "$expected" ]] || die "niezgodny SHA-256 pliku $name"
    info "SHA-256 pliku $name zgodny"
}

github_download() { # github_download URL CEL
    curl -fsSL --proto '=https' --tlsv1.2 -A "$USER_AGENT" -o "$2" -- "$1" || die "pobieranie nieudane: $1"
}

obtain_package() {
    WORK="$(mktemp -d /tmp/zdrowieinfo-install.XXXXXX)"
    chmod 700 "$WORK"
    if [[ -z "$PACKAGE" && -z "$VERSION_ARG" && -z "$GITHUB_REPOSITORY" && $UNATTENDED -eq 0 ]]; then
        tty_read PACKAGE "Ścieżka do ZdrowieInfo.Server-<wersja>-linux-$ARCH.tar.gz"
    fi
    if [[ -n "$PACKAGE" ]]; then
        [[ -f "$PACKAGE" ]] || die "nie ma paczki $PACKAGE"
        local base
        base="$(basename "$PACKAGE")"
        [[ "$base" =~ ^ZdrowieInfo\.Server-([0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5})-linux-(x64|arm64)\.tar\.gz$ ]] ||
            die "paczka musi się nazywać ZdrowieInfo.Server-<x.y.z>-linux-<x64|arm64>.tar.gz"
        PKG_VERSION="${BASH_REMATCH[1]}"
        [[ "${BASH_REMATCH[2]}" == "$ARCH" ]] || die "paczka jest dla ${BASH_REMATCH[2]}, ten serwer to $ARCH"
        PKG_FILE="$WORK/$base"
        cp -- "$PACKAGE" "$PKG_FILE"
        local sums
        sums="$(dirname "$PACKAGE")/SHA256SUMS.txt"
        if [[ -f "$sums" ]]; then
            verify_against_sums "$PKG_FILE" "$sums"
        else
            warn "brak SHA256SUMS.txt obok paczki; skrót nie jest sprawdzany"
        fi
        bootstrap_functions "$PKG_FILE"
        return 0
    fi

    [[ -n "$GITHUB_REPOSITORY" ]] || die "ten install.sh nie ma repozytorium wydań: użyj --package PLIK (paczka z build/build-server.sh)"
    local version="$VERSION_ARG"
    if [[ -z "$version" ]]; then
        local json
        json="$(curl -fsSL --proto '=https' -A "$USER_AGENT" -H 'Accept: application/vnd.github+json' \
            "https://api.github.com/repos/$GITHUB_REPOSITORY/releases/latest")" || die "nie można odczytać najnowszego wydania"
        version="$(printf '%s' "$json" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -n1)"
    fi
    [[ "$version" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "wersja '$version' nie ma postaci x.y.z"
    PKG_VERSION="$version"
    local name="ZdrowieInfo.Server-$version-linux-$ARCH.tar.gz" base="https://github.com/$GITHUB_REPOSITORY/releases/download/v$version"
    info "Pobieranie $name"
    github_download "$base/$name" "$WORK/$name"
    github_download "$base/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt"
    PKG_FILE="$WORK/$name"
    verify_against_sums "$PKG_FILE" "$WORK/SHA256SUMS.txt"
    bootstrap_functions "$PKG_FILE"
}

# Funkcje sprawdzania i rozpakowywania są w update.sh (jedna implementacja) – z paczki.
bootstrap_functions() {
    local pkg="$1" dir="$WORK/bootstrap" m
    mkdir -p "$dir"
    for m in ./deploy/linux/update.sh deploy/linux/update.sh; do
        if tar -xzf "$pkg" -C "$dir" --no-same-owner "$m" 2>/dev/null; then break; fi
    done
    local f="$dir/deploy/linux/update.sh"
    [[ -f "$f" && ! -L "$f" ]] || die "paczka nie ma deploy/linux/update.sh"
    # shellcheck source=update.sh
    source "$f"
    set -Eeuo pipefail
    trap 'die "install.sh przerwany w linii $LINENO"' ERR
}

# ------------------------------------------------------------------------------------------------ układ i użytkownik

create_user() {
    if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
        info "Tworzenie użytkownika systemowego $SERVICE_USER"
        useradd --system --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
    fi
}

create_layout() {
    info "Przygotowanie $INSTALL_ROOT"
    install -d -o root -g root -m 755 "$INSTALL_ROOT" "$INSTALL_ROOT/releases" "$INSTALL_ROOT/bin"
    install -d -o root -g root -m 700 "$INSTALL_ROOT/config"
    [[ ! -L "$INSTALL_ROOT/data" ]] || die "$INSTALL_ROOT/data jest dowiązaniem"
    install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 700 "$INSTALL_ROOT/data"
    # data/ należy do użytkownika usługi, który mógł umieścić tam dowiązania; install -d za nimi podąża, więc katalogi
    # niżej tworzy sam użytkownik usługi, nigdy root.
    as_service_user install -d -m 700 "$INSTALL_ROOT/data/keys" "$INSTALL_ROOT/data/reports" "$INSTALL_ROOT/data/updates" ||
        warn "nie udało się utworzyć wszystkich katalogów w $INSTALL_ROOT/data (czy któryś jest dowiązaniem?)"
    secure_backups_dir
}

# backups/ (archiwa z backup.sh) i backups/pre-update/ (zrzuty z update.sh) są root:root 700: pisze tam root, więc
# użytkownik usługi nie może podłożyć dowiązań. Dowiązania w środku zostają, jakie są, i nie są śledzone.
secure_backups_dir() {
    local top="$INSTALL_ROOT/backups" pre="$INSTALL_ROOT/backups/pre-update"
    [[ ! -L "$top" ]] || die "$top jest dowiązaniem: kopie muszą być w zwykłym katalogu roota. Przenieś je (mv $top $top.old; mkdir -m 700 $top) i uruchom ponownie"
    [[ -e "$top" ]] || install -d -o root -g root -m 700 "$top"
    [[ -d "$top" ]] || die "$top nie jest katalogiem"
    chown -h root:root -- "$top"
    chmod 700 -- "$top"
    chown -hR root:root -- "$top"
    find "$top" -mindepth 1 -type d -exec chmod 700 {} +
    find "$top" -mindepth 1 -type f -exec chmod 600 {} +
    if [[ -L "$pre" ]]; then rm -f -- "$pre"; fi
    [[ -e "$pre" ]] || install -d -o root -g root -m 700 "$pre"
    [[ -d "$pre" && ! -L "$pre" && "$(stat -c '%u %a' -- "$pre")" == "0 700" ]] || die "$pre nie jest katalogiem tylko dla roota"
}

fix_permissions() { # naprawa: właściciele i prawa, bez zmiany zawartości
    create_layout
    chown -hR "$SERVICE_USER:$SERVICE_USER" "$INSTALL_ROOT/data"
    chmod 700 "$INSTALL_ROOT/data"
    local f
    for f in "$CONFIG_FILE" "$UPDATE_ENV" "$STATE_FILE"; do [[ -f "$f" ]] && { chown root:root "$f"; chmod 600 "$f"; }; done
    find "$INSTALL_ROOT/releases" -mindepth 1 -maxdepth 1 -type d -exec chown -hR root:root {} + -exec chmod -R go-w {} +
    for f in "$INSTALL_ROOT/bin/update.sh" "$INSTALL_ROOT/bin/backup.sh"; do
        [[ -f "$f" && ! -L "$f" ]] && { chown root:root "$f"; chmod 755 "$f"; }
    done
    for f in "${UNITS[@]}"; do [[ -f "$UNIT_DIR/$f" && ! -L "$UNIT_DIR/$f" ]] && { chown root:root "$UNIT_DIR/$f"; chmod 644 "$UNIT_DIR/$f"; }; done
    return 0
}

unpack_release() {
    local release="$INSTALL_ROOT/releases/$PKG_VERSION" staging="$INSTALL_ROOT/releases/.staging-$PKG_VERSION"
    info "Rozpakowywanie $PKG_VERSION do $release"
    rm -rf -- "$staging"
    ZI_ERROR=""
    zi_safe_unpack "$PKG_FILE" "$staging" || { rm -rf -- "$staging"; die "niebezpieczna albo uszkodzona paczka: $ZI_ERROR"; }
    [[ -x "$staging/ZdrowieInfo.Server" ]] || { rm -rf -- "$staging"; die "paczka nie ma ZdrowieInfo.Server"; }
    rm -rf -- "$release"
    mv -T -- "$staging" "$release"
}

# install_deploy_files KATALOG: update.sh, backup.sh i jednostki systemd, które uruchamia root. KATALOG to zawsze paczka
# podana temu skryptowi przez administratora (--package albo --version, pobrana i sprawdzona), rozpakowana w tym
# przebiegu – nigdy current/ ani releases/, których zawartość może wybrać aktualizacja zlecona przez użytkownika usługi.
install_deploy_files() {
    local src="$1/deploy/linux" u f
    for f in update.sh backup.sh "${UNITS[@]}"; do
        [[ -f "$src/$f" && ! -L "$src/$f" ]] || die "paczka nie ma deploy/linux/$f"
    done
    install -o root -g root -m 755 "$src/update.sh" "$INSTALL_ROOT/bin/update.sh"
    install -o root -g root -m 755 "$src/backup.sh" "$INSTALL_ROOT/bin/backup.sh"
    for u in "${UNITS[@]}"; do install -o root -g root -m 644 "$src/$u" "$UNIT_DIR/$u"; done
    systemctl daemon-reload
}

missing_deploy_files() { # zainstalowane pliki roota, których brakuje (naprawa bez paczki)
    local f
    for f in "$INSTALL_ROOT/bin/update.sh" "$INSTALL_ROOT/bin/backup.sh" "${UNITS[@]/#/$UNIT_DIR/}"; do
        [[ -f "$f" && ! -L "$f" ]] || printf '%s\n' "$f"
    done
}

as_service_user() { # as_service_user POLECENIE...: jako użytkownik usługi, z czystym środowiskiem
    runuser -u "$SERVICE_USER" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 "$@"
}

put_as_service_user() { # put_as_service_user CEL <TREŚĆ: CEL zapisuje użytkownik usługi (plik tymczasowy + zmiana
    # nazwy), więc root nigdy nie otwiera do zapisu ścieżki w jego katalogach
    # shellcheck disable=SC2016 # rozwija wewnętrzna powłoka
    as_service_user /bin/sh -c 'umask 077; t="$(mktemp "$1.XXXXXX")" || exit 1
        if cat >"$t" && mv -fT -- "$t" "$1"; then exit 0; fi; rm -f -- "$t"; exit 1' sh "$1"
}

# ------------------------------------------------------------------------------------------------ baza

pg_query() { # pg_query SQL: do skonfigurowanej bazy, skonfigurowaną rolą
    PGPASSWORD="$DB_PASSWORD" PGSSLMODE="$DB_SSLMODE" PGCONNECT_TIMEOUT=10 psql -X -At -v ON_ERROR_STOP=1 \
        -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -c "$1" 2>&1
}

pg_test_connection() { # ustawia PG_SERVER_NUM i PG_IS_OWNER; przy błędzie 1 z komunikatem
    local out
    if ! out="$(pg_query "SELECT current_setting('server_version_num') || '|' || (SELECT pg_catalog.pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = current_database())::text")"; then
        printf 'Połączenie nieudane: %s\n' "$out" >&2
        return 1
    fi
    PG_SERVER_NUM="${out%%|*}"
    PG_IS_OWNER="${out#*|}"
    return 0
}

ensure_pg_client() { # ensure_pg_client WERSJA: pg_dump nie może być starszy niż serwer
    local major="$1"
    if [[ ! -x "/usr/lib/postgresql/$major/bin/pg_dump" ]]; then
        info "Serwer PostgreSQL $major: dodaję repozytorium apt projektu PostgreSQL dla zgodnego klienta"
        apt-get install -y -q --no-install-recommends postgresql-common
        /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
        apt-get install -y -q --no-install-recommends "postgresql-client-$major"
    fi
    PG_BIN="/usr/lib/postgresql/$major/bin"
    [[ -x "$PG_BIN/pg_dump" ]] || die "nie znaleziono pg_dump dla PostgreSQL $major"
}

setup_database() {
    if [[ -z "$DB_MODE" ]]; then
        if [[ $UNATTENDED -eq 1 ]]; then die "brak --db=local|existing"; fi
        echo "Baza: 1) PostgreSQL na tym serwerze (instalowany, gdy go nie ma; istniejący jest współdzielony)  2) serwer PostgreSQL gdzie indziej"
        local choice
        tty_read choice "Wybierz 1 albo 2" 1
        case "$choice" in 1) DB_MODE=local ;; 2) DB_MODE=existing ;; *) die "niepoprawny wybór" ;; esac
    fi
    case "$DB_MODE" in
        local) setup_local_database ;;
        existing) setup_existing_database ;;
        *) die "--db musi być local albo existing" ;;
    esac
}

# ---- PostgreSQL na tym serwerze. Może już obsługiwać inne aplikacje, więc ZdrowieInfo tylko dodaje własną rolę i bazę:
# nigdy nie instaluje, nie aktualizuje, nie konfiguruje ani nie restartuje istniejącego serwera i nie zmienia ani nie
# usuwa roli czy bazy, której nie utworzyło.
local_pg_clusters() { # "wersja port stan" każdego klastra (układ Ubuntu/Debian); pusto, gdy brak
    command -v pg_lsclusters >/dev/null 2>&1 || return 0
    pg_lsclusters --no-header 2>/dev/null | awk 'NF >= 4 {print $1, $3, $4}'
}
port_in_use() { command -v ss >/dev/null 2>&1 && ss -Hltn "sport = :$1" 2>/dev/null | grep -q .; }
pg_local() { # pg_local SQL: jako superużytkownik postgres przez gniazdo lokalne, w wybranym klastrze ($DB_PORT)
    runuser -u postgres -- psql -X -At -q -v ON_ERROR_STOP=1 -p "$DB_PORT" -d postgres -c "$1"
}
pg_name_free() { # pg_name_free NAZWA: nie ma roli ani bazy o tej nazwie
    [[ "$(pg_local "SELECT (SELECT count(*) FROM pg_roles WHERE rolname = '$1') + (SELECT count(*) FROM pg_database WHERE datname = '$1')")" == 0 ]]
}

setup_local_database() {
    local clusters online
    clusters="$(local_pg_clusters)"
    if [[ -z "$clusters" ]]; then
        port_in_use "${DB_PORT:-5432}" && die "port ${DB_PORT:-5432} jest zajęty, a nie ma klastra PostgreSQL z pakietów Ubuntu (pg_lsclusters). Jeśli to serwer PostgreSQL, użyj --db=existing z rolą i bazą dla ZdrowieInfo."
        info "Instalowanie PostgreSQL na tym serwerze"
        apt-get install -y -q --no-install-recommends postgresql
        systemctl enable --now postgresql
        clusters="$(local_pg_clusters)"
        [[ -n "$clusters" ]] || die "PostgreSQL zainstalowany, ale nie znaleziono klastra (pg_lsclusters)"
    else
        info "PostgreSQL jest już zainstalowany: zostaje, jaki jest (ZdrowieInfo dodaje tylko własną rolę i bazę)"
    fi
    online="$(awk '$3 == "online" {print $2}' <<<"$clusters")"
    if [[ -n "$DB_PORT" ]]; then
        grep -qx -- "$DB_PORT" <<<"$online" || die "na porcie $DB_PORT nie działa klaster PostgreSQL (zob. pg_lsclusters)"
    elif grep -qx 5432 <<<"$online"; then
        DB_PORT=5432
    elif [[ -n "$online" && "$(wc -l <<<"$online")" -eq 1 ]]; then
        DB_PORT="$online"
    elif [[ -z "$online" ]]; then
        die "żaden klaster PostgreSQL nie działa (zob. pg_lsclusters); uruchom go i ponów instalację"
    else
        die "działa kilka klastrów PostgreSQL (porty $(tr '\n' ' ' <<<"$online")); wybierz jeden przez --db-port"
    fi
    DB_HOST=127.0.0.1 DB_SSLMODE=prefer

    local num
    num="$(pg_local 'SHOW server_version_num')"
    [[ "$num" =~ ^[0-9]+$ ]] || die "nie można odpytać klastra PostgreSQL na porcie $DB_PORT"
    ((num >= 160000)) || die "wymagany PostgreSQL 16 lub nowszy (klaster na porcie $DB_PORT to $num); nie jest aktualizowany"

    # Nowa rola i baza. Nazwa z --db-name/--db-user musi być wolna; inaczej pierwsza wolna z zdrowieinfo, zdrowieinfo_2…
    local name_re='^[a-z_][a-z0-9_]{0,62}$'
    if [[ -n "$DB_NAME" || -n "$DB_USER" ]]; then
        DB_NAME="${DB_NAME:-${DB_USER}}" DB_USER="${DB_USER:-${DB_NAME}}"
        [[ "$DB_NAME" =~ $name_re && "$DB_USER" =~ $name_re ]] || die "nazwy bazy i roli: małe litery, cyfry i _"
        [[ "$DB_NAME" != postgres && "$DB_NAME" != template0 && "$DB_NAME" != template1 && "$DB_USER" != postgres ]] ||
            die "'$DB_NAME'/'$DB_USER' to nazwy samego PostgreSQL"
        [[ "$(pg_local "SELECT count(*) FROM pg_database WHERE datname = '$DB_NAME'")" == 0 ]] ||
            die "baza '$DB_NAME' już istnieje na porcie $DB_PORT; ZdrowieInfo nigdy nie przejmuje istniejącej bazy. Wybierz inną --db-name."
        [[ "$(pg_local "SELECT count(*) FROM pg_roles WHERE rolname = '$DB_USER'")" == 0 ]] ||
            die "rola '$DB_USER' już istnieje na porcie $DB_PORT; ZdrowieInfo nigdy nie przejmuje istniejącej roli. Wybierz inną --db-user."
    else
        local n candidate=zdrowieinfo
        for n in $(seq 2 99); do
            pg_name_free "$candidate" && break
            candidate="zdrowieinfo_$n"
        done
        pg_name_free "$candidate" || die "brak wolnej nazwy roli/bazy zdrowieinfo … zdrowieinfo_99 na porcie $DB_PORT"
        [[ "$candidate" == zdrowieinfo ]] || info "Rola albo baza 'zdrowieinfo' już istnieje (inna instalacja?); używam '$candidate'"
        DB_NAME="$candidate" DB_USER="$candidate"
    fi

    info "Tworzenie roli '$DB_USER' i bazy '$DB_NAME' (PostgreSQL na porcie $DB_PORT)"
    DB_PASSWORD="$(random_alnum 32)"
    # SQL przez stdin, nigdy w linii poleceń. Hasło alfanumeryczne, nazwy zgodne z $name_re – bez escapowania.
    # Bez IF EXISTS / ALTER: nazwy sprawdzone jako wolne, istniejący obiekt kończy się błędem.
    runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -p "$DB_PORT" -d postgres <<SQL || die "nie udało się utworzyć roli i bazy"
CREATE ROLE "$DB_USER" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$DB_PASSWORD';
CREATE DATABASE "$DB_NAME" OWNER "$DB_USER";
REVOKE ALL ON DATABASE "$DB_NAME" FROM PUBLIC;
SQL
    DB_CREATED=1
    if ! pg_test_connection; then
        pg_local "DROP DATABASE IF EXISTS \"$DB_NAME\"" >/dev/null 2>&1 || true
        pg_local "DROP ROLE IF EXISTS \"$DB_USER\"" >/dev/null 2>&1 || true
        die "nie można połączyć się z 127.0.0.1:$DB_PORT jako '$DB_USER' hasłem. Jeśli pg_hba.conf tego PostgreSQL nie pozwala na logowanie hasłem z localhost, dodaj np. 'host $DB_NAME $DB_USER 127.0.0.1/32 scram-sha-256', przeładuj PostgreSQL i ponów instalację."
    fi
    ensure_pg_client "$((PG_SERVER_NUM / 10000))"
}

setup_existing_database() {
    while true; do
        ask DB_HOST "Host PostgreSQL" ""
        ask DB_PORT "Port" 5432
        ask DB_NAME "Baza" zdrowieinfo
        ask DB_USER "Użytkownik" zdrowieinfo
        ask DB_SSLMODE "Tryb SSL (disable/allow/prefer/require/verify-ca/verify-full)" require
        [[ $UNATTENDED -eq 0 || -n "$DB_HOST" ]] || die "brak --db-host"
        npgsql_sslmode "$DB_SSLMODE" >/dev/null || die "niepoprawny tryb SSL '$DB_SSLMODE'"
        [[ "$DB_PORT" =~ ^[0-9]+$ ]] || die "niepoprawny port '$DB_PORT'"
        if [[ -z "$DB_PASSWORD" ]]; then
            [[ $UNATTENDED -eq 0 ]] || die "brak DB_PASSWORD (plik sekretów, stdin albo ZDROWIEINFO_DB_PASSWORD)"
            tty_secret DB_PASSWORD "Hasło użytkownika $DB_USER"
        fi
        # Plik konfiguracji czyta systemd (EnvironmentFile), a connection string Npgsql dzieli się na ; i = – hasło z takimi
        # znakami trzeba by cytować, a systemd usuwa cudzysłowy. Tylko znaki, które nie wymagają cytowania.
        [[ "$DB_PASSWORD" != *[[:space:]\"\'\\\$\`\;=]* ]] || die "hasło bazy nie może zawierać spacji, cudzysłowów, \\, \$, \`, ; ani ="
        info "Test połączenia z $DB_HOST:$DB_PORT/$DB_NAME"
        if pg_test_connection; then break; fi
        [[ $UNATTENDED -eq 0 ]] || die "nie można połączyć się z bazą"
        ask_yes_no "Spróbować z innymi wartościami?" t || die "nie można połączyć się z bazą"
        DB_HOST="" DB_PORT="" DB_NAME="" DB_USER="" DB_SSLMODE="" DB_PASSWORD=""
    done
    [[ "$PG_SERVER_NUM" =~ ^[0-9]+$ ]] || die "nieoczekiwana wersja serwera '$PG_SERVER_NUM'"
    ((PG_SERVER_NUM >= 160000)) || die "wymagany PostgreSQL 16 lub nowszy (serwer to $PG_SERVER_NUM)"
    [[ "$PG_IS_OWNER" == true ]] || die "rola $DB_USER musi być właścicielem bazy $DB_NAME (aktualizacja przy wycofaniu odtwarza bazę z kopii, a migracja tworzy rozszerzenie unaccent)"
    ensure_pg_client "$((PG_SERVER_NUM / 10000))"
}

# ------------------------------------------------------------------------------------------------ konfiguracja

health_url() {
    local host="$LISTEN"
    [[ "$host" == 0.0.0.0 || -z "$host" ]] && host=127.0.0.1
    echo "http://$host:$PORT/healthz"
}

collect_settings() { # pytania przed jakąkolwiek zmianą na serwerze
    while true; do
        ask PUBLIC_URL "Publiczny adres aplikacji (https://…, pod nim działa reverse proxy)" ""
        PUBLIC_URL="${PUBLIC_URL%/}"
        [[ "$PUBLIC_URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$ ]] && break
        [[ $UNATTENDED -eq 0 ]] || die "--public-url musi mieć postać https://domena (bez ścieżki)"
        echo "Adres musi zaczynać się od https:// i nie może mieć ścieżki (Web Push, passkey i PWA wymagają HTTPS)." >/dev/tty
        PUBLIC_URL=""
    done
    ask PORT "Port aplikacji (reverse proxy kieruje na http://127.0.0.1:<port>)" 5180
    [[ "$PORT" =~ ^[0-9]{1,5}$ ]] && ((PORT >= 1 && PORT <= 65535)) || die "niepoprawny port '$PORT'"
    LISTEN="${LISTEN:-127.0.0.1}"
    local ipv4='^([0-9]{1,3}\.){3}[0-9]{1,3}$'
    [[ "$LISTEN" =~ $ipv4 ]] || die "--listen: oczekiwany adres IPv4 (np. 127.0.0.1 albo 0.0.0.0), jest '$LISTEN'"
    local p
    for p in "${TRUSTED_PROXIES[@]}"; do [[ "$p" =~ $ipv4 ]] || die "--trusted-proxy: oczekiwany adres IPv4, jest '$p'"; done
    if [[ "$LISTEN" != 127.0.0.1 && ${#TRUSTED_PROXIES[@]} -eq 0 ]]; then
        warn "aplikacja nasłuchuje na $LISTEN, ale nie podano --trusted-proxy: nagłówki X-Forwarded-* z proxy nie będą uwzględniane"
    fi
    [[ -z "$CONTACT_EMAIL" || "$CONTACT_EMAIL" =~ ^[^[:space:]@]+@[^[:space:]@]+$ ]] || die "niepoprawny --contact-email"
}

write_config() {
    [[ ! -e "$CONFIG_FILE" ]] || { info "Zostawiam istniejący $CONFIG_FILE"; return 0; }
    info "Zapisywanie $CONFIG_FILE"
    local cs tmp i
    cs="Host=$(cs_value "$DB_HOST");Port=$DB_PORT;Database=$(cs_value "$DB_NAME");Username=$(cs_value "$DB_USER");Password=$(cs_value "$DB_PASSWORD");SslMode=$(npgsql_sslmode "$DB_SSLMODE")"
    tmp="$(mktemp "$INSTALL_ROOT/config/.zdrowieinfo-env.XXXXXX")"
    {
        echo "# Konfiguracja ZdrowieInfo – czyta ją systemd (EnvironmentFile) i update.sh. root:root 600."
        echo "# Wartości bez cudzysłowów; zmiana: edytuj i 'systemctl restart zdrowieinfo'. Opis: README, sekcja Wdrożenie."
        echo "ASPNETCORE_URLS=http://$LISTEN:$PORT"
        echo "ZdrowieInfo__PublicUrl=$PUBLIC_URL"
        echo "ZdrowieInfo__DataDirectory=$INSTALL_ROOT/data"
        echo "ZdrowieInfo__Updates__GitHubRepository=$GITHUB_REPOSITORY"
        for i in "${!TRUSTED_PROXIES[@]}"; do echo "ZdrowieInfo__TrustedProxies__$i=${TRUSTED_PROXIES[$i]}"; done
        [[ -z "$CONTACT_EMAIL" ]] || echo "ZdrowieInfo__Sources__ContactEmail=$CONTACT_EMAIL"
        [[ $NO_PDF -eq 0 ]] || echo "ZdrowieInfo__Export__Enabled=false"
        echo "ConnectionStrings__ZdrowieInfo=$cs"
    } | (umask 077; cat >"$tmp")
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$CONFIG_FILE"
}

write_update_env() { # kopia ustawień bazy tylko dla roota (update.sh, backup.sh: pg_dump / pg_restore)
    if [[ -e "$UPDATE_ENV" ]]; then info "Zostawiam istniejący $UPDATE_ENV"; return 0; fi
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.update-env.XXXXXX")"
    (umask 077; printf '%s\n' "# Dla $INSTALL_ROOT/bin/update.sh i backup.sh. Tylko root." \
        "PGHOST=$DB_HOST" "PGPORT=$DB_PORT" "PGDATABASE=$DB_NAME" "PGUSER=$DB_USER" "PGPASSWORD=$DB_PASSWORD" \
        "PGSSLMODE=$DB_SSLMODE" "PG_BIN=$PG_BIN" "HEALTH_URL=$(health_url)" >"$tmp")
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$UPDATE_ENV"
}

write_state() {
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.state.XXXXXX")"
    printf '%s\n' "DB_MODE=$DB_MODE" "DB_NAME=$DB_NAME" "DB_USER=$DB_USER" "DB_PORT=$DB_PORT" "DB_CREATED=$DB_CREATED" \
        "PUBLIC_URL=$PUBLIC_URL" "PORT=$PORT" "LISTEN=$LISTEN" "NO_PDF=$NO_PDF" \
        "INSTALLED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$tmp"
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$STATE_FILE"
}

collect_admin_account() { # przed zmianami na serwerze
    ask ADMIN_USER "Pierwsze konto – login (administrator)" admin
    [[ -n "$ADMIN_USER" && "$ADMIN_USER" != -* && "$ADMIN_USER" =~ ^[A-Za-z0-9._@+-]{1,64}$ ]] ||
        die "niepoprawny login '$ADMIN_USER' (litery, cyfry, . _ @ + -)"
    if [[ -z "$ADMIN_PASSWORD" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "brak ADMIN_PASSWORD (plik sekretów, stdin albo ZDROWIEINFO_ADMIN_PASSWORD)"
        while true; do
            tty_secret ADMIN_PASSWORD "Hasło dla $ADMIN_USER ($PASSWORD_RULE)" confirm
            password_ok "$ADMIN_PASSWORD" && break
            echo "Hasło nie spełnia zasad ($PASSWORD_RULE)." >/dev/tty
            ADMIN_PASSWORD=""
        done
    fi
    password_ok "$ADMIN_PASSWORD" || die "ADMIN_PASSWORD nie spełnia zasad ($PASSWORD_RULE)"
}

create_admin_account() {
    info "Tworzenie konta $ADMIN_USER (administrator)"
    # Hasło przez stdin, nigdy w linii poleceń.
    printf '%s\n' "$ADMIN_PASSWORD" | run_server "$INSTALL_ROOT/current" --create-user "$ADMIN_USER" ||
        die "nie udało się utworzyć konta"
}

migrate() {
    info "Migracje bazy"
    run_server "$1" --migrate-only || die "--migrate-only nieudane"
}

start_services() {
    info "Uruchamianie usług"
    systemctl enable zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-backup.timer
    systemctl restart zdrowieinfo.service
    systemctl start zdrowieinfo-update.path zdrowieinfo-backup.timer
    local i
    for ((i = 0; i < 40; i++)); do
        if curl -fs --max-time 5 "$(health_url)" >/dev/null 2>&1; then return 0; fi
        sleep 3
    done
    warn "serwer jeszcze nie odpowiada na $(health_url) (journalctl -u zdrowieinfo)"
}

print_summary() {
    local version
    version="$(installed_version)"
    cat <<EOF

ZdrowieInfo $version jest zainstalowane.

  Adres:           $PUBLIC_URL
  Aplikacja:       http://$LISTEN:$PORT (dla reverse proxy)
  Log:             journalctl -u zdrowieinfo -f
  Kopie:           codziennie o 3:30 w $INSTALL_ROOT/backups (14 ostatnich); install.sh --backup
  Aktualizacje:    $([[ -n "$GITHUB_REPOSITORY" ]] && echo "Panel → Aktualizacje (wydania $GITHUB_REPOSITORY)" || echo "install.sh --upgrade --package PLIK")

Reverse proxy (po Twojej stronie): HTTPS → http://127.0.0.1:$PORT, z nagłówkami Host, X-Forwarded-For i
X-Forwarded-Proto oraz WebSocket (panel to Blazor Server). Przy pierwszym logowaniu aplikacja poprosi o konfigurację
aplikacji uwierzytelniającej (TOTP). Klucz Claude API: Panel → Ustawienia.

EOF
}

# ------------------------------------------------------------------------------------------------ tryby

do_install() {
    collect_settings
    # Wszystko, czego może brakować bez pytań, sprawdzane przed zmianą serwera.
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ -n "$PACKAGE" || -n "$GITHUB_REPOSITORY" ]] || die "brak --package PLIK (ten install.sh nie ma repozytorium wydań)"
        [[ "$DB_MODE" == local || "$DB_MODE" == existing ]] || die "brak albo niepoprawne --db=local|existing"
        if [[ "$DB_MODE" == existing ]]; then
            [[ -n "$DB_HOST" ]] || die "brak --db-host"
            [[ -n "$DB_PASSWORD" ]] || die "brak DB_PASSWORD (plik sekretów, stdin albo ZDROWIEINFO_DB_PASSWORD)"
        fi
    fi
    collect_admin_account

    install_packages
    obtain_package
    # Baza przed układem katalogów: nieudany test połączenia nie zostawia nic w /opt/zdrowieinfo.
    setup_database
    create_user
    create_layout
    unpack_release
    zi_switch_current "$INSTALL_ROOT/releases/$PKG_VERSION"
    write_config
    write_update_env
    # Rozpakowane w tym przebiegu z paczki administratora (unpack_release zastępuje wcześniejszy katalog tej wersji).
    install_deploy_files "$INSTALL_ROOT/releases/$PKG_VERSION"
    migrate "$INSTALL_ROOT/current"
    create_admin_account
    # install.state oznacza skończoną instalację; bez niego ponowne uruchomienie zaczyna od nowa zamiast proponować naprawę.
    write_state
    INSTALL_DONE=1
    start_services
    print_summary
}

load_installed_settings() {
    PUBLIC_URL="$(read_state PUBLIC_URL || true)"
    PORT="$(read_state PORT || echo 5180)"
    LISTEN="$(read_state LISTEN || echo 127.0.0.1)"
    NO_PDF="$(read_state NO_PDF || echo "$NO_PDF")"
}

do_repair() {
    local version
    version="$(installed_version)" || die "brak działającej wersji do naprawy"
    info "Naprawa ZdrowieInfo $version (konfiguracja i dane zostają)"
    load_installed_settings
    # update.sh, backup.sh i jednostki działają jako root, więc nigdy nie pochodzą z current/. Z --package albo --version
    # są brane z tej paczki (po tych samych kontrolach co przy instalacji); bez – zostają zainstalowane.
    local missing pkgdir=""
    if [[ -z "$PACKAGE" && -z "$VERSION_ARG" ]]; then
        missing="$(missing_deploy_files)"
        [[ -z "$missing" ]] || die "brak: ${missing//$'\n'/, }. Uruchom install.sh --repair --package PLIK (albo --version $version), żeby zainstalować je ze sprawdzonej paczki"
    fi
    install_packages
    if [[ -n "$PACKAGE" || -n "$VERSION_ARG" ]]; then
        obtain_package
        [[ "$PKG_VERSION" == "$version" ]] ||
            warn "paczka to $PKG_VERSION, działa $version: z paczki brane są tylko update.sh, backup.sh i jednostki"
        pkgdir="$WORK/pkg"
        ZI_ERROR=""
        zi_safe_unpack "$PKG_FILE" "$pkgdir" || die "niebezpieczna albo uszkodzona paczka: $ZI_ERROR"
    else
        # Funkcje z zainstalowanego update.sh (root, sprawdzony przy instalacji).
        # shellcheck source=update.sh
        source "$INSTALL_ROOT/bin/update.sh"
        set -Eeuo pipefail
        trap 'die "install.sh przerwany w linii $LINENO"' ERR
    fi
    create_user
    fix_permissions
    if [[ -n "$pkgdir" ]]; then
        install_deploy_files "$pkgdir"
    else
        info "Zostawiam zainstalowane update.sh, backup.sh i jednostki (--package albo --version, żeby je zastąpić)"
        systemctl daemon-reload
    fi
    [[ -f "$UPDATE_ENV" ]] || warn "brak $UPDATE_ENV: aktualizacje i kopie nie zadziałają, dopóki go nie będzie"
    [[ -f "$CONFIG_FILE" ]] || die "brak $CONFIG_FILE; przywróć go albo usuń instalację (--uninstall) i zainstaluj ponownie"
    migrate "$INSTALL_ROOT/current"
    start_services
    print_summary
}

do_upgrade() {
    local current
    current="$(installed_version)" || die "brak działającej wersji do aktualizacji"
    load_installed_settings
    install_packages
    obtain_package
    zi_version_gt "$PKG_VERSION" "$current" || die "$PKG_VERSION nie jest nowsza niż zainstalowana $current (bez cofania wersji)"
    [[ -f "$UPDATE_ENV" ]] || die "brak $UPDATE_ENV; najpierw --repair"
    info "Aktualizacja $current do $PKG_VERSION przez update.sh (kopia bazy, migracje, sprawdzenie, wycofanie przy błędzie)"
    fix_permissions

    # Nowy update.sh i jednostki z paczki (wybranej przez administratora uruchamiającego ten skrypt).
    local pkgdir="$WORK/pkg"
    ZI_ERROR=""
    zi_safe_unpack "$PKG_FILE" "$pkgdir" || die "niebezpieczna albo uszkodzona paczka: $ZI_ERROR"
    install_deploy_files "$pkgdir"

    # Paczka przygotowana tak jak robi to serwer, potem jednostka aktualizacji i czekanie na nią. data/updates należy do
    # użytkownika usługi, więc to on tam pisze (root tylko przekazuje treść na stdin).
    local stage="$INSTALL_ROOT/data/updates/$PKG_VERSION" name sha run_id
    name="$(basename "$PKG_FILE")"
    as_service_user rm -rf -- "$stage"
    as_service_user mkdir -m 700 -- "$stage" || die "nie można utworzyć $stage"
    put_as_service_user "$stage/$name" <"$PKG_FILE" || die "nie można przygotować paczki"
    sha="$(sha256_of "$PKG_FILE")"
    printf '%s  %s\n' "$sha" "$name" | put_as_service_user "$stage/SHA256SUMS.txt" || die "nie można przygotować SHA256SUMS.txt"
    run_id="$(new_uuid)"
    printf '%s\n' "# Zapisane przez install.sh --upgrade" "format=1" "run_id=$run_id" "version=$PKG_VERSION" \
        "from_version=$current" "arch=$ARCH" "file=$name" "sha256=$sha" |
        put_as_service_user "$INSTALL_ROOT/data/updates/request.conf" || die "nie można zapisać zlecenia aktualizacji"
    systemctl start zdrowieinfo-update.service || true

    local now_version result="$INSTALL_ROOT/data/updates/result.conf"
    now_version="$(installed_version || true)"
    as_service_user grep -E '^(state|message)=' -- "$result" 2>/dev/null || true
    [[ "$now_version" == "$PKG_VERSION" ]] || die "aktualizacja się nie powiodła; zob. journalctl -u zdrowieinfo-update"
    print_summary
}

do_backup() {
    [[ -f "$STATE_FILE" ]] || die "ZdrowieInfo nie jest zainstalowane"
    [[ -x "$INSTALL_ROOT/bin/backup.sh" ]] || die "brak backup.sh; uruchom install.sh --repair --package PLIK (albo --version X.Y.Z)"
    local -a args=(create)
    [[ -z "$BACKUP_OUTPUT" ]] || args+=(--output "$BACKUP_OUTPUT")
    bash "$INSTALL_ROOT/bin/backup.sh" "${args[@]}"
}

do_restore() {
    [[ -f "$STATE_FILE" ]] || die "ZdrowieInfo nie jest tu zainstalowane: najpierw instalacja (ta sama albo nowsza wersja), potem --restore"
    [[ -n "$RESTORE_FILE" ]] || die "--restore wymaga pliku kopii"
    [[ -x "$INSTALL_ROOT/bin/backup.sh" ]] || die "brak backup.sh; uruchom install.sh --repair --package PLIK"
    local -a args=(restore "$RESTORE_FILE")
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ $ASSUME_YES -eq 1 ]] || die "--restore bez pytań wymaga --yes"
        args+=(--yes)
    fi
    bash "$INSTALL_ROOT/bin/backup.sh" "${args[@]}"
}

source_installed_functions() {
    [[ -f "$INSTALL_ROOT/bin/update.sh" && ! -L "$INSTALL_ROOT/bin/update.sh" ]] || die "brak $INSTALL_ROOT/bin/update.sh; uruchom install.sh --repair --package PLIK"
    # shellcheck source=update.sh
    source "$INSTALL_ROOT/bin/update.sh"
    set -Eeuo pipefail
    trap 'die "install.sh przerwany w linii $LINENO"' ERR
}

do_set_password() {
    [[ -f "$STATE_FILE" ]] || die "ZdrowieInfo nie jest zainstalowane"
    source_installed_functions
    if [[ -z "$RESET_USER" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "--set-password bez pytań wymaga loginu"
        tty_read RESET_USER "Login konta"
    fi
    [[ "$RESET_USER" =~ ^[A-Za-z0-9._@+-]{1,64}$ ]] || die "niepoprawny login '$RESET_USER'"
    if [[ -z "$ADMIN_PASSWORD" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "brak ADMIN_PASSWORD (plik sekretów, stdin albo ZDROWIEINFO_ADMIN_PASSWORD)"
        while true; do
            tty_secret ADMIN_PASSWORD "Nowe hasło dla $RESET_USER ($PASSWORD_RULE)" confirm
            password_ok "$ADMIN_PASSWORD" && break
            echo "Hasło nie spełnia zasad ($PASSWORD_RULE)." >/dev/tty
            ADMIN_PASSWORD=""
        done
    fi
    password_ok "$ADMIN_PASSWORD" || die "hasło nie spełnia zasad ($PASSWORD_RULE)"
    # Hasło przez stdin, nigdy w linii poleceń.
    local out rc=0
    out="$(printf '%s\n' "$ADMIN_PASSWORD" | run_server "$INSTALL_ROOT/current" --set-password "$RESET_USER" 2>&1)" || rc=$?
    print_cli_output "$out"
    [[ $rc -eq 0 ]] || die "nie udało się ustawić hasła"
}

print_cli_output() { # komunikaty polecenia serwera bez linii logowania
    printf '%s\n' "$1" | grep -vE '^(info|warn|dbug|trce|fail|crit): |^      ' || true
}

do_reset_mfa() {
    [[ -f "$STATE_FILE" ]] || die "ZdrowieInfo nie jest zainstalowane"
    source_installed_functions
    [[ "$RESET_USER" =~ ^[A-Za-z0-9._@+-]{1,64}$ ]] || die "--reset-mfa wymaga loginu"
    local out rc=0
    out="$(run_server "$INSTALL_ROOT/current" --reset-mfa "$RESET_USER" 2>&1)" || rc=$?
    print_cli_output "$out"
    [[ $rc -eq 0 ]] || die "nie udało się wyłączyć weryfikacji dwuetapowej"
}

remove_program() { # program, jednostki i stan usług; konfiguracja, dane, kopie i baza zostają
    systemctl disable --now zdrowieinfo-update.path zdrowieinfo-backup.timer zdrowieinfo.service 2>/dev/null || true
    systemctl stop zdrowieinfo-update.service zdrowieinfo-backup.service 2>/dev/null || true
    local u
    for u in "${UNITS[@]}"; do rm -f -- "$UNIT_DIR/$u"; done
    systemctl daemon-reload
    rm -rf -- "${INSTALL_ROOT:?}/releases" "${INSTALL_ROOT:?}/bin"
    rm -f -- "$INSTALL_ROOT/current"
}

drop_local_database() { # drop_local_database NAZWA ROLA PORT: tylko własna baza i rola ZdrowieInfo
    local name="$1" role="$2" port="$3" owner super
    [[ "$name" =~ ^[A-Za-z0-9_]+$ && "$role" =~ ^[A-Za-z0-9_]+$ && "$port" =~ ^[0-9]+$ ]] ||
        { warn "nieoczekiwane ustawienia bazy w $STATE_FILE; baza nie została usunięta"; return 0; }
    case "$name" in postgres|template0|template1) warn "odmowa usunięcia '$name'"; return 0 ;; esac
    [[ "$role" != postgres ]] || { warn "odmowa usunięcia roli postgres"; return 0; }
    zi_pgq() { runuser -u postgres -- psql -X -At -q -v ON_ERROR_STOP=1 -p "$port" -d postgres -c "$1"; }
    if ! super="$(zi_pgq "SELECT rolsuper FROM pg_roles WHERE rolname = '$role'")"; then
        warn "PostgreSQL na porcie $port jest niedostępny; usuń bazę '$name' i rolę '$role' samodzielnie"; return 0
    fi
    [[ "$super" != t ]] || { warn "rola '$role' jest superużytkownikiem: nie utworzyło jej ZdrowieInfo, zostaje"; return 0; }
    owner="$(zi_pgq "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname = '$name'")" ||
        { warn "nie można sprawdzić bazy '$name'; nie została usunięta"; return 0; }
    if [[ -z "$owner" ]]; then
        info "Baza '$name' już nie istnieje"
    elif [[ "$owner" != "$role" ]]; then
        warn "właścicielem bazy '$name' jest '$owner', nie '$role': zostaje"
        return 0
    else
        info "Usuwanie bazy '$name' (PostgreSQL na porcie $port; inne bazy nietknięte)"
        zi_pgq "DROP DATABASE \"$name\" WITH (FORCE)" >/dev/null || warn "nie udało się usunąć bazy '$name'"
    fi
    if [[ -n "$super" ]]; then
        zi_pgq "DROP ROLE \"$role\"" >/dev/null ||
            warn "nie udało się usunąć roli '$role' (może mieć obiekty w innych bazach); zostaje"
    fi
}

do_uninstall() {
    [[ $EUID -eq 0 ]] || die "uruchom jako root: sudo bash $0"
    if [[ $KEEP_DATA -eq 1 ]]; then
        info "Usuwanie programu ZdrowieInfo i jednostek (konfiguracja, dane, kopie i baza zostają)"
        remove_program
        info "Zostają: $INSTALL_ROOT/config, $INSTALL_ROOT/data, $INSTALL_ROOT/backups, baza i użytkownik $SERVICE_USER"
        return 0
    fi

    local db_mode db_name db_user db_port
    db_mode="$(read_state DB_MODE || true)"
    db_name="$(read_state DB_NAME || echo zdrowieinfo)"
    db_user="$(read_state DB_USER || echo zdrowieinfo)"
    db_port="$(read_state DB_PORT || true)"
    if [[ -z "$db_port" && -f "$UPDATE_ENV" ]]; then db_port="$(sed -n 's/^PGPORT=//p' "$UPDATE_ENV" | head -n1)"; fi
    db_port="${db_port:-5432}"
    cat <<EOF

  !!  Odinstalowanie usuwa z tego serwera WSZYSTKO:
      - program i jego usługi
      - $INSTALL_ROOT: konfigurację, klucze (zaszyfrowany klucz Claude API, VAPID), dane i kopie zapasowe
      - BAZĘ '$db_name' ze wszystkimi raportami, wpisami, kontami i ustawieniami
EOF
    if [[ "$db_mode" == local ]]; then
        echo "        (usuwane są tylko baza '$db_name' i rola '$db_user'; sam PostgreSQL oraz inne bazy i role zostają)"
    elif [[ "$db_mode" == existing ]]; then echo "        (wszystkie tabele w '$db_name' na zewnętrznym serwerze PostgreSQL są usuwane; baza i rola zostają)"; fi
    echo "      Tego nie da się cofnąć. Jeśli baza może się przydać, najpierw zrób kopię."
    echo

    local backup_file="$BACKUP_TO"
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ $ASSUME_YES -eq 1 ]] || die "--uninstall bez pytań wymaga --yes (i opcjonalnie --backup-to PLIK)"
    elif [[ -z "$backup_file" && -f "$UPDATE_ENV" ]]; then
        if ask_yes_no "Zrobić kopię (baza, konfiguracja, klucze, program) przed odinstalowaniem?" t; then
            tty_read backup_file "Plik kopii (poza $INSTALL_ROOT, który zostanie usunięty)" "/root/zdrowieinfo-backup-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
        fi
    fi
    if [[ -n "$backup_file" ]]; then
        [[ "$backup_file" == *.tar.gz ]] || backup_file="$backup_file.tar.gz"
        [[ "$(readlink -m -- "$backup_file")" != "$INSTALL_ROOT"/* ]] || die "kopia musi być poza $INSTALL_ROOT (zostanie usunięty)"
        BACKUP_OUTPUT="$backup_file"
        do_backup || die "kopia nieudana; nic nie usunięto"
    fi
    if [[ $UNATTENDED -eq 0 ]]; then
        local answer
        read -r -p "Wpisz 'odinstaluj', aby usunąć ZdrowieInfo i jego bazę: " answer </dev/tty || die "nie potwierdzono"
        [[ "$answer" == odinstaluj ]] || die "nie potwierdzono"
    fi

    info "Usuwanie ZdrowieInfo"
    remove_program
    if [[ "$db_mode" == local ]] && command -v psql >/dev/null 2>&1; then
        drop_local_database "$db_name" "$db_user" "$db_port"
    elif [[ "$db_mode" == existing && -f "$UPDATE_ENV" ]]; then
        # Rola jest właścicielem bazy: usuwane jest wszystko, co utworzyło w niej ZdrowieInfo.
        ( set -a; while IFS= read -r line; do
              [[ "$line" =~ ^(PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE)=(.*)$ ]] && printf -v "${BASH_REMATCH[1]}" '%s' "${BASH_REMATCH[2]}"
          done <"$UPDATE_ENV"; set +a
          psql -X -q -v ON_ERROR_STOP=1 -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' ) ||
            warn "nie udało się wyczyścić zewnętrznej bazy '$db_name'; usuń ją samodzielnie"
    fi
    rm -rf -- "$INSTALL_ROOT"
    if id -u "$SERVICE_USER" >/dev/null 2>&1; then userdel "$SERVICE_USER" || warn "nie udało się usunąć użytkownika $SERVICE_USER"; fi
    info "ZdrowieInfo usunięte. Pakiety PostgreSQL i TeX zostały."
    [[ -z "$backup_file" ]] || info "Kopia: $backup_file"
}

main() {
    parse_args "$@"
    if [[ "$MODE" == uninstall ]]; then load_secrets; do_uninstall; return; fi
    check_system
    check_legacy
    load_secrets
    case "$MODE" in
        backup) do_backup; return ;;
        restore) do_restore; return ;;
        set-password) do_set_password; return ;;
        reset-mfa) do_reset_mfa; return ;;
    esac
    local installed=0
    if [[ -f "$STATE_FILE" ]]; then
        installed=1
    elif [[ -L "$INSTALL_ROOT/current" || -f "$CONFIG_FILE" ]]; then
        # Poprzednia instalacja przerwana w połowie (install.state zapisywany jest na końcu). Od nowa; jej konfiguracja
        # jest zastępowana (może mieć nieaktualne hasło bazy). data/ zostaje.
        [[ -z "$MODE" ]] || die "poprzednia instalacja nie skończyła się; uruchom bez --$MODE, żeby zainstalować ponownie, albo --uninstall"
        warn "poprzednia instalacja nie skończyła się; instaluję ponownie (konfiguracja zastąpiona, data/ zostaje)"
        if [[ $UNATTENDED -eq 0 ]]; then ask_yes_no "Kontynuować?" t || exit 0; fi
        rm -f -- "$CONFIG_FILE" "$UPDATE_ENV" "$INSTALL_ROOT/current"
    fi
    if [[ $installed -eq 1 ]]; then
        if [[ -z "$MODE" ]]; then
            [[ $UNATTENDED -eq 0 ]] || die "ZdrowieInfo jest już zainstalowane: użyj --repair, --upgrade, --backup, --restore, --set-password, --reset-mfa albo --uninstall"
            echo "ZdrowieInfo $(installed_version || echo '?') jest zainstalowane. Co zrobić?"
            echo "  1) naprawa             (usługi, właściciele i prawa; konfiguracja i dane zostają)"
            echo "  2) aktualizacja        (do podanej paczki albo wydania)"
            echo "  3) kopia zapasowa      (baza, konfiguracja, klucze, program)"
            echo "  4) przywrócenie kopii"
            echo "  5) nowe hasło konta"
            echo "  6) usunięcie weryfikacji dwuetapowej konta (np. zgubiony telefon)"
            echo "  7) odinstalowanie      (usuwa wszystko, także bazę)"
            echo "  8) anuluj"
            local choice
            tty_read choice "Wybór" 8
            case "$choice" in
                1) MODE=repair ;;
                2) MODE=upgrade ;;
                3) tty_read BACKUP_OUTPUT "Katalog albo plik .tar.gz kopii" "$INSTALL_ROOT/backups"; do_backup; return ;;
                4) tty_read RESTORE_FILE "Plik kopii do przywrócenia"; do_restore; return ;;
                5) do_set_password; return ;;
                6) tty_read RESET_USER "Login konta"; do_reset_mfa; return ;;
                7) do_uninstall; return ;;
                *) exit 0 ;;
            esac
        fi
    else
        [[ -z "$MODE" ]] || die "ZdrowieInfo nie jest zainstalowane; uruchom bez --$MODE"
        MODE=install
    fi
    case "$MODE" in
        install) do_install ;;
        repair) do_repair ;;
        upgrade) do_upgrade ;;
    esac
}

# Tylko przy uruchomieniu (testy mogą wczytać plik, żeby wywołać pojedyncze kroki).
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
