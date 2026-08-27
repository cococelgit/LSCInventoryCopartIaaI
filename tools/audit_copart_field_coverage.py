#!/usr/bin/env python3
"""Report non-empty coverage for selected Copart CSV columns without exposing vehicle values."""
from __future__ import annotations

import csv
import json
import sys
from collections import Counter
from pathlib import Path

FIELDS = [
    "VIN", "Seller Name", "Year", "Make", "Model Group", "Model Detail", "Trim",
    "Vehicle Type", "Body Style", "Color", "Damage Description", "Secondary Damage",
    "Sale Title State", "Sale Title Type", "Has Keys-Yes or No", "Lot Cond. Code",
    "Odometer", "Odometer Brand", "Est. Retail Value", "Repair cost", "Engine", "Drive",
    "Transmission", "Fuel Type", "Cylinders", "Runs/Drives", "Sale Status",
    "High Bid =non-vix,Sealed=Vix", "Buy-It-Now Price", "Location city", "Location state",
    "Location ZIP", "Location country", "Image Thumbnail", "Image URL", "AutoGrade",
    "Announcements", "Special Note", "Sale Light",
]


def normalized(value: str | None) -> str:
    return (value or "").strip()


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: audit_copart_field_coverage.py <csv-path>")
    path = Path(sys.argv[1])
    nonempty = Counter()
    total = 0
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        missing = [name for name in FIELDS if name not in (reader.fieldnames or [])]
        for row in reader:
            total += 1
            for name in FIELDS:
                if normalized(row.get(name)):
                    nonempty[name] += 1
    output = {
        "rows": total,
        "missing_headers": missing,
        "fields": [
            {"field": name, "nonempty": nonempty[name], "missing": total - nonempty[name], "coverage_pct": round((nonempty[name] / total * 100) if total else 0, 2)}
            for name in FIELDS
        ],
    }
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
