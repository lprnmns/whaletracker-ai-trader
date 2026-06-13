#!/usr/bin/env python3
"""
Export Hyperliquid leaderboard rows with full trader addresses.

The web UI renders shortened addresses, but its public data endpoint includes
the full ethAddress for every leaderboard row. This script downloads that
dataset and writes ranked CSV/JSON files for research and follow-up PnL checks.
"""

from __future__ import annotations

import argparse
import csv
import json
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any
from urllib.request import Request, urlopen


LEADERBOARD_URL = "https://stats-data.hyperliquid.xyz/Mainnet/leaderboard"
WINDOWS = {
    "1d": "day",
    "day": "day",
    "7d": "week",
    "week": "week",
    "30d": "month",
    "month": "month",
    "all": "allTime",
    "alltime": "allTime",
    "allTime": "allTime",
}


def dec(value: Any) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0"))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def pct(value: Decimal) -> str:
    return f"{(value * Decimal('100')).quantize(Decimal('0.01'))}"


def fetch_json(url: str) -> dict[str, Any]:
    request = Request(url, headers={"Accept": "application/json", "User-Agent": "WhaleTracker/1.0"})
    with urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def parse_rows(payload: dict[str, Any], window: str) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for raw in payload.get("leaderboardRows", []):
        performances = dict(raw.get("windowPerformances") or [])
        perf = performances.get(window)
        if not perf:
            continue

        address = str(raw.get("ethAddress") or "").lower()
        if not address.startswith("0x") or len(address) != 42:
            continue

        account_value = dec(raw.get("accountValue"))
        pnl = dec(perf.get("pnl"))
        roi = dec(perf.get("roi"))
        volume = dec(perf.get("vlm"))
        rows.append(
            {
                "address": address,
                "display_name": raw.get("displayName") or "",
                "account_value_usd": account_value,
                "pnl_usd": pnl,
                "roi": roi,
                "volume_usd": volume,
                "prize": dec(raw.get("prize")),
            }
        )
    return rows


def sort_rows(rows: list[dict[str, Any]], sort: str) -> list[dict[str, Any]]:
    key_map = {
        "roi": "roi",
        "pnl": "pnl_usd",
        "volume": "volume_usd",
        "vlm": "volume_usd",
        "account": "account_value_usd",
        "account_value": "account_value_usd",
    }
    key = key_map[sort]
    return sorted(rows, key=lambda row: (row[key], row["pnl_usd"], row["account_value_usd"]), reverse=True)


def to_csv_rows(rows: list[dict[str, Any]], window: str, sort: str, start_rank: int) -> list[dict[str, str]]:
    output: list[dict[str, str]] = []
    for index, row in enumerate(rows, start=start_rank):
        output.append(
            {
                "rank": str(index),
                "window": window,
                "sort": sort,
                "address": row["address"],
                "display_name": row["display_name"],
                "account_value_usd": money(row["account_value_usd"]),
                "pnl_usd": money(row["pnl_usd"]),
                "roi_pct": pct(row["roi"]),
                "volume_usd": money(row["volume_usd"]),
                "prize": money(row["prize"]),
                "hyperliquid_url": f"https://app.hyperliquid.xyz/explorer/address/{row['address']}",
            }
        )
    return output


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def main() -> int:
    parser = argparse.ArgumentParser(description="Export Hyperliquid leaderboard full addresses.")
    parser.add_argument("--window", default="30d", choices=sorted(WINDOWS), help="Leaderboard window. Default: 30d.")
    parser.add_argument("--sort", default="roi", choices=["roi", "pnl", "volume", "vlm", "account", "account_value"])
    parser.add_argument("--offset", type=int, default=0, help="Rows to skip after sorting. Default: 0.")
    parser.add_argument("--limit", type=int, default=100, help="Rows to export after offset. Default: 100.")
    parser.add_argument("--min-account-usd", default="0", help="Minimum account value. Default: 0.")
    parser.add_argument("--min-pnl-usd", default="0", help="Minimum PnL. Default: 0.")
    parser.add_argument("--min-volume-usd", default="0", help="Minimum volume. Default: 0.")
    parser.add_argument("--out-dir", default="data/reports/hyperliquid_leaderboard")
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    window = WINDOWS[args.window]
    payload = fetch_json(LEADERBOARD_URL)
    rows = parse_rows(payload, window)
    rows = [
        row
        for row in rows
        if row["account_value_usd"] >= dec(args.min_account_usd)
        and row["pnl_usd"] >= dec(args.min_pnl_usd)
        and row["volume_usd"] >= dec(args.min_volume_usd)
    ]
    ranked = sort_rows(rows, args.sort)
    offset = max(0, args.offset)
    limit = max(1, args.limit)
    selected = ranked[offset : offset + limit]
    csv_rows = to_csv_rows(selected, window, args.sort, offset + 1)

    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    out_dir = root / args.out_dir / timestamp
    write_csv(out_dir / "leaderboard.csv", csv_rows)
    (out_dir / "leaderboard.json").write_text(json.dumps(csv_rows, indent=2), encoding="utf-8")
    (out_dir / "raw.json").write_text(json.dumps(payload, indent=2), encoding="utf-8")

    print(f"Fetched rows: {len(payload.get('leaderboardRows', []))}")
    print(f"Filtered rows: {len(rows)}")
    print(f"Report directory: {out_dir}")
    for row in csv_rows[: min(25, len(csv_rows))]:
        label = row["display_name"] or row["address"]
        print(
            f"#{row['rank']} {label} {row['address']} "
            f"account ${row['account_value_usd']} pnl ${row['pnl_usd']} "
            f"roi {row['roi_pct']}% volume ${row['volume_usd']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
