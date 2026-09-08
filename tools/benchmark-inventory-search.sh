#!/usr/bin/env bash
set -euo pipefail

: "${INVENTORY_BASE_URL:?Set INVENTORY_BASE_URL to the API root, for example https://inventory-api-stg.example.com}"
: "${INVENTORY_API_TOKEN:?Set INVENTORY_API_TOKEN}"
: "${AUCTION_FROM:?Set AUCTION_FROM as an ISO-8601 UTC timestamp}"

samples="${SAMPLES:-20}"
page_size="${PAGE_SIZE:-24}"
output="${OUTPUT:-search-benchmark.tsv}"
url="${INVENTORY_BASE_URL%/}/api/v1/inventory/search?auctionFrom=$(printf '%s' "$AUCTION_FROM" | jq -sRr @uri)&excludeSpecialTitles=true&page=1&pageSize=$page_size&sort=updated-desc"

printf 'sample\tttfb_seconds\ttotal_seconds\ttotal_results\torder_sha256\n' > "$output"
for sample in $(seq 1 "$samples"); do
  body="$(mktemp)"
  timings="$(curl -sS -H "Authorization: Bearer $INVENTORY_API_TOKEN" -o "$body" -w '%{time_starttransfer}\t%{time_total}' "$url")"
  total="$(jq -r '.total' "$body")"
  order_hash="$(jq -r '.vehicles[] | [.platform,.lot] | @tsv' "$body" | sha256sum | cut -d' ' -f1)"
  printf '%s\t%s\t%s\t%s\t%s\n' "$sample" "${timings%%$'\t'*}" "${timings##*$'\t'}" "$total" "$order_hash" >> "$output"
  rm -f "$body"
done

tail -n +2 "$output" | cut -f3 | sort -n | awk '
  { values[NR]=$1; sum+=$1 }
  END {
    p50=values[int((NR-1)*0.50)+1];
    p95=values[int((NR-1)*0.95)+1];
    printf "samples=%d min=%.3f p50=%.3f p95=%.3f max=%.3f mean=%.3f\n", NR,values[1],p50,p95,values[NR],sum/NR
  }'

distinct_totals="$(tail -n +2 "$output" | cut -f4 | sort -u | wc -l)"
distinct_orders="$(tail -n +2 "$output" | cut -f5 | sort -u | wc -l)"
printf 'distinct_totals=%s distinct_order_fingerprints=%s output=%s\n' "$distinct_totals" "$distinct_orders" "$output"
