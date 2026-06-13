#!/usr/bin/env python3
"""
Snapshot current Hyperliquid account values for a large address list.

This is deliberately lighter than a full fill profile: it only calls
clearinghouseState per address, so we can quickly learn whether leaderboard
winners still have capital on Hyperliquid.
"""

from __future__ import annotations

import argparse
import csv
import json
import time
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


INFO_URL = "https://api.hyperliquid.xyz/info"


def dec(value: Any) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0"))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def pct(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


def normalize_address(value: str) -> str:
    address = value.strip().lower()
    if not address.startswith("0x") or len(address) != 42:
        raise ValueError(f"Invalid address: {value}")
    int(address[2:], 16)
    return address


def post_info(payload: dict[str, Any], max_retries: int = 8) -> Any:
    body = json.dumps(payload).encode("utf-8")
    for attempt in range(max_retries + 1):
        request = Request(
            INFO_URL,
            data=body,
            method="POST",
            headers={
                "Content-Type": "application/json",
                "Accept": "application/json",
                "User-Agent": "WhaleTracker/1.0",
            },
        )
        try:
            with urlopen(request, timeout=45) as response:
                return json.loads(response.read().decode("utf-8"))
        except HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            if exc.code == 429 and attempt < max_retries:
                wait = min(90, (2 ** attempt) * 5)
                print(f"HTTP 429; retry in {wait}s", flush=True)
                time.sleep(wait)
                continue
            raise RuntimeError(f"Hyperliquid HTTP {exc.code}: {detail}") from exc
        except URLError as exc:
            if attempt < max_retries:
                wait = min(60, (2 ** attempt) * 3)
                print(f"Network error; retry in {wait}s: {exc}", flush=True)
                time.sleep(wait)
                continue
            raise RuntimeError(f"Hyperliquid network error: {exc}") from exc
    raise RuntimeError("Hyperliquid request failed after retries.")


def load_addresses(path: Path, max_addresses: int) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    with path.open("r", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            row["address"] = normalize_address(row["address"])
            rows.append(row)
            if max_addresses and len(rows) >= max_addresses:
                break
    return rows


def snapshot_row(source: dict[str, str]) -> dict[str, str]:
    address = source["address"]
    state = post_info({"type": "clearinghouseState", "user": address})
    margin = state.get("marginSummary") or {}
    account_value = dec(margin.get("accountValue"))
    total_notional = dec(margin.get("totalNtlPos"))
    margin_used = dec(margin.get("totalMarginUsed"))
    positions = state.get("assetPositions") or []
    withdrawable = dec(state.get("withdrawable"))
    return {
        "rank": source.get("rank", ""),
        "address": address,
        "display_name": source.get("display_name", ""),
        "leaderboard_account_value_usd": source.get("account_value_usd", ""),
        "leaderboard_pnl_usd": source.get("pnl_usd", ""),
        "leaderboard_roi_pct": source.get("roi_pct", ""),
        "leaderboard_volume_usd": source.get("volume_usd", ""),
        "current_account_value_usd": money(account_value),
        "current_withdrawable_usd": money(withdrawable),
        "current_position_notional_usd": money(total_notional),
        "current_margin_used_usd": money(margin_used),
        "active_position_count": str(len(positions)),
        "has_capital": "yes" if account_value > 0 else "no",
        "has_active_position": "yes" if len(positions) > 0 else "no",
    }


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def summarize(rows: list[dict[str, str]]) -> dict[str, str]:
    total = len(rows)
    thresholds = [Decimal("1"), Decimal("100"), Decimal("1000"), Decimal("10000"), Decimal("30000"), Decimal("100000")]
    summary: dict[str, str] = {"total": str(total)}
    if total == 0:
        return summary
    active_positions = sum(1 for row in rows if row["has_active_position"] == "yes")
    with_capital = sum(1 for row in rows if row["has_capital"] == "yes")
    summary["with_capital"] = str(with_capital)
    summary["with_capital_pct"] = pct(Decimal(with_capital) / Decimal(total) * Decimal("100"))
    summary["with_active_position"] = str(active_positions)
    summary["with_active_position_pct"] = pct(Decimal(active_positions) / Decimal(total) * Decimal("100"))
    for threshold in thresholds:
        count = sum(1 for row in rows if dec(row["current_account_value_usd"]) >= threshold)
        key = f"account_value_gte_{threshold:f}_usd"
        summary[key] = str(count)
        summary[f"{key}_pct"] = pct(Decimal(count) / Decimal(total) * Decimal("100"))
    total_value = sum((dec(row["current_account_value_usd"]) for row in rows), Decimal("0"))
    summary["total_current_account_value_usd"] = money(total_value)
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Snapshot current Hyperliquid account values.")
    parser.add_argument("--leaderboard-csv", required=True)
    parser.add_argument("--max-addresses", type=int, default=0)
    parser.add_argument("--delay", type=float, default=0.08, help="Delay between accounts. Default: 0.08s.")
    parser.add_argument("--progress-every", type=int, default=100)
    parser.add_argument("--out-dir", default="data/reports/hyperliquid_account_snapshots")
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    source_rows = load_addresses(Path(args.leaderboard_csv), args.max_addresses)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    out_dir = root / args.out_dir / timestamp
    out_dir.mkdir(parents=True, exist_ok=True)

    rows: list[dict[str, str]] = []
    errors: list[dict[str, str]] = []
    start = time.time()
    for index, source in enumerate(source_rows, start=1):
        try:
            rows.append(snapshot_row(source))
        except Exception as exc:
            errors.append({"address": source.get("address", ""), "error": str(exc)})
            print(f"{source.get('address')}: ERROR {exc}", flush=True)
        if args.delay > 0:
            time.sleep(args.delay)
        if index == 1 or index % args.progress_every == 0 or index == len(source_rows):
            elapsed = time.time() - start
            print(f"Progress {index}/{len(source_rows)} ({index / len(source_rows) * 100:.1f}%) elapsed {elapsed:.0f}s", flush=True)
            write_csv(out_dir / "account_snapshot_partial.csv", rows)
            if errors:
                write_csv(out_dir / "errors_partial.csv", errors)

    write_csv(out_dir / "account_snapshot.csv", rows)
    if errors:
        write_csv(out_dir / "errors.csv", errors)
    summary = summarize(rows)
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"Report directory: {out_dir}")
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
