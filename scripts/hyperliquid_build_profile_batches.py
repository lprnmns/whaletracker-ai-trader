#!/usr/bin/env python3
"""Build resumable Hyperliquid profile batches from an account snapshot CSV."""

from __future__ import annotations

import argparse
import csv
import json
from decimal import Decimal, InvalidOperation
from pathlib import Path


METRICS: dict[str, tuple[str, Decimal]] = {
    "pnl_rank": ("leaderboard_pnl_usd", Decimal("0.35")),
    "account_rank": ("current_account_value_usd", Decimal("0.25")),
    "roi_rank": ("leaderboard_roi_pct", Decimal("0.25")),
    "volume_rank": ("leaderboard_volume_usd", Decimal("0.15")),
}


def dec(value: object) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0").replace(",", ""))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def is_yes(value: object) -> bool:
    return str(value or "").strip().lower() in {"yes", "true", "1"}


def normalize_address(value: str) -> str:
    address = value.strip().lower()
    if not address.startswith("0x") or len(address) != 42:
        raise ValueError(f"Invalid address: {value}")
    int(address[2:], 16)
    return address


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    fieldnames: list[str] = []
    for row in rows:
        for key in row:
            if key not in fieldnames:
                fieldnames.append(key)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def add_rank_columns(rows: list[dict[str, str]]) -> None:
    total = Decimal(max(1, len(rows)))
    rank_lookup: dict[str, dict[str, int]] = {rank_name: {} for rank_name in METRICS}
    for rank_name, (field, _) in METRICS.items():
        ranked = sorted(rows, key=lambda row: dec(row.get(field)), reverse=True)
        for rank, row in enumerate(ranked, start=1):
            rank_lookup[rank_name][normalize_address(row["address"])] = rank

    for row in rows:
        address = normalize_address(row["address"])
        score = Decimal("0")
        for rank_name, (_, weight) in METRICS.items():
            rank = rank_lookup[rank_name][address]
            row[rank_name] = str(rank)
            score += ((total - Decimal(rank) + Decimal("1")) / total) * weight * Decimal("100")

        active_position_count = dec(row.get("active_position_count"))
        if active_position_count > 0:
            score += min(active_position_count, Decimal("5"))
        row["profile_priority_score"] = f"{score.quantize(Decimal('0.0001'))}"


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Hyperliquid profile address batches.")
    parser.add_argument("--snapshot-csv", required=True, help="account_snapshot.csv from hyperliquid_account_snapshot.py")
    parser.add_argument("--out-dir", default="", help="Output directory. Defaults to snapshot_dir/profile_batches.")
    parser.add_argument("--min-current-account-usd", default="30000")
    parser.add_argument("--min-leaderboard-pnl-usd", default="0")
    parser.add_argument("--min-leaderboard-volume-usd", default="0")
    parser.add_argument("--require-active-position", action="store_true")
    parser.add_argument("--batch-size", type=int, default=100)
    parser.add_argument("--max-addresses", type=int, default=0)
    parser.add_argument("--prefix", default="active_30k_composite")
    args = parser.parse_args()

    snapshot = Path(args.snapshot_csv)
    rows: list[dict[str, str]] = []
    with snapshot.open("r", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if dec(row.get("current_account_value_usd")) < dec(args.min_current_account_usd):
                continue
            if dec(row.get("leaderboard_pnl_usd")) < dec(args.min_leaderboard_pnl_usd):
                continue
            if dec(row.get("leaderboard_volume_usd")) < dec(args.min_leaderboard_volume_usd):
                continue
            if args.require_active_position and not is_yes(row.get("has_active_position")):
                continue
            row["address"] = normalize_address(row["address"])
            rows.append(row)

    add_rank_columns(rows)
    rows.sort(key=lambda row: dec(row.get("profile_priority_score")), reverse=True)
    if args.max_addresses:
        rows = rows[: args.max_addresses]

    out_dir = Path(args.out_dir) if args.out_dir else snapshot.parent / "profile_batches"
    out_dir.mkdir(parents=True, exist_ok=True)

    all_csv = out_dir / f"{args.prefix}.csv"
    all_txt = out_dir / f"{args.prefix}_addresses.txt"
    write_csv(all_csv, rows)
    all_txt.write_text("\n".join(row["address"] for row in rows) + ("\n" if rows else ""), encoding="utf-8")

    batches: list[dict[str, object]] = []
    batch_size = max(1, args.batch_size)
    for index in range(0, len(rows), batch_size):
        batch_number = index // batch_size + 1
        batch_rows = rows[index : index + batch_size]
        batch_prefix = f"{args.prefix}_batch_{batch_number:03d}"
        batch_csv = out_dir / f"{batch_prefix}.csv"
        batch_txt = out_dir / f"{batch_prefix}_addresses.txt"
        write_csv(batch_csv, batch_rows)
        batch_txt.write_text("\n".join(row["address"] for row in batch_rows) + "\n", encoding="utf-8")
        batches.append(
            {
                "batch": batch_number,
                "count": len(batch_rows),
                "csv": str(batch_csv),
                "addresses": str(batch_txt),
            }
        )

    manifest = {
        "snapshot_csv": str(snapshot),
        "candidate_count": len(rows),
        "batch_size": batch_size,
        "batches": batches,
        "filters": {
            "min_current_account_usd": args.min_current_account_usd,
            "min_leaderboard_pnl_usd": args.min_leaderboard_pnl_usd,
            "min_leaderboard_volume_usd": args.min_leaderboard_volume_usd,
            "require_active_position": args.require_active_position,
        },
    }
    (out_dir / f"{args.prefix}_manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print(f"Candidates: {len(rows)}")
    print(f"All CSV: {all_csv}")
    print(f"All addresses: {all_txt}")
    print(f"Batches: {len(batches)} x {batch_size}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
