#!/usr/bin/env bash
# Kopia zapasowa ZdrowieInfo: zrzut bazy + klucze Data Protection (bez nich zaszyfrowane ustawienia są nie do odczytania).
# Uruchamiane codziennie przez zdrowieinfo-backup.timer. Trzyma 14 ostatnich kopii.
set -euo pipefail
DATA_DIR=/var/lib/zdrowieinfo
DEST=$DATA_DIR/backups
STAMP=$(date +%Y-%m-%d_%H%M)
umask 077
sudo -u postgres pg_dump -Fc zdrowieinfo > "$DEST/zdrowieinfo-$STAMP.dump"
tar -C "$DATA_DIR" -czf "$DEST/keys-$STAMP.tar.gz" keys
chown zdrowieinfo:zdrowieinfo "$DEST"/*-"$STAMP".*
ls -1t "$DEST"/zdrowieinfo-*.dump | tail -n +15 | xargs -r rm --
ls -1t "$DEST"/keys-*.tar.gz | tail -n +15 | xargs -r rm --
echo "Kopia: $DEST/zdrowieinfo-$STAMP.dump"
