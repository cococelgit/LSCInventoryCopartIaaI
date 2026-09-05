#!/bin/sh
set -eu

platform="${1:-iaai}"
case "${AuctionsApi__Enabled:-}" in true|TRUE|1|yes|YES) enabled=true ;; *) enabled=false ;; esac
if [ -n "${AuctionsApi__ApiKey:-}" ]; then has_key=true; else has_key=false; fi
case "${AuctionsApi__AllowWrites:-}" in true|TRUE|1|yes|YES) allow_writes=true ;; *) allow_writes=false ;; esac
echo "AuctionsAPI_CONFIG enabled=$enabled has_api_key=$has_key allow_writes=$allow_writes platform=$platform"
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
