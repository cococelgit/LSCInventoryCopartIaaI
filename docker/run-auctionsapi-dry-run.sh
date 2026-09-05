#!/bin/sh
set -eu

platform="${1:-iaai}"
case "$platform" in
  copart)
    exec dotnet /app/Lsc.Inventory.Api.dll --copart-auctionsapi-dry-run
    ;;
  iaai)
    maximum="${2:-15}"
    case "$maximum" in
      ''|*[!0-9]*)
        echo "maximum must be numeric" >&2
        exit 64
        ;;
    esac
    exec dotnet /app/Lsc.Inventory.Api.dll --iaai-auctionsapi-backfill-dry-run --maximum "$maximum"
    ;;
  *)
    echo "platform must be copart or iaai" >&2
    exit 64
    ;;
esac
