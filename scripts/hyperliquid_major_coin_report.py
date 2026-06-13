#!/usr/bin/env python3
"""Aggregate Hyperliquid profile reports by copyable major coins."""

from __future__ import annotations

import argparse
import csv
from collections import defaultdict
from decimal import Decimal, InvalidOperation
from pathlib import Path


MAJORS = {
    "BTC",
    "ETH",
    "SOL",
    "AVAX",
    "LINK",
    "HYPE",
    "DOGE",
    "BNB",
    "XRP",
    "WLD",
    "ARB",
    "OP",
    "SUI",
    "TIA",
    "AAVE",
    "PENDLE",
    "UNI",
}


def dec(value: object) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0").replace(",", ""))
    except (InvalidOperation, ValueError):
        return Decimal("0")


def money(value: Decimal) -> str:
    return f"{value.quantize(Decimal('0.01'))}"


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
    parser = argparse.ArgumentParser(description="Build major-coin PnL reports from Hyperliquid profiles.")
    parser.add_argument("run_dir", help="Hyperliquid profile run directory.")
    parser.add_argument("--out-dir", help="Output directory. Default: run_dir/major_coin_report")
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    out_dir = Path(args.out_dir) if args.out_dir else run_dir / "major_coin_report"
    coin_stats: dict[str, dict[str, object]] = defaultdict(
        lambda: {
            "net": Decimal("0"),
            "fees": Decimal("0"),
            "positions": 0,
            "wins": 0,
            "traders": set(),
        }
    )
    trader_stats: dict[str, dict[str, object]] = defaultdict(
        lambda: {
            "major_net": Decimal("0"),
            "positions": 0,
            "wins": 0,
            "coins": defaultdict(Decimal),
        }
    )
    positions: list[dict[str, str]] = []

    for csv_path in sorted(run_dir.glob("0x*/closed_positions.csv")):
        address = csv_path.parent.name
        with csv_path.open("r", encoding="utf-8") as handle:
            for row in csv.DictReader(handle):
                coin = row.get("coin", "")
                if coin not in MAJORS:
                    continue
                net = dec(row.get("net_pnl_usd"))
                row = dict(row)
                row["address"] = address
                positions.append(row)

                coin_stats[coin]["net"] = dec(coin_stats[coin]["net"]) + net
                coin_stats[coin]["fees"] = dec(coin_stats[coin]["fees"]) + dec(row.get("fees_usd"))
                coin_stats[coin]["positions"] = int(coin_stats[coin]["positions"]) + 1
                coin_stats[coin]["wins"] = int(coin_stats[coin]["wins"]) + (1 if net > 0 else 0)
                coin_stats[coin]["traders"].add(address)  # type: ignore[union-attr]

                trader_stats[address]["major_net"] = dec(trader_stats[address]["major_net"]) + net
                trader_stats[address]["positions"] = int(trader_stats[address]["positions"]) + 1
                trader_stats[address]["wins"] = int(trader_stats[address]["wins"]) + (1 if net > 0 else 0)
                trader_stats[address]["coins"][coin] += net  # type: ignore[index]

    coin_rows = []
    for coin, item in coin_stats.items():
        positions_count = int(item["positions"])
        coin_rows.append(
            {
                "coin": coin,
                "net_pnl_usd": money(dec(item["net"])),
                "fees_usd": money(dec(item["fees"])),
                "closed_positions": str(positions_count),
                "winning_positions": str(item["wins"]),
                "win_rate_pct": money(Decimal(int(item["wins"])) / Decimal(positions_count) * Decimal("100") if positions_count else Decimal("0")),
                "trader_count": str(len(item["traders"])),
            }
        )
    coin_rows.sort(key=lambda row: dec(row["net_pnl_usd"]), reverse=True)

    trader_rows = []
    for address, item in trader_stats.items():
        positions_count = int(item["positions"])
        top_coins = sorted(item["coins"].items(), key=lambda pair: pair[1], reverse=True)  # type: ignore[union-attr]
        trader_rows.append(
            {
                "address": address,
                "major_net_pnl_usd": money(dec(item["major_net"])),
                "closed_positions": str(positions_count),
                "winning_positions": str(item["wins"]),
                "win_rate_pct": money(Decimal(int(item["wins"])) / Decimal(positions_count) * Decimal("100") if positions_count else Decimal("0")),
                "top_coins": "; ".join(f"{coin}:{money(value)}" for coin, value in top_coins[:6]),
            }
        )
    trader_rows.sort(key=lambda row: dec(row["major_net_pnl_usd"]), reverse=True)

    positions.sort(key=lambda row: dec(row.get("net_pnl_usd")), reverse=True)
    write_csv(out_dir / "major_coin_summary.csv", coin_rows)
    write_csv(out_dir / "major_trader_summary.csv", trader_rows)
    write_csv(out_dir / "major_closed_positions.csv", positions)
    print(f"Major coin report: {out_dir}")
    print(f"coins={len(coin_rows)} traders={len(trader_rows)} positions={len(positions)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
