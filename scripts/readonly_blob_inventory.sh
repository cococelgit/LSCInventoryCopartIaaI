#!/bin/sh
set -eu

ACCOUNT="${BLOB_ACCOUNT:-stlscinvprodeus2}"
CONTAINER="${BLOB_CONTAINER:-raw-apibara}"
PREFIX="${BLOB_PREFIX:-snapshots/}"
MAX_PAGES="${MAX_PAGES:-20000}"
TOP_N="${TOP_N:-100}"
API_VERSION="2023-11-03"
TOKEN_URL="http://169.254.169.254/metadata/identity/oauth2/token?api-version=2019-08-01&resource=https%3A%2F%2Fstorage.azure.com%2F"

command -v curl >/dev/null 2>&1 || { echo 'ERROR curl_missing' >&2; exit 2; }
command -v awk >/dev/null 2>&1 || { echo 'ERROR awk_missing' >&2; exit 2; }
command -v sort >/dev/null 2>&1 || { echo 'ERROR sort_missing' >&2; exit 2; }

TOKEN_JSON="$(curl -fsS --max-time 10 -H Metadata:true "$TOKEN_URL")"
TOKEN="$(printf '%s' "$TOKEN_JSON" | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')"
test -n "$TOKEN" || { echo 'ERROR managed_identity_token_missing' >&2; exit 3; }

TMP="/tmp/lsc-blob-audit.$$"
trap 'rm -rf "$TMP"' EXIT INT TERM
mkdir -p "$TMP"
: > "$TMP/pages"

marker=""
page=0
while :; do
  page=$((page + 1))
  test "$page" -le "$MAX_PAGES" || { echo "ERROR page_limit=$MAX_PAGES" >&2; exit 4; }
  query="restype=container&comp=list&include=metadata"
  if [ -n "$PREFIX" ]; then query="$query&prefix=$(printf '%s' "$PREFIX" | sed 's/%/%25/g; s|/|%2F|g')"; fi
  if [ -n "$marker" ]; then query="$query&marker=$(printf '%s' "$marker" | sed 's/%/%25/g; s|/|%2F|g; s/+/%2B/g')"; fi
  url="https://${ACCOUNT}.blob.core.windows.net/${CONTAINER}?${query}"
  xml="$TMP/page.xml"
  curl -fsS --max-time 120 -H "Authorization: Bearer $TOKEN" -H "x-ms-version: $API_VERSION" "$url" -o "$xml"
  if [ $((page % 10)) -eq 0 ]; then printf 'PROGRESS_PAGE|%s\n' "$page"; fi

  # Normalize the XML to one tag/value per line. Only metadata is read.
  tr '<' '\n<' < "$xml" | sed 's/>/>&\n/g' > "$TMP/page.tags"
  awk '
    /<Name>/ { name=$0; sub(/^.*<Name>/,"",name); sub(/<\/Name>.*$/,"",name) }
    /<Content-Length>/ { size=$0; sub(/^.*<Content-Length>/,"",size); sub(/<\/Content-Length>.*$/,"",size) }
    /<Last-Modified>/ { lm=$0; sub(/^.*<Last-Modified>/,"",lm); sub(/<\/Last-Modified>.*$/,"",lm) }
    /<Blob>/ { name=""; size=""; lm="" }
    /<\/Blob>/ {
      if (name != "") print name "|" (size == "" ? 0 : size) "|" lm
    }
  ' "$TMP/page.tags" >> "$TMP/blob.rows"

  next_marker="$(sed -n 's:.*<NextMarker>\(.*\)</NextMarker>.*:\1:p' "$xml" | head -1)"
  if [ -z "$next_marker" ]; then break; fi
  marker="$next_marker"
done

awk -v out="$TMP/lots" -F'|' '
function lotkey(path, a, n) {
  n=split(path,a,"/")
  if (n >= 4 && a[1] == "snapshots") {
    # New content-addressed layout: snapshots/{lot_key}/{hash}.json
    if (n == 4) return a[2]
    # Legacy layout: snapshots/YYYY/MM/DD/{lot_key}/{timestamp-hash}.json
    if (n >= 8) return a[5]
  }
  return "(unparsed)"
}
function hashpart(path, a, n, base, dash) {
  n=split(path,a,"/"); base=a[n]; sub(/\.json$/,"",base)
  dash=index(base,"-")
  if (dash > 0) return substr(base,dash+1)
  return base
}
{
  lot=lotkey($1); bytes=$2+0; h=hashpart($1)
  total_blobs++; total_bytes+=bytes
  count[lot]++; bytes_by_lot[lot]+=bytes; hashes[lot SUBSEP h]++; hash_bytes[lot SUBSEP h]+=bytes
  if (!(lot SUBSEP h in hash_min) || bytes < hash_min[lot SUBSEP h]) hash_min[lot SUBSEP h]=bytes
  if (count[lot] == 1 || bytes < min_bytes[lot]) min_bytes[lot]=bytes
  if (count[lot] == 1 || bytes > max_bytes[lot]) max_bytes[lot]=bytes
}
END {
  distinct_lots=0; duplicate_lots=0; duplicate_excess=0; duplicate_bytes=0
  for (key in hashes) {
    split(key, parts, SUBSEP); lot=parts[1]
    if (hashes[key] > 1) {
      duplicate_excess += hashes[key]-1
      duplicate_bytes += hash_bytes[key] - hash_min[key]
    }
  }
  for (lot in count) {
    distinct_lots++
    if (count[lot] > 1) duplicate_lots++
    # The per-lot column is exact only for identical-hash copies; it is not a purge recommendation.
    lot_duplicate_bytes=0
    for (key in hashes) {
      split(key, parts, SUBSEP)
      if (parts[1] == lot && hashes[key] > 1) lot_duplicate_bytes += hash_bytes[key] - hash_min[key]
    }
    print lot "|" count[lot] "|" bytes_by_lot[lot] "|" lot_duplicate_bytes "|" min_bytes[lot] "|" max_bytes[lot] > out
  }
  print "SUMMARY|" total_blobs "|" total_bytes "|" distinct_lots "|" duplicate_lots "|" duplicate_excess "|" duplicate_bytes
}
' "$TMP/blob.rows" > "$TMP/summary.txt"

# The temporary awk file name above is based on PPID; find it robustly.
LOT_FILE="$TMP/lots"
summary="$(grep '^SUMMARY|' "$TMP/summary.txt")"
test -n "$summary" || { echo 'ERROR summary_missing' >&2; exit 5; }
printf '%s\n' "$summary"
if [ -n "$LOT_FILE" ]; then
  printf '%s\n' 'TOP_DUPLICATED_LOTS'
  sort -t'|' -k4,4nr -k2,2nr "$LOT_FILE" | head -n "$TOP_N"
fi
printf 'PAGES|%s\n' "$page"
