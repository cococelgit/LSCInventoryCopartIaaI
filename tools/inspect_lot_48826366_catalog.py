#!/usr/bin/env python3
"""Inspect only aggregate media-catalog metadata; never print source URLs or query values."""
import csv
import json
import sys
from collections import Counter
from pathlib import Path

import requests

CSV_PATH = Path("/home/ubuntu/upload/salesdata.csv")
LOT = "48826366"


def main() -> int:
    with CSV_PATH.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        row = next((candidate for candidate in reader if (candidate.get("Lot #") or "").strip() == LOT), None)
    if row is None:
        print(json.dumps({"found": False, "lot": LOT}))
        return 0

    catalog_url = (row.get("Image URL") or "").strip()
    if not catalog_url:
        print(json.dumps({"found": True, "catalog_present": False}))
        return 0

    try:
        response = requests.get(catalog_url, headers={"Accept": "application/json"}, timeout=20)
        response.raise_for_status()
        payload = response.json()
    except requests.RequestException as exc:
        print(json.dumps({"found": True, "catalog_present": True, "request_error": type(exc).__name__}))
        return 0
    except ValueError:
        print(json.dumps({"found": True, "catalog_present": True, "response_json": False, "content_type": response.headers.get("content-type", "")[:80]}))
        return 0

    links = []
    for image in payload.get("lotImages", []) if isinstance(payload, dict) else []:
        if not isinstance(image, dict):
            continue
        raw_links = image.get("link", [])
        if isinstance(raw_links, dict):
            raw_links = [raw_links]
        for link in raw_links if isinstance(raw_links, list) else []:
            if isinstance(link, dict):
                links.append(link)

    hosts = Counter()
    schemes = Counter()
    url_keys = Counter()
    with_query = 0
    non_absolute = 0
    for link in links:
        candidate = link.get("url")
        if not isinstance(candidate, str):
            url_keys["missing_or_non_string"] += 1
            continue
        from urllib.parse import urlparse
        parsed = urlparse(candidate)
        if not parsed.scheme or not parsed.netloc:
            non_absolute += 1
            continue
        hosts[parsed.hostname or ""] += 1
        schemes[parsed.scheme] += 1
        if parsed.query:
            with_query += 1
        url_keys["url"] += 1

    print(json.dumps({
        "found": True,
        "catalog_present": True,
        "top_level_keys": sorted(payload.keys()) if isinstance(payload, dict) else [],
        "lot_image_entries": len(payload.get("lotImages", [])) if isinstance(payload, dict) and isinstance(payload.get("lotImages"), list) else 0,
        "link_entries": len(links),
        "url_entries": url_keys["url"],
        "missing_url_entries": url_keys["missing_or_non_string"],
        "absolute_hosts": dict(hosts),
        "schemes": dict(schemes),
        "urls_with_query": with_query,
        "non_absolute_urls": non_absolute,
        "catalog_status": response.status_code,
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
