#!/usr/bin/env bash
# Daily backup of Soford ERP data (products, jobs, encrypted token, data-protection keys) and its env file.
# The data-protection keys must travel with the token file: without them the stored token cannot be decrypted.
set -euo pipefail

APP_ROOT="${APP_ROOT:-/opt/soford-erp}"
BACKUP_DIR="${BACKUP_DIR:-$APP_ROOT/backups}"
KEEP="${KEEP:-14}"

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

stamp="$(date +%Y%m%d-%H%M%S)"
target="$BACKUP_DIR/soford-erp-$stamp.tar.gz"
# Exit code 1 means "a file changed while reading" (atomic-write temp files); the archive is still usable.
set +e
tar --warning=no-file-changed --ignore-failed-read --exclude="*.tmp" -czf "$target" -C "$APP_ROOT" app_data env
status=$?
set -e
if [ "$status" -gt 1 ]; then
  echo "tar failed with status $status" >&2
  rm -f "$target"
  exit "$status"
fi
chmod 600 "$target"

# Keep the newest $KEEP archives.
ls -1t "$BACKUP_DIR"/soford-erp-*.tar.gz 2>/dev/null | tail -n +"$((KEEP + 1))" | xargs -r rm -f

echo "Backup written: $target"

# Optional off-site copy: a disk failure takes local backups with it. Configure by installing an executable
# $APP_ROOT/env/backup-offsite.sh (see $APP_ROOT/bin/backup-offsite.example.sh); it receives the archive path.
OFFSITE="$APP_ROOT/env/backup-offsite.sh"
if [ -x "$OFFSITE" ]; then
  if "$OFFSITE" "$target"; then
    echo "Off-site copy done."
  else
    echo "Off-site copy FAILED; the local archive is kept." >&2
    exit 1
  fi
fi
