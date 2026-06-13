#!/usr/bin/env python3
"""Rebuild trader_summaries.csv/json from completed profile directories."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
from typing import Any


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    fieldnames: list[str] = []
    for row in rows:
        for key in row:
            if key not in fieldnames:
                fieldnames.append(key)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def main() -> int:
    parser = argparse.ArgumentParser(description="Rebuild Hyperliquid profile run summary.")
    parser.add_argument("run_dir", help="Profile run directory.")
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    rows: list[dict[str, str]] = []
    summaries: list[dict[str, Any]] = []
    for summary_path in sorted(run_dir.glob("0x*/summary.json")):
        data = json.loads(summary_path.read_text(encoding="utf-8"))
        summaries.append(data)
        rows.append({key: str(value) for key, value in data.items() if not isinstance(value, (list, dict))})

    write_csv(run_dir / "trader_summaries.csv", rows)
    (run_dir / "trader_summaries.json").write_text(json.dumps(summaries, indent=2), encoding="utf-8")
    print(f"Rebuilt {len(rows)} summaries in {run_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
