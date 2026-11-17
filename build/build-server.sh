#!/usr/bin/env bash
# Buduje paczkę serwera: self-contained linux-x64 + pliki instalacyjne.
# Wynik: artifacts/zdrowieinfo-<wersja>-linux-x64.tar.gz
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
NAME="zdrowieinfo-$VERSION-linux-x64"
OUT="$ROOT/artifacts/$NAME"

rm -rf "$OUT" "$OUT.tar.gz"
mkdir -p "$OUT"

dotnet test "$ROOT/tests/ZdrowieInfo.Server.Tests"

dotnet publish "$ROOT/src/ZdrowieInfo.Server" \
    -c Release -r linux-x64 --self-contained true \
    -p:DebugType=none -p:GenerateDocumentationFile=false \
    -o "$OUT/app"

# Kontrola: zasoby Blazora muszą trafić do wwwroot (w Development są czytane z pakietów).
test -n "$(ls "$OUT"/app/wwwroot/_framework/blazor.web*.js 2>/dev/null)" || { echo "Brak _framework/blazor.web.js w publikacji" >&2; exit 1; }

cp "$ROOT"/deploy/linux/* "$OUT/"
chmod +x "$OUT"/*.sh
tar -C "$ROOT/artifacts" -czf "$OUT.tar.gz" "$NAME"
echo "Gotowe: $OUT.tar.gz"
