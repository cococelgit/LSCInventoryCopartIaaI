#!/usr/bin/env python3
"""Inspect the structure of one Copart media response without emitting lot, VIN, URLs, or query values."""
from __future__ import annotations

import csv
import json
import sys
from pathlib import Path
from urllib.parse import urlparse

import requests


def shape(value: object, depth: int = 0) -> object:
    if depth > 5:
        return type(value).__name__
    if isinstance(value, dict):
        return {str(key): shape(item, depth + 1) for key, item in value.items()}
    if isinstance(value, list):
        return [shape(value[0], depth + 1)] if value else []
    if isinstance(value, str):
        parsed = urlparse(value)
        if parsed.scheme and parsed.hostname:
            return {"url": {"host": parsed.hostname, "path_suffix": parsed.path.rsplit("/", 1)[-1][-32:], "has_query": bool(parsed.query)}}
        return "string"
    return type(value).__name__


def main(path_text: str) -> int:
    with Path(path_text).open("r", encoding="utf-8-sig", newline="") as handle:
        row = next(item for item in csv.DictReader(handle) if item.get("Image URL", "").strip().startswith(("http://", "https://")))
    response = requests.get(row["Image URL"].strip(), timeout=20, headers={"Accept": "application/json, image/*;q=0.9"})
    output: dict[str, object] = {
        "status": response.status_code,
        "content_type": response.headers.get("content-type", ""),
        "content_length": response.headers.get("content-length", ""),
    }
    if "json" in response.headers.get("content-type", ""):
        try:
            body = response.json()
            output["body_shape"] = shape(body)
            output["top_level_type"] = type(body).__name__
            output["top_level_length"] = len(body) if isinstance(body, (dict, list)) else None
        except ValueError:
            output["json_parse_error"] = True
    print(json.dumps(output, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1]))
