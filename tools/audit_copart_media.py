#!/usr/bin/env python3
"""Summarize Copart media URL shapes without emitting vehicle or query-secret data."""
from __future__ import annotations

import csv
import json
import sys
from collections import Counter
from pathlib import Path
from urllib.parse import parse_qsl, urlparse


def describe_url(value: str | None) -> dict[str, object] | None:
    if not value:
        return None
    parsed = urlparse(value.strip())
    return {
        "scheme": parsed.scheme,
        "host": parsed.hostname,
        "path_pattern": "/".join("{id}" if segment.isdigit() else segment for segment in parsed.path.split("/")),
        "query_keys": sorted({key for key, _ in parse_qsl(parsed.query, keep_blank_values=True)}),
        "has_query": bool(parsed.query),
    }


def main(path_text: str) -> int:
    path = Path(path_text)
    total = 0
    thumbnail = 0
    image_url = 0
    identical = 0
    forms: Counter[str] = Counter()
    query_keys: Counter[str] = Counter()

    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            total += 1
            thumb = row.get("Image Thumbnail", "").strip()
            image = row.get("Image URL", "").strip()
            thumbnail += int(bool(thumb))
            image_url += int(bool(image))
            identical += int(bool(thumb and image and thumb == image))
            for value in (thumb, image):
                item = describe_url(value)
                if not item:
                    continue
                forms[json.dumps({key: item[key] for key in ("scheme", "host", "path_pattern", "has_query")}, sort_keys=True)] += 1
                query_keys.update(item["query_keys"])

    print(json.dumps({
        "rows": total,
        "rows_with_thumbnail": thumbnail,
        "rows_with_image_url": image_url,
        "rows_with_identical_thumbnail_and_image_url": identical,
        "media_url_forms": [{"shape": json.loads(shape), "count": count} for shape, count in forms.most_common(12)],
        "query_parameter_names": [key for key, _ in query_keys.most_common()],
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1]))
