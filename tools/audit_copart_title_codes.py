#!/usr/bin/env python3
"""Count Copart title codes from a CSV without printing vehicle details."""
from __future__ import annotations

import csv
import json
import sys
from collections import Counter
from pathlib import Path


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: audit_copart_title_codes.py <csv-path>")
    counts: Counter[str] = Counter()
    total = 0
    blank = 0
    with Path(sys.argv[1]).open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            total += 1
            code = (row.get("Sale Title Type") or "").strip().upper()
            if code:
                counts[code] += 1
            else:
                blank += 1
    print(json.dumps({"rows": total, "blank": blank, "codes": [{"code": code, "count": count} for code, count in counts.most_common()]}, indent=2))


if __name__ == "__main__":
    main()
