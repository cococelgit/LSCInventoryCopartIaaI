#!/bin/sh
set -eu

has_key=false
if [ -n "${AuctionsApi__ApiKey:-}" ]; then
  has_key=true
fi

enabled=false
case "${AuctionsApi__Enabled:-}" in
  true|True|TRUE|1) enabled=true ;;
esac

allow_writes=false
case "${AuctionsApi__AllowWrites:-}" in
  true|True|TRUE|1) allow_writes=true ;;
esac

printf '{"AuctionsApiEnabled":%s,"AuctionsApiHasKey":%s,"AuctionsApiAllowWrites":%s}\n' "$enabled" "$has_key" "$allow_writes"
