#!/bin/sh
set -eu

maximum="${1:-15}"
case "$maximum" in
  ''|*[!0-9]*)
    echo "maximum must be numeric" >&2
    exit 64
    ;;
esac

exec dotnet /app/Lsc.Inventory.Api.dll \
  --iaai-auctionsapi-backfill-dry-run \
  --maximum "$maximum"

