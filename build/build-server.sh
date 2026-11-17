#!/usr/bin/env bash
# Buduje paczki wydania serwera ZdrowieInfo:
#   artifacts/ZdrowieInfo.Server-<wersja>-linux-x64.tar.gz
#   artifacts/ZdrowieInfo.Server-<wersja>-linux-arm64.tar.gz
#   artifacts/install.sh
#   artifacts/SHA256SUMS.txt   (paczki i install.sh)
#
# Paczka: serwer self-contained w katalogu głównym (bez katalogu nadrzędnego, pliki roota) oraz deploy/linux/
# (install.sh, update.sh, backup.sh, jednostki systemd). update.sh rozpakowuje ją do /opt/zdrowieinfo/releases/<wersja>/.
#
# --repository WŁAŚCICIEL/NAZWA: repozytorium GitHub z wydaniami, wpisywane do install.sh (w paczce i obok niej – te same
# bajty) – z niego install.sh pobiera wydania, a serwer sprawdza aktualizacje. Domyślnie $GITHUB_REPOSITORY (ustawia je
# GitHub Actions), a lokalnie BarPos/ZdrowieInfo. --repository "" – bez repozytorium (paczki testowe: install.sh działa
# tylko z --package, a serwer nie łączy się z GitHubem).
#
# Użycie: build/build-server.sh [--arch x64|arm64]... [--repository WŁAŚCICIEL/NAZWA] [--output KATALOG]
# Działa na Linuksie i macOS (publikacja krzyżowa). Wymaga .NET SDK z global.json.
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts"
ARCHES=()
DEFAULT_REPOSITORY="BarPos/ZdrowieInfo"
REPOSITORY="${GITHUB_REPOSITORY:-$DEFAULT_REPOSITORY}"

usage() { sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "build-server.sh: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) [[ $# -ge 2 ]] || die "--arch wymaga wartości"; ARCHES+=("$2"); shift 2 ;;
        --arch=*) ARCHES+=("${1#*=}"); shift ;;
        --repository) [[ $# -ge 2 ]] || die "--repository wymaga wartości"; REPOSITORY="$2"; shift 2 ;;
        --repository=*) REPOSITORY="${1#*=}"; shift ;;
        --output) [[ $# -ge 2 ]] || die "--output wymaga wartości"; OUT="$2"; shift 2 ;;
        --output=*) OUT="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "nieznany argument: $1 (zob. --help)" ;;
    esac
done
[[ ${#ARCHES[@]} -gt 0 ]] || ARCHES=(x64 arm64)
for a in "${ARCHES[@]}"; do [[ "$a" == x64 || "$a" == arm64 ]] || die "nieobsługiwana architektura '$a' (x64 albo arm64)"; done
[[ -z "$REPOSITORY" || "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "--repository musi mieć postać właściciel/nazwa"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)"
[[ "$VERSION" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "<Version> w Directory.Build.props ('$VERSION') nie ma postaci x.y.z"
APP_VERSION="$(sed -n 's:.*const string Current = "\([^"]*\)".*:\1:p' "$ROOT/src/ZdrowieInfo.Server.Core/Common/AppVersion.cs" | head -n1)"
[[ "$APP_VERSION" == "$VERSION" ]] || die "AppVersion.Current ($APP_VERSION) różni się od Directory.Build.props ($VERSION)"

if command -v sha256sum >/dev/null 2>&1; then SHA256=(sha256sum); else SHA256=(shasum -a 256); fi

# Przenośny tar: wpisy roota, bez metadanych macOS (AppleDouble, xattr).
if tar --version 2>/dev/null | grep -q 'GNU tar'; then
    TAR_CREATE=(tar --owner=0 --group=0 --numeric-owner --sort=name -czf)
else
    export COPYFILE_DISABLE=1
    TAR_CREATE=(tar --uid 0 --gid 0 --uname root --gname root --no-xattrs --no-mac-metadata --no-acls -czf)
fi

DEPLOY_FILES=(install.sh update.sh backup.sh zdrowieinfo.service zdrowieinfo-update.path zdrowieinfo-update.service
              zdrowieinfo-backup.service zdrowieinfo-backup.timer)
for f in "${DEPLOY_FILES[@]}"; do [[ -f "$ROOT/deploy/linux/$f" ]] || die "brak deploy/linux/$f"; done

# install.sh z wpisanym repozytorium (linia GITHUB_REPOSITORY="" musi wystąpić dokładnie raz).
INSTALL_SH="$OUT/install.sh"
[[ "$(grep -c '^GITHUB_REPOSITORY=""$' "$ROOT/deploy/linux/install.sh")" == 1 ]] || die 'deploy/linux/install.sh nie ma linii GITHUB_REPOSITORY=""'
sed "s|^GITHUB_REPOSITORY=\"\"\$|GITHUB_REPOSITORY=\"$REPOSITORY\"|" "$ROOT/deploy/linux/install.sh" >"$INSTALL_SH"
chmod 755 "$INSTALL_SH"
echo "==> install.sh: repozytorium wydań '${REPOSITORY:-(brak – tylko --package)}'"

for arch in "${ARCHES[@]}"; do
    rid="linux-$arch"
    name="ZdrowieInfo.Server-$VERSION-$rid.tar.gz"
    pub="$OUT/publish/$rid"
    echo "==> Publikacja $rid ($VERSION)"
    rm -rf "$pub"
    dotnet publish "$ROOT/src/ZdrowieInfo.Server/ZdrowieInfo.Server.csproj" \
        -c Release -r "$rid" --self-contained true \
        -p:DebugType=None -p:DebugSymbols=false -p:GenerateDocumentationFile=false -p:Version="$VERSION" \
        -o "$pub" --nologo
    [[ -f "$pub/ZdrowieInfo.Server" ]] || die "w publikacji nie ma programu ZdrowieInfo.Server"
    rm -f "$pub/appsettings.Development.json"
    # Zasoby Blazora muszą trafić do wwwroot (w Development są czytane z pakietów).
    compgen -G "$pub/wwwroot/_framework/blazor.web*.js" >/dev/null || die "brak wwwroot/_framework/blazor.web*.js w publikacji"

    mkdir -p "$pub/deploy/linux"
    for f in "${DEPLOY_FILES[@]}"; do cp "$ROOT/deploy/linux/$f" "$pub/deploy/linux/$f"; done
    cp "$INSTALL_SH" "$pub/deploy/linux/install.sh"

    # Tylko zwykłe pliki i katalogi; pisać może tylko właściciel.
    if find "$pub" ! -type f ! -type d | grep -q .; then die "publikacja zawiera dowiązania albo pliki specjalne"; fi
    find "$pub" -type d -exec chmod 755 {} +
    find "$pub" -type f -exec chmod 644 {} +
    chmod 755 "$pub/ZdrowieInfo.Server" "$pub/deploy/linux/install.sh" "$pub/deploy/linux/update.sh" "$pub/deploy/linux/backup.sh"
    find "$pub" -type f -name '*.so' -exec chmod 755 {} +
    [[ -f "$pub/createdump" ]] && chmod 755 "$pub/createdump"

    echo "==> Pakowanie $name"
    rm -f "$OUT/$name"
    (cd "$pub" && "${TAR_CREATE[@]}" "$OUT/$name" .)
done

echo "==> SHA256SUMS.txt"
(
    cd "$OUT"
    files=()
    for f in "ZdrowieInfo.Server-$VERSION-linux-x64.tar.gz" "ZdrowieInfo.Server-$VERSION-linux-arm64.tar.gz" install.sh; do
        [[ -f "$f" ]] && files+=("$f")
    done
    "${SHA256[@]}" "${files[@]}" > SHA256SUMS.txt
    cat SHA256SUMS.txt
)
echo "==> Gotowe: $OUT"
