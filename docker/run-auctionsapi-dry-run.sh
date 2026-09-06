#!/bin/sh
set -eu

platform="${1:-iaai}"
case "${AuctionsApi__Enabled:-}" in true|TRUE|1|yes|YES) enabled=true ;; *) enabled=false ;; esac
if [ -n "${AuctionsApi__ApiKey:-}" ]; then has_key=true; else has_key=false; fi
case "${AuctionsApi__AllowWrites:-}" in true|TRUE|1|yes|YES) allow_writes=true ;; *) allow_writes=false ;; esac
now_utc() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
phase() { echo "CATCHUP_WRAPPER phase=$1 utc=$(now_utc)"; }
phase "startup"
echo "AuctionsAPI_CONFIG enabled=$enabled has_api_key=$has_key allow_writes=$allow_writes platform=$platform"
case "$platform" in
  copart)
    mode="${2:-dry-run}"
    maximum="${3:-}"
    case "$mode" in
      run) flag="--copart-auctionsapi-run" ;;
      dry-run) flag="--copart-auctionsapi-dry-run" ;;
      catch-up) flag="--copart-auctionsapi-catch-up" ;;
      catch-up-dry-run) flag="--copart-auctionsapi-catch-up-dry-run" ;;
      *) echo "Unsupported Copart mode: $mode" >&2; exit 2 ;;
    esac
    phase "before_dotnet flag=$flag mode=$mode maximum=${maximum:-default}"
    if [ -n "$maximum" ]; then
      exec dotnet /app/Lsc.Inventory.Api.dll "$flag" --maximum "$maximum"
    fi
    exec dotnet /app/Lsc.Inventory.Api.dll "$flag"
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
