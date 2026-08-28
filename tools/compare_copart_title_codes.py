#!/usr/bin/env python3
"""Compare title codes in the user-provided Copart reference PDF text with a Copart CSV."""
from __future__ import annotations

import csv
import json
import re
import sys
from collections import Counter
from pathlib import Path

ENTRY = re.compile(r"^\s*\f?([A-Z0-9]{1,3})\s{2,}(.+?)\s{2,}(.+?)\s{2,}(si|no)(?:\s+.*)?$", re.IGNORECASE)


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: compare_copart_title_codes.py <reference.txt> <snapshot.csv>")
    reference: dict[str, dict[str, str]] = {}
    for line in Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace").splitlines():
        match = ENTRY.match(line)
        if not match:
            continue
        code, english, spanish, process = match.groups()
        reference[code.upper()] = {"english": english.strip(), "spanish": spanish.strip(), "source_process": process.lower()}

    counts: Counter[str] = Counter()
    with Path(sys.argv[2]).open("r", encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            code = (row.get("Sale Title Type") or "").strip().upper()
            if code:
                counts[code] += 1

    matched = sum(count for code, count in counts.items() if code in reference)
    unmatched = [{"code": code, "count": count} for code, count in counts.most_common() if code not in reference]
    result = {
        "reference_codes": len(reference),
        "snapshot_distinct_codes": len(counts),
        "snapshot_rows_with_code": sum(counts.values()),
        "matched_rows": matched,
        "matched_pct": round(matched / sum(counts.values()) * 100, 2) if counts else 0,
        "unmatched_codes": unmatched,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
