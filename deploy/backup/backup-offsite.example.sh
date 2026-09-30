#!/usr/bin/env bash
# Example off-site copy for soford-erp-backup.sh. To enable:
#   sudo cp /opt/soford-erp/bin/backup-offsite.example.sh /opt/soford-erp/env/backup-offsite.sh
#   sudo chmod 700 /opt/soford-erp/env/backup-offsite.sh   # then edit it
# It receives the new archive as $1 and must exit non-zero on failure. The archive contains the Alibaba tokens,
# data-protection keys and env file (AppSecret, admin password): keep the destination private.
set -euo pipefail
archive="$1"

# Aliyun OSS with ossutil (https://help.aliyun.com/zh/oss/developer-reference/ossutil), configured once with
# `ossutil config` for root. Use a private bucket in another region, ideally with versioning or a lifecycle rule.
# ossutil cp "$archive" "oss://your-private-bucket/soford-erp/$(basename "$archive")"

# Or any rclone remote (https://rclone.org), configured with `rclone config`:
# rclone copy "$archive" "remote:soford-erp-backups/"

echo "backup-offsite.sh is not configured yet: edit it to copy $archive somewhere else." >&2
exit 1
