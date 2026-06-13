#!/usr/bin/env python3
"""Select active Hyperliquid traders from an account snapshot CSV."""

from __future__ import annotations

import argparse
import csv
from decimal import Decimal, InvalidOperation
from pathlib import Path


def dec(value: object) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0").replace(",", ""))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def is_yes(value: object) -> bool:
    return str(value or "").strip().lower() in {"yes", "true", "1"}


def main() -> int:
    parser = argparse.ArgumentParser(description="Select active Hyperliquid profile candidates.")
    parser.add_argument("--snapshot-csv", required=True, help="account_snapshot.csv from hyperliquid_account_snapshot.py")
    parser.add_argument("--min-current-account-usd", default="30000")
    parser.add_argument("--require-active-position", action="store_true")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--sort", choices=["roi", "pnl", "account", "volume"], default="roi")
    parser.add_argument("--out-prefix", default="")
    args = parser.parse_args()

    rows: list[dict[str, str]] = []
    with Path(args.snapshot_csv).open("r", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if dec(row.get("current_account_value_usd")) < dec(args.min_current_account_usd):
                continue
            if args.require_active_position and not is_yes(row.get("has_active_position")):
                continue
            rows.append(row)

    sort_key = {
        "roi": "leaderboard_roi_pct",
        "pnl": "leaderboard_pnl_usd",
        "account": "current_account_value_usd",
        "volume": "leaderboard_volume_usd",
    }[args.sort]
    rows.sort(key=lambda row: dec(row.get(sort_key)), reverse=True)
    if args.limit:
        rows = rows[: args.limit]

    source = Path(args.snapshot_csv)
    prefix = args.out_prefix or f"selected_active_{args.sort}_{len(rows)}"
    out_csv = source.parent / f"{prefix}.csv"
    out_txt = source.parent / f"{prefix}_addresses.txt"

    if rows:
        with out_csv.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
            writer.writeheader()
            writer.writerows(rows)
    else:
        out_csv.write_text("", encoding="utf-8")
    out_txt.write_text("\n".join(row["address"].lower() for row in rows) + ("\n" if rows else ""), encoding="utf-8")

    print(f"Selected {len(rows)} traders")
    print(f"CSV: {out_csv}")
    print(f"Addresses: {out_txt}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
